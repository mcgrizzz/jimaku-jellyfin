using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Jimaku.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jimaku;

/// <summary>
/// Plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Server paths.</param>
    /// <param name="xmlSerializer">Configuration serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Jimaku";

    /// <summary>
    /// Gets the plugin ID. Must match the <c>guid</c> in <c>build.yaml</c>, or the server will
    /// refuse to associate the packaged manifest with this assembly.
    /// </summary>
    public override Guid Id => Guid.Parse("9f1c2a3e-6d54-4b07-8e21-5c9a7d3b1f80");

    /// <inheritdoc />
    public override string Description =>
        "Finds Japanese subtitles on Jimaku, verifies them against your media's actual timing, and attaches them as external sidecars.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "jimaku",
            DisplayName = "Jimaku",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        },
        new PluginPageInfo
        {
            Name = "jimakujs",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.js",
                GetType().Namespace),
        },
    ];
}
