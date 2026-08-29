using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.Jimaku.Providers;
using Jellyfin.Plugin.Jimaku.Jimaku;
using Jellyfin.Plugin.Jimaku.Matching;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jimaku;

/// <summary>
/// Registers the plugin's services with the server's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        serviceCollection.AddHttpClient(nameof(Jimaku), client =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Jellyfin.Plugin.Jimaku/" + (Plugin.Instance?.Version?.ToString() ?? "1.0"));
        });

        // One shared rate limiter across the whole plugin: Jimaku's 25-per-minute budget is
        // per-IP, so an on-demand request and a running library sweep are spending the same
        // allowance and must coordinate.
        serviceCollection.AddSingleton<RateLimiter>(_ => new RateLimiter());

        serviceCollection.AddSingleton(provider => new JimakuApiClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(Jimaku)),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JimakuApiClient>>(),
            provider.GetRequiredService<RateLimiter>()));

        serviceCollection.AddSingleton(provider =>
        {
            var cache = new KometaMappingCache(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(Jimaku)),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KometaMappingCache>>());

            cache.CacheDirectory = DataFolder("cache");
            return cache;
        });

        serviceCollection.AddSingleton(provider =>
        {
            var store = new SyncHistoryStore(
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SyncHistoryStore>>());

            store.Directory = DataFolder("history");
            return store;
        });

        // The factory reads the model path from configuration on each use, so changing it in the
        // settings page takes effect without a server restart.
        serviceCollection.AddSingleton(provider =>
        {
            var store = new SeriesProfileStore(
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SeriesProfileStore>>());

            store.Directory = DataFolder(Path.Combine("history", "series"));
            return store;
        });

        // Resolves ISessionManager and IActivityManager on use, not here: this is reachable from
        // the subtitle provider's constructor, which the container builds mid-graph.
        serviceCollection.AddSingleton<SyncNotifier>();

        // Shared state: the scheduled task and the on-demand endpoint drive the same run, and the
        // progress view has to see it whichever started it.
        serviceCollection.AddSingleton<SweepProgress>();
        serviceCollection.AddSingleton<SweepRunner>();

        serviceCollection.AddSingleton<IVoiceActivityDetectorFactory, VoiceActivityDetectorFactory>();

        serviceCollection.AddSingleton<AnimeIdResolver>();
        serviceCollection.AddSingleton<FfmpegRunner>();
        serviceCollection.AddSingleton<EmbeddedSubtitleReferenceProvider>();
        serviceCollection.AddSingleton<AudioActivityReferenceProvider>();
        serviceCollection.AddSingleton<ReferenceTrackResolver>();
        serviceCollection.AddSingleton<SidecarWriter>();
        serviceCollection.AddSingleton<JimakuSyncService>();

        // Also expose the plugin as a subtitle provider, so every client gets a per-episode
        // "search subtitles" entry point rather than only the plugin's own settings page.
        serviceCollection.AddSingleton<ISubtitleProvider, JimakuSubtitleProvider>();
    }

    private static string DataFolder(string name)
    {
        var root = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
