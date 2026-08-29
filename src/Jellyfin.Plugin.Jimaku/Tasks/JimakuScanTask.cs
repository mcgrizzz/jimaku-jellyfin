using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Sync;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Tasks;

/// <summary>
/// Sweeps the library for episodes with no Japanese subtitles and tries to supply them.
/// </summary>
/// <remarks>
/// Deliberately more conservative than the on-demand action. Nobody is watching a scheduled task, so
/// a wrong subtitle written at 3am is discovered days later, attached to an episode the user has no
/// reason to suspect. Differing-cut correction is therefore off by default here even though it is on
/// for interactive use, and nothing this task chooses is allowed to teach the series preference.
/// </remarks>
public class JimakuScanTask(
    SweepRunner runner,
    ILocalizationManager localization,
    ILogger<JimakuScanTask> logger) : IScheduledTask, IConfigurableScheduledTask
{
    private const string BaseName = "Fetch Japanese subtitles from Jimaku";

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately not constant. Jellyfin's Scheduled Tasks view renders a task's name and a
    /// percentage and nothing else - not its description - so the name is the only place a running
    /// sweep can say which episode it is on. It reverts the moment the run ends.
    /// </remarks>
    public string Name => runner.Progress.DescribeFor(BaseName);

    /// <inheritdoc />
    public string Key => "JimakuSubtitleScan";

    /// <inheritdoc />
    public string Description =>
        "Finds episodes with no Japanese subtitle track, looks for one on Jimaku, verifies its timing against the media, and attaches it as an external sidecar.";

    /// <inheritdoc />
    public string Category => "Subtitles";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => Plugin.Instance?.Configuration.EnableScheduledTask ?? false;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        },
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            logger.LogWarning("No Jimaku API key is configured; skipping the subtitle sweep.");
            progress.Report(100);
            return;
        }

        // The library filter is silently ignored when the language cannot be resolved, which would
        // return every episode in the library rather than none. Fail loudly instead.
        if (localization.FindLanguageInfo(configuration.LanguageTag) is null)
        {
            logger.LogError(
                "'{Tag}' is not a language Jellyfin recognises, so the 'missing subtitles' filter would match everything. Set a valid tag such as 'jpn'.",
                configuration.LanguageTag);
            progress.Report(100);
            return;
        }

        var ancestors = configuration.LibraryIds
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();

        if (ancestors.Count == 0)
        {
            logger.LogInformation(
                "No libraries are selected, so the sweep covers every library. Narrow this in the plugin settings if the server holds non-anime content.");
        }

        var scope = new SweepScope
        {
            AncestorIds = ancestors,
            OnlyMissingSubtitles = true,
            AddedWithinDays = configuration.OnlySweepEpisodesAddedWithinDays,
            MaxEpisodes = configuration.MaxEpisodesPerRun,
            RespectHistory = true,
            Label = ancestors.Count == 0 ? "every library" : $"{ancestors.Count} selected librar(y/ies)",
        };

        var options = new SyncOptions
        {
            AllowPiecewise = configuration.AllowPiecewiseScheduled,
            AllowAudioFallback = configuration.EnableAudioFallback,
        };

        // Linked so that cancelling from the Scheduled Tasks page and cancelling from the plugin's
        // own page both reach the same run.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        await runner.RunAsync(scope, options, progress, cancellation).ConfigureAwait(false);
    }
}
