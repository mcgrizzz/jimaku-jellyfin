using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Startup;

/// <summary>
/// Guards the plugin against re-introducing a startup dependency cycle.
/// </summary>
/// <remarks>
/// <para>
/// Version 1.0.0.0 stopped the server booting outright:
/// <c>IProviderManager -&gt; ISubtitleManager -&gt; IEnumerable&lt;ISubtitleProvider&gt; -&gt;
/// JimakuSubtitleProvider -&gt; JimakuSyncService -&gt; SidecarWriter -&gt; IProviderManager</c>.
/// </para>
/// <para>
/// Jellyfin constructs every registered subtitle provider while it is still building the provider
/// manager. So the invariant is: nothing reachable from the subtitle provider's constructor may
/// require <see cref="IProviderManager"/> or <see cref="ISubtitleManager"/>. Those must be resolved
/// on use instead. This walks the registered graph and asserts exactly that, rather than trying to
/// stand up Jellyfin's own container.
/// </para>
/// </remarks>
public class ServiceGraphTests(ITestOutputHelper output)
{
    private static readonly Type[] Forbidden = [typeof(IProviderManager), typeof(ISubtitleManager)];

    private static ServiceCollection BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpClient();

        services.AddSingleton(Mock.Of<ILibraryManager>());
        services.AddSingleton(Mock.Of<ILibraryMonitor>());
        services.AddSingleton(Mock.Of<IMediaSourceManager>());
        services.AddSingleton(Mock.Of<ISubtitleEncoder>());
        services.AddSingleton(Mock.Of<IMediaEncoder>());
        services.AddSingleton(Mock.Of<IFileSystem>());
        services.AddSingleton(Mock.Of<ILocalizationManager>());
        services.AddSingleton(Mock.Of<IProviderManager>());

        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        return services;
    }

    /// <summary>Maps a service type to the concrete type the container would build for it.</summary>
    private static Dictionary<Type, Type> ImplementationMap(ServiceCollection services)
    {
        var map = new Dictionary<Type, Type>();
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationType is not null)
            {
                map[descriptor.ServiceType] = descriptor.ImplementationType;
            }
        }

        return map;
    }

    /// <summary>
    /// Walks constructor parameters transitively through plugin-owned types, recording the path so
    /// a failure names the exact chain rather than just the offending type.
    /// </summary>
    private static bool Reaches(
        Type start,
        Dictionary<Type, Type> map,
        Type target,
        List<string> path,
        HashSet<Type>? seen = null)
    {
        seen ??= [];
        if (!seen.Add(start))
        {
            return false;
        }

        var constructor = start.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (constructor is null)
        {
            return false;
        }

        foreach (var parameter in constructor.GetParameters())
        {
            var type = parameter.ParameterType;

            if (type == target)
            {
                path.Add($"{start.Name} -> {type.Name}");
                return true;
            }

            var concrete = map.TryGetValue(type, out var impl) ? impl : type;

            // Only follow types this plugin owns; framework types are not ours to reason about.
            if (concrete.Namespace?.StartsWith("Jellyfin.Plugin.Jimaku", StringComparison.Ordinal) != true)
            {
                continue;
            }

            if (Reaches(concrete, map, target, path, seen))
            {
                path.Insert(0, $"{start.Name} -> {concrete.Name}");
                return true;
            }
        }

        return false;
    }

    public static TheoryData<string, Type> Roots => new()
    {
        // The provider itself: constructed mid-graph by SubtitleManager.
        { "ISubtitleProvider", typeof(IProviderManager) },
        { "ISubtitleProvider", typeof(ISubtitleManager) },

        // The pipeline the provider resolves. Checked separately because the provider defers that
        // resolution: without this, reverting SidecarWriter alone would slip through, since the
        // walk from the provider stops at IServiceProvider.
        { "JimakuSyncService", typeof(IProviderManager) },
        { "JimakuSyncService", typeof(ISubtitleManager) },
    };

    [Theory]
    [MemberData(nameof(Roots))]
    public void PipelineConstructors_DoNotTransitivelyRequire(string root, Type forbidden)
    {
        var services = BuildContainer();
        var map = ImplementationMap(services);

        var rootType = root == "ISubtitleProvider"
            ? services.First(d => d.ServiceType == typeof(ISubtitleProvider)).ImplementationType
            : typeof(global::Jellyfin.Plugin.Jimaku.Sync.JimakuSyncService);

        Assert.NotNull(rootType);

        var path = new List<string>();
        var reaches = Reaches(rootType!, map, forbidden, path);

        if (reaches)
        {
            output.WriteLine("offending chain: " + string.Join(" -> ", path));
        }

        Assert.False(
            reaches,
            $"{rootType!.Name} transitively requires {forbidden.Name} in its constructor. Jellyfin " +
            $"builds every ISubtitleProvider while constructing IProviderManager, so this closes a " +
            $"dependency cycle and the server refuses to start. Resolve it on use instead. " +
            $"Chain: {string.Join(" -> ", path)}");
    }

    [Fact]
    public void SidecarWriter_DoesNotInjectProviderManager()
    {
        // The specific regression: SidecarWriter needs IProviderManager to refresh an item, but
        // must fetch it at call time.
        var parameters = typeof(global::Jellyfin.Plugin.Jimaku.Sync.SidecarWriter)
            .GetConstructors()[0]
            .GetParameters()
            .Select(p => p.ParameterType);

        Assert.DoesNotContain(typeof(IProviderManager), parameters);
    }

    [Fact]
    public void EveryPluginServiceResolves()
    {
        using var provider = BuildContainer().BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        foreach (var type in new[]
                 {
                     typeof(ISubtitleProvider),
                     typeof(global::Jellyfin.Plugin.Jimaku.Sync.JimakuSyncService),
                     typeof(global::Jellyfin.Plugin.Jimaku.Sync.SidecarWriter),
                     typeof(global::Jellyfin.Plugin.Jimaku.Sync.SyncHistoryStore),
                     typeof(global::Jellyfin.Plugin.Jimaku.Jimaku.JimakuApiClient),
                     typeof(global::Jellyfin.Plugin.Jimaku.Jimaku.RateLimiter),
                     typeof(global::Jellyfin.Plugin.Jimaku.Matching.KometaMappingCache),
                     typeof(global::Jellyfin.Plugin.Jimaku.Matching.AnimeIdResolver),
                     typeof(global::Jellyfin.Plugin.Jimaku.Media.FfmpegRunner),
                     typeof(global::Jellyfin.Plugin.Jimaku.Media.ReferenceTrackResolver),
                     typeof(global::Jellyfin.Plugin.Jimaku.Media.IVoiceActivityDetectorFactory),
                 })
        {
            Assert.NotNull(provider.GetRequiredService(type));
        }
    }

    [Fact]
    public void SubtitleProvider_StillReachesTheSyncPipelineAtRuntime()
    {
        // Deferring resolution must not mean the pipeline is unreachable.
        using var provider = BuildContainer().BuildServiceProvider();

        var subtitleProvider = Assert.Single(provider.GetServices<ISubtitleProvider>());
        Assert.Equal("Jimaku", subtitleProvider.Name);
        Assert.NotNull(provider.GetRequiredService<global::Jellyfin.Plugin.Jimaku.Sync.JimakuSyncService>());
    }
}
