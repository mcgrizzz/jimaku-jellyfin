using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jimaku.Jimaku;
using Jellyfin.Plugin.Jimaku.Sync;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
/// for interactive use.
/// </remarks>
public class JimakuScanTask(
    ILibraryManager libraryManager,
    JimakuSyncService syncService,
    SyncHistoryStore history,
    ILocalizationManager localization,
    ILogger<JimakuScanTask> logger) : IScheduledTask, IConfigurableScheduledTask
{
    /// <inheritdoc />
    public string Name => "Fetch Japanese subtitles from Jimaku";

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

        var languageTag = configuration.LanguageTag;

        // The library filter is silently ignored when the language cannot be resolved, which would
        // return every episode in the library rather than none. Fail loudly instead.
        if (localization.FindLanguageInfo(languageTag) is null)
        {
            logger.LogError(
                "'{Tag}' is not a language Jellyfin recognises, so the 'missing subtitles' filter would match everything. Set a valid tag such as 'jpn'.",
                languageTag);
            progress.Report(100);
            return;
        }

        var episodes = FindEpisodes(configuration.LibraryIds, languageTag);
        if (episodes.Count == 0)
        {
            logger.LogInformation("No episodes are missing {Tag} subtitles.", languageTag);
            progress.Report(100);
            return;
        }

        logger.LogInformation("{Count} episode(s) are missing {Tag} subtitles.", episodes.Count, languageTag);

        var options = new SyncOptions
        {
            AllowPiecewise = configuration.AllowPiecewiseScheduled,
            AllowAudioFallback = configuration.EnableAudioFallback,
        };

        var applied = 0;
        var declined = 0;
        var skipped = 0;

        for (var i = 0; i < episodes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / episodes.Count);

            var episode = episodes[i];

            if (history.ShouldSkip(episode.Id, configuration.RetryDeclinedAfterDays, out var reason))
            {
                logger.LogDebug("Skipping {Name}: {Reason}.", episode.Name, reason);
                skipped++;
                continue;
            }

            try
            {
                var result = await syncService.SyncEpisodeAsync(episode, options, cancellationToken)
                    .ConfigureAwait(false);

                if (result.Applied)
                {
                    applied++;
                }
                else
                {
                    declined++;
                    logger.LogInformation("No subtitle for {Name}: {Message}", episode.Name, result.Message);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JimakuApiException ex) when (ex.IsAuthenticationFailure)
            {
                // Every remaining episode would fail the same way and burn the request budget.
                logger.LogError(ex, "Jimaku rejected the API key; stopping the sweep.");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Processing {Name} failed.", episode.Name);
                declined++;
            }
        }

        progress.Report(100);
        logger.LogInformation(
            "Jimaku sweep finished: {Applied} attached, {Declined} declined, {Skipped} skipped.",
            applied,
            declined,
            skipped);
    }

    private List<Episode> FindEpisodes(string[] libraryIds, string languageTag)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,

            // Matches on the three-letter code after normalization, so a stream tagged jpn is found
            // whether this is set to "ja", "jpn" or "Japanese".
            HasNoSubtitleTrackWithLanguage = languageTag,
            DtoOptions = new DtoOptions(false) { EnableImages = false },
        };

        var ancestors = libraryIds
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();

        if (ancestors.Length > 0)
        {
            query.AncestorIds = ancestors;
        }
        else
        {
            logger.LogInformation(
                "No libraries are selected, so the sweep covers every library. Narrow this in the plugin settings if the server holds non-anime content.");
        }

        return libraryManager.GetItemList(query)
            .OfType<Episode>()
            .Where(e => !string.IsNullOrEmpty(e.Path))
            .ToList();
    }
}
