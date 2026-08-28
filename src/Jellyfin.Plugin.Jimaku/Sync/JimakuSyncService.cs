using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Jimaku;
using Jellyfin.Plugin.Jimaku.Jimaku.Models;
using Jellyfin.Plugin.Jimaku.Matching;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Timing;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// Finds, verifies, corrects and attaches a Japanese subtitle for one episode.
/// </summary>
/// <remarks>
/// The single entry point shared by the on-demand API, the subtitle provider and the scheduled
/// sweep, so all three behave identically and there is one place where the accept/decline rule
/// lives.
/// </remarks>
public sealed class JimakuSyncService(
    JimakuApiClient apiClient,
    AnimeIdResolver idResolver,
    ReferenceTrackResolver referenceResolver,
    SidecarWriter sidecarWriter,
    SyncHistoryStore history,
    ILogger<JimakuSyncService> logger)
{
    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Lists the candidate subtitles for an episode without downloading or analysing any of them.
    /// </summary>
    /// <remarks>
    /// Deliberately cheap: this backs the interactive candidate list and the subtitle provider's
    /// search, both of which must return promptly. Timing verification only happens on the file
    /// that is actually going to be used.
    /// </remarks>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates, best filename match first.</returns>
    public async Task<IReadOnlyList<SubtitleCandidate>> FindCandidatesAsync(
        Episode episode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var configuration = Configuration;
        if (string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            throw new JimakuApiException("No Jimaku API key is configured.");
        }

        var lookup = await idResolver.ResolveAsync(episode, cancellationToken).ConfigureAwait(false);
        if (!lookup.IsUsable)
        {
            logger.LogInformation("Cannot identify {Name}: {Reason}.", episode.Name, lookup.Description);
            return Array.Empty<SubtitleCandidate>();
        }

        logger.LogDebug("Identifying {Name} via {Description}.", episode.Name, lookup.Description);

        var entries = await SearchEntriesAsync(lookup, configuration.ApiKey, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return Array.Empty<SubtitleCandidate>();
        }

        var videoName = Path.GetFileName(episode.Path) ?? string.Empty;
        var candidates = new List<SubtitleCandidate>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var files = await GetFilesAsync(entry, lookup, configuration.ApiKey, cancellationToken)
                .ConfigureAwait(false);

            if (files.Count == 0)
            {
                continue;
            }

            foreach (var filtered in CandidateFilter.Filter(files, configuration.AllowArchives))
            {
                candidates.Add(new SubtitleCandidate
                {
                    EntryId = entry.Id,
                    EntryName = entry.Name,
                    EntryNotes = entry.Notes ?? string.Empty,
                    EntryUnverified = entry.Flags.Unverified,
                    File = filtered.File,
                    Rejection = filtered.Rejection,
                    NameMatch = ReleaseMatcher.Compare(videoName, filtered.File.Name, lookup.EpisodeNumber),
                });
            }
        }

        return candidates
            .OrderByDescending(c => c.IsUsable)
            .ThenByDescending(c => c.NameMatch.Score)
            .ThenBy(c => c.File.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Runs the full pipeline for one episode: identify, fetch, verify, correct, write, refresh.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="options">Options for this attempt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, including why nothing was written when that is the outcome.</returns>
    public async Task<SyncResult> SyncEpisodeAsync(
        Episode episode,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(options);

        var configuration = Configuration;

        if (string.IsNullOrWhiteSpace(episode.Path) || !File.Exists(episode.Path))
        {
            return SyncResult.Fail("The episode has no readable media file.");
        }

        // Resolved once and reused: the episode number is needed again when opening an archive,
        // and the mapping lookup is not free.
        var lookup = await idResolver.ResolveAsync(episode, cancellationToken).ConfigureAwait(false);

        List<SubtitleCandidate> candidates;
        List<SubtitleCandidate> usable;

        if (options.ForcedFile is not null)
        {
            candidates =
            [
                new SubtitleCandidate
                {
                    EntryId = options.ForcedEntryId,
                    File = options.ForcedFile,
                    NameMatch = ReleaseMatcher.Compare(
                        Path.GetFileName(episode.Path) ?? string.Empty,
                        options.ForcedFile.Name,
                        lookup.EpisodeNumber),
                }
            ];

            // The user picked this file deliberately, so the filename pre-filter does not get to
            // veto it. Its timing is still verified before anything is written.
            usable = candidates;
        }
        else
        {
            candidates = (await FindCandidatesAsync(episode, cancellationToken).ConfigureAwait(false)).ToList();
            usable = candidates.Where(c => c.IsUsable).ToList();
        }

        if (usable.Count == 0)
        {
            var result = SyncResult.Fail(
                candidates.Count == 0
                    ? "Jimaku has no subtitles for this episode."
                    : "Jimaku has files for this episode, but none of them are usable.");
            result.Candidates = candidates;
            await RecordAsync(episode, result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        // The filename score orders candidates; it must never exclude them. Release naming on
        // Jimaku bears little relation to the local file's naming, so a low score routinely belongs
        // to a subtitle that matches perfectly - which is exactly what verification is for. Capping
        // the count bounds the work without pre-judging which one is right.
        if (usable.Count > configuration.MaxCandidatesToTry)
        {
            logger.LogDebug(
                "{Count} usable candidates for {Name}; trying the {Max} best-named.",
                usable.Count,
                episode.Name,
                configuration.MaxCandidatesToTry);

            usable = usable.Take(configuration.MaxCandidatesToTry).ToList();
        }

        // The reference is derived once and reused for every candidate: extracting an embedded
        // track or running VAD over an episode is by far the most expensive step here.
        var reference = await referenceResolver
            .ResolveAsync(episode, options.AllowAudioFallback && configuration.EnableAudioFallback, cancellationToken)
            .ConfigureAwait(false);

        var aligner = new SubtitleAligner(configuration);

        foreach (var candidate in usable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] bytes;
            var fileName = candidate.File.Name;
            try
            {
                bytes = await apiClient.DownloadAsync(candidate.File.Url, cancellationToken).ConfigureAwait(false);
            }
            catch (JimakuApiException ex)
            {
                logger.LogWarning(ex, "Downloading {File} failed.", candidate.File.Name);
                continue;
            }

            if (CandidateFilter.IsReadableArchive(candidate.File.Name))
            {
                var extracted = ArchiveExtractor.TryExtract(bytes, lookup.EpisodeNumber, out var innerName);
                if (extracted is null)
                {
                    logger.LogDebug("No usable subtitle inside the archive {File}.", candidate.File.Name);
                    continue;
                }

                bytes = extracted;
                fileName = innerName;
            }

            SubtitleDocument document;
            try
            {
                document = SubtitleDocument.Parse(bytes);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Parsing {File} failed.", fileName);
                continue;
            }

            if (document.Kind == SubtitleFormatKind.Unknown || document.TimedLines.Count == 0)
            {
                logger.LogDebug("{File} contains no recognisable subtitle timings.", fileName);
                continue;
            }

            var alignment = Evaluate(candidate, document, reference, aligner, options);

            candidate.Alignment = alignment;

            if (!alignment.IsAcceptable)
            {
                logger.LogInformation("Rejected {File} for {Name}: {Reason}", fileName, episode.Name, alignment.Reason);
                continue;
            }

            var result = await ApplyAsync(episode, document, alignment, fileName, options, configuration, cancellationToken)
                .ConfigureAwait(false);

            result.Candidates = candidates;
            await RecordAsync(episode, result, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var declined = SyncResult.Fail(BuildDeclineMessage(usable, reference));
        declined.Candidates = candidates;
        declined.ReferenceSource = reference?.Source ?? "none";
        await RecordAsync(episode, declined, cancellationToken).ConfigureAwait(false);
        return declined;
    }

    private static AlignmentResult Evaluate(
        SubtitleCandidate candidate,
        SubtitleDocument document,
        ReferenceTrack? reference,
        SubtitleAligner aligner,
        SyncOptions options)
    {
        // A shared CRC32 means the subtitle was released against this exact video file, so its
        // timing is identical by construction and there is nothing to measure.
        if (candidate.NameMatch.IsExactRelease)
        {
            return SubtitleAligner.ExactRelease();
        }

        if (reference is null)
        {
            // Without a reference nothing can be verified. A user who explicitly picked this file
            // may still choose to take it as-is; an unattended sweep never does.
            return options.ApplyEvenIfUnverified
                ? new AlignmentResult
                {
                    Verdict = SyncVerdict.Exact,
                    Transform = TimingTransform.Identity,
                    ReferenceSource = "none",
                    Reason = "Applied without verification at your request: this episode has no embedded subtitle track and no usable audio analysis.",
                }
                : AlignmentResult.Decline(
                    "This episode has no embedded subtitle track to compare against, and voice activity analysis of its audio did not produce a usable reference.",
                    "none");
        }

        // Whether piecewise correction is permitted is the caller's decision: it differs between
        // an interactive request and the unattended sweep.
        var alignment = aligner.Align(
            reference.Value,
            document,
            options.AllowPiecewise,
            candidate.NameMatch.SourceMismatch);

        if (!alignment.IsAcceptable && options.ApplyEvenIfUnverified)
        {
            alignment.Verdict = SyncVerdict.Exact;
            alignment.Transform = TimingTransform.Identity;
            alignment.Reason = "Applied unchanged at your request, despite failing verification: " + alignment.Reason;
        }

        return alignment;
    }

    private async Task<SyncResult> ApplyAsync(
        Episode episode,
        SubtitleDocument document,
        AlignmentResult alignment,
        string fileName,
        SyncOptions options,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = alignment.Verdict == SyncVerdict.PiecewiseCut
                ? SubtitleRewriter.ApplyBlocks(document, alignment.Blocks).Text
                : SubtitleRewriter.Apply(document, alignment.Transform, configuration.KaraokePolicy).Text;
        }
        catch (InvalidOperationException ex)
        {
            return SyncResult.Fail(ex.Message);
        }

        var extension = document.Kind == SubtitleFormatKind.Srt ? "srt" : "ass";

        string? path = null;
        if (options.WriteSidecar)
        {
            path = await sidecarWriter.WriteAsync(
                episode,
                text,
                extension,
                configuration.LanguageTag,
                configuration.OverwriteExisting,
                cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Attached {File} to {Name}: {Verdict}, {Transform}, correlation {Correlation:0.00} via {Reference}.",
            fileName,
            episode.Name,
            alignment.Verdict,
            alignment.Transform.Describe(),
            alignment.Correlation,
            alignment.ReferenceSource);

        return new SyncResult
        {
            Applied = true,
            Verdict = alignment.Verdict,
            Message = alignment.Reason,
            SidecarPath = path,
            FileName = fileName,
            Content = options.WriteSidecar ? null : text,
            Extension = extension,
            ReferenceSource = alignment.ReferenceSource,
            Transform = alignment.Transform,
            Correlation = alignment.Correlation,
            PeakRatio = alignment.PeakRatio,
        };
    }

    private async Task<IReadOnlyList<JimakuEntry>> SearchEntriesAsync(
        AnimeLookup lookup,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (lookup.AniListId is { } aniListId)
        {
            return await apiClient.SearchByAniListIdAsync(aniListId, apiKey, cancellationToken).ConfigureAwait(false);
        }

        if (lookup.TmdbId is { } tmdbId)
        {
            return await apiClient.SearchByTmdbIdAsync(tmdbId, false, apiKey, cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(lookup.Query))
        {
            var entries = await apiClient.SearchByNameAsync(lookup.Query, true, apiKey, cancellationToken)
                .ConfigureAwait(false);

            // The API filters to anime by default, so live action finds nothing until asked for.
            if (entries.Count == 0)
            {
                entries = await apiClient.SearchByNameAsync(lookup.Query, false, apiKey, cancellationToken)
                    .ConfigureAwait(false);
            }

            return entries;
        }

        return Array.Empty<JimakuEntry>();
    }

    private async Task<IReadOnlyList<JimakuFile>> GetFilesAsync(
        JimakuEntry entry,
        AnimeLookup lookup,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var files = await apiClient
            .GetFilesAsync(entry.Id, lookup.EpisodeNumber, apiKey, cancellationToken)
            .ConfigureAwait(false);

        if (files.Count > 0 || !lookup.EpisodeNumber.HasValue)
        {
            return files;
        }

        // Jimaku drops files whose episode number it cannot parse from the filename when the
        // episode filter is set, which silently hides season packs. Ask again without the filter.
        logger.LogDebug("No per-episode files in entry {EntryId}; retrying without the episode filter.", entry.Id);
        return await apiClient.GetFilesAsync(entry.Id, null, apiKey, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildDeclineMessage(List<SubtitleCandidate> usable, ReferenceTrack? reference)
    {
        var best = usable
            .Where(c => c.Alignment is not null)
            .OrderByDescending(c => c.Alignment!.Correlation)
            .FirstOrDefault();

        if (best?.Alignment is null)
        {
            return "None of the candidate subtitles could be downloaded or parsed.";
        }

        var via = reference is null ? "no timing reference" : $"reference: {reference.Value.Source}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Declined all {usable.Count} candidate(s). Closest was '{best.File.Name}' - {best.Alignment.Reason} ({via}).");
    }

    private async Task RecordAsync(Episode episode, SyncResult result, CancellationToken cancellationToken)
    {
        await history.SetAsync(
            episode.Id,
            new SyncHistoryEntry
            {
                AttemptedUtc = DateTimeOffset.UtcNow,
                Verdict = result.Verdict,
                FileName = result.FileName ?? string.Empty,
                OffsetSeconds = result.Transform.OffsetSeconds,
                Scale = result.Transform.Scale,
                Correlation = result.Correlation,
                PeakRatio = result.PeakRatio,
                SidecarPath = result.SidecarPath ?? string.Empty,
                Reason = result.Message,
            },
            cancellationToken).ConfigureAwait(false);
    }
}
