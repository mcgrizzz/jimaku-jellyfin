using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Jimaku;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// What a sweep covers.
/// </summary>
public sealed class SweepScope
{
    /// <summary>Gets or sets the libraries, series or seasons to search under.</summary>
    public IReadOnlyList<Guid> AncestorIds { get; set; } = [];

    /// <summary>Gets or sets specific episodes to attempt, bypassing the search entirely.</summary>
    public IReadOnlyList<Guid> EpisodeIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether to consider only episodes with no Japanese track.</summary>
    public bool OnlyMissingSubtitles { get; set; } = true;

    /// <summary>Gets or sets a limit on how recently an episode must have been added. Zero means all.</summary>
    public int AddedWithinDays { get; set; }

    /// <summary>Gets or sets the most episodes to attempt. Zero means no limit.</summary>
    public int MaxEpisodes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to skip episodes already settled or recently declined.
    /// </summary>
    /// <remarks>
    /// True for the unattended sweep, where re-attempting settled episodes forever would spend the
    /// whole request budget on questions already answered. False when a person asked for a specific
    /// season: they asked, so the answer is to do it.
    /// </remarks>
    public bool RespectHistory { get; set; } = true;

    /// <summary>Gets or sets a description of the scope, for display.</summary>
    public string Label { get; set; } = "the library";
}

/// <summary>
/// Runs a sweep over a set of episodes, reporting as it goes.
/// </summary>
/// <remarks>
/// Shared by the scheduled task and the on-demand endpoint so that "fetch subtitles for this
/// season" and the nightly run cannot drift apart in behaviour. Only one sweep runs at a time:
/// Jimaku's budget is per-IP and shared, so two concurrent sweeps would simply take turns waiting
/// on the same limiter while making the progress reporting incoherent.
/// </remarks>
public sealed class SweepRunner(
    ILibraryManager libraryManager,
    JimakuSyncService syncService,
    SyncHistoryStore history,
    SweepProgress progress,
    ILogger<SweepRunner> logger)
{
    /// <summary>Gets the live progress of the current or last run.</summary>
    public SweepProgress Progress => progress;

    /// <summary>
    /// Finds the episodes a scope covers, newest first.
    /// </summary>
    /// <param name="scope">The scope.</param>
    /// <param name="languageTag">The language a subtitle must be missing to qualify.</param>
    /// <returns>The episodes.</returns>
    public List<Episode> FindEpisodes(SweepScope scope, string languageTag)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false) { EnableImages = false },
        };

        if (scope.EpisodeIds.Count > 0)
        {
            query.ItemIds = [.. scope.EpisodeIds];
        }
        else if (scope.AncestorIds.Count > 0)
        {
            query.AncestorIds = [.. scope.AncestorIds];
        }

        if (scope.OnlyMissingSubtitles)
        {
            // Matches on the three-letter code after normalization, so a stream tagged jpn is found
            // whether this is set to "ja", "jpn" or "Japanese".
            query.HasNoSubtitleTrackWithLanguage = languageTag;
        }

        var episodes = libraryManager.GetItemList(query)
            .OfType<Episode>()
            .Where(e => !string.IsNullOrEmpty(e.Path));

        if (scope.AddedWithinDays > 0)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(scope.AddedWithinDays);
            episodes = episodes.Where(e => e.DateCreated >= cutoff);
        }

        var ordered = scope.EpisodeIds.Count > 0 || scope.AncestorIds.Count > 0
            ? episodes.OrderBy(e => e.ParentIndexNumber ?? 0).ThenBy(e => e.IndexNumber ?? 0)
            : episodes.OrderByDescending(e => e.DateCreated);

        var list = ordered.ToList();

        if (scope.MaxEpisodes > 0 && list.Count > scope.MaxEpisodes)
        {
            list = list.Take(scope.MaxEpisodes).ToList();
        }

        return list;
    }

    /// <summary>
    /// Runs a sweep.
    /// </summary>
    /// <param name="scope">What to cover.</param>
    /// <param name="options">How each episode is attempted.</param>
    /// <param name="report">Optional progress sink, for the scheduled task's own percentage.</param>
    /// <param name="cancellation">The source that cancels the run; disposed by the caller.</param>
    /// <returns>A summary line.</returns>
    public async Task<string> RunAsync(
        SweepScope scope,
        SyncOptions options,
        IProgress<double>? report,
        CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cancellation);

        var configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var episodes = FindEpisodes(scope, configuration.LanguageTag);

        if (!progress.TryBegin(scope.Label, episodes.Count, cancellation))
        {
            return "A sweep is already running.";
        }

        try
        {
            if (episodes.Count == 0)
            {
                const string Nothing = "No episodes matched.";
                progress.Finish(Nothing);
                report?.Report(100);
                return Nothing;
            }

            logger.LogInformation(
                "Sweeping {Count} episode(s) in {Scope}.",
                episodes.Count,
                scope.Label);

            var token = cancellation.Token;

            for (var i = 0; i < episodes.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                report?.Report(100.0 * i / episodes.Count);

                var episode = episodes[i];
                var name = Describe(episode);

                if (scope.RespectHistory
                    && history.ShouldSkip(episode.Id, configuration.RetryDeclinedAfterDays, out var reason))
                {
                    logger.LogDebug("Skipping {Name}: {Reason}.", name, reason);
                    progress.RecordSkip();
                    continue;
                }

                progress.SetCurrent(name);

                try
                {
                    var result = await syncService.SyncEpisodeAsync(episode, options, token).ConfigureAwait(false);

                    progress.Record(new SweepOutcome(
                        episode.Id,
                        name,
                        result.Applied,
                        result.Verdict.ToString(),
                        result.FileName ?? string.Empty,
                        result.Message,
                        DateTimeOffset.UtcNow));

                    if (!result.Applied)
                    {
                        logger.LogInformation("No subtitle for {Name}: {Message}", name, result.Message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (JimakuApiException ex) when (ex.IsAuthenticationFailure)
                {
                    // Every remaining episode would fail the same way and burn the request budget.
                    logger.LogError(ex, "Jimaku rejected the API key; stopping the sweep.");
                    progress.Record(new SweepOutcome(
                        episode.Id, name, false, "Declined", string.Empty,
                        "Jimaku rejected the API key; the sweep stopped here.", DateTimeOffset.UtcNow));
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Processing {Name} failed.", name);
                    progress.Record(new SweepOutcome(
                        episode.Id, name, false, "Failed", string.Empty, ex.Message, DateTimeOffset.UtcNow));
                }
            }

            var stopped = cancellation.IsCancellationRequested ? " (stopped early)" : string.Empty;
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{progress.Applied} attached, {progress.Declined} declined, {progress.Skipped} skipped{stopped}");

            logger.LogInformation("Jimaku sweep finished: {Summary}.", summary);
            progress.Finish(summary);
            report?.Report(100);
            return summary;
        }
        catch (Exception ex)
        {
            progress.Finish("Failed: " + ex.Message);
            throw;
        }
    }

    private static string Describe(Episode episode)
    {
        var code = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}")
            : null;

        var parts = new[] { episode.SeriesName, code, episode.Name }
            .Where(p => !string.IsNullOrWhiteSpace(p));

        var joined = string.Join(" ", parts);
        return joined.Length > 0 ? joined : episode.Id.ToString("N", CultureInfo.InvariantCulture);
    }
}
