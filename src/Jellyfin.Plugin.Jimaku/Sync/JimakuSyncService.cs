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
                    Languages = SubtitleLanguageHint.Classify(filtered.File.Name),
                });
            }
        }

        // A bilingual release puts Chinese on the styled, prominent line with Japanese underneath,
        // which is a poor result for someone asking for Japanese subtitles. The same groups almost
        // always publish a Japanese-only file beside it, so rank that first - but keep the
        // bilingual one available, since it still beats nothing.
        return candidates
            .OrderByDescending(c => c.IsUsable)
            .ThenBy(c => LanguageRank(c.Languages))
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

        // Measure every candidate, then choose the best one - rather than taking the first that
        // merely passes. Passing is a floor, not a ranking: the first acceptable file is often not
        // the closest match, and stopping early means better candidates are never even downloaded.
        var measured = new List<(SubtitleCandidate Candidate, SubtitleDocument Document, string FileName)>();

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
            measured.Add((candidate, document, fileName));

            logger.LogInformation(
                "Measured {File} for {Name}: {Verdict}, correlation {Correlation:0.00}, uniqueness {PeakRatio:0.00}, {Transform}.",
                fileName,
                episode.Name,
                alignment.Verdict,
                alignment.Correlation,
                alignment.PeakRatio,
                alignment.Transform.Describe());

            // A shared CRC32 proves the subtitle was released against this exact video file.
            // Nothing measured can beat that, so there is no reason to keep downloading.
            if (candidate.NameMatch.IsExactRelease)
            {
                break;
            }
        }

        // When several independently produced subtitles all need the same correction, that shared
        // offset belongs to the reference, not to any of them. Remove it before choosing, so what
        // remains is each subtitle's own error.
        if (configuration.DetectReferenceBias)
        {
            ApplyReferenceBiasCorrection(measured.Select(m => m.Candidate).ToList(), configuration);
        }

        // Language first, then fit.
        //
        // A bilingual release is a worse outcome for someone asking for Japanese subtitles no
        // matter how well it correlates, so a hundredth of a point of correlation must not buy its
        // way past that. Ranking fit first let a [CHS, JPN] file beat its own [JPN] sibling on
        // r=1.00 against r=0.99 - a difference well inside the measurement noise.
        var best = measured
            .Where(m => m.Candidate.Alignment!.IsAcceptable)
            .OrderBy(m => LanguageRank(m.Candidate.Languages))
            .ThenByDescending(m => Quality(m.Candidate))
            .ThenByDescending(m => m.Candidate.NameMatch.Score)
            .Select(m => (Item: m, Found: true))
            .FirstOrDefault();

        if (best.Found)
        {
            var (candidate, document, fileName) = best.Item;

            if (measured.Count > 1)
            {
                logger.LogInformation(
                    "Chose {File} for {Name} out of {Count} measured candidates.",
                    fileName,
                    episode.Name,
                    measured.Count);
            }

            var result = await ApplyAsync(
                episode,
                document,
                candidate.Alignment!,
                fileName,
                options,
                configuration,
                cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Subtracts any offset shared by most candidates, and re-derives each verdict from what is
    /// left. A candidate whose remaining error falls below the correction threshold becomes an
    /// exact match written unchanged.
    /// </summary>
    private void ApplyReferenceBiasCorrection(
        IReadOnlyList<SubtitleCandidate> candidates,
        PluginConfiguration configuration)
    {
        // Every measurement votes, including candidates that failed verification. A declined
        // candidate still measured an offset, and it is precisely those corroborating measurements
        // that reveal the shared component: restricting the vote to accepted candidates threw away
        // four of the seven observations and left too few to reach a consensus.
        var offsets = candidates
            .Where(c => c.Alignment is not null)
            .Where(c => c.Alignment!.Transform.IsShiftOnly || c.Alignment.Verdict == SyncVerdict.Declined)
            .Select(c => c.Alignment!.Transform.OffsetSeconds)
            .ToList();

        var bias = ReferenceBias.Detect(offsets);
        if (!bias.Detected || Math.Abs(bias.OffsetSeconds) < 0.02)
        {
            return;
        }

        logger.LogInformation(
            "{Agreeing} of {Total} candidates agree on a {Bias:+0.000;-0.000}s offset; treating it as the reference's own timing and not correcting for it.",
            bias.Agreeing,
            bias.Total,
            bias.OffsetSeconds);

        foreach (var candidate in candidates)
        {
            var alignment = candidate.Alignment;
            if (alignment is null || !alignment.Transform.IsShiftOnly)
            {
                continue;
            }

            var corrected = alignment.Transform.OffsetSeconds - bias.OffsetSeconds;
            alignment.Transform = new TimingTransform(alignment.Transform.Scale, corrected);
            alignment.ReferenceBiasSeconds = bias.OffsetSeconds;

            if (alignment.Verdict is not (SyncVerdict.ConstantOffset or SyncVerdict.Exact))
            {
                continue;
            }

            if (Math.Abs(corrected) < configuration.MinCorrectionSeconds)
            {
                alignment.Verdict = SyncVerdict.Exact;
                alignment.Transform = TimingTransform.Identity;
                alignment.Reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Already in sync. The {bias.OffsetSeconds:+0.000;-0.000}s difference is shared by {bias.Agreeing} of {bias.Total} candidates, so it belongs to the reference track rather than to this subtitle.");
            }
            else
            {
                alignment.Verdict = SyncVerdict.ConstantOffset;
                alignment.Reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Constant offset of {corrected:+0.000;-0.000}s, after discounting the {bias.OffsetSeconds:+0.000;-0.000}s shared by {bias.Agreeing} of {bias.Total} candidates.");
            }
        }
    }

    /// <summary>
    /// Scores how well a measured candidate fits, for choosing between several that all pass.
    /// </summary>
    /// <remarks>
    /// Correlation is rounded before comparison so that differences well inside the measurement
    /// noise do not decide the outcome; uniqueness then breaks the tie, since it is the measure
    /// that says the alignment is unambiguous rather than merely strong.
    /// </remarks>
    private static double Quality(SubtitleCandidate candidate)
    {
        var alignment = candidate.Alignment;
        if (alignment is null)
        {
            return double.NegativeInfinity;
        }

        // Coverage leads. Between two correctly aligned subtitles the better one is the one that
        // actually renders the dialogue: a file omitting a fifth of the lines, and holding the rest
        // on screen briefly, reads badly however well it correlates. Correlation is normalized, so
        // on its own it quietly favours the sparser file, whose fewer cues have less to disagree
        // with. Correlation and uniqueness stay in the score to break ties and to keep a
        // well-covered but mistimed file from winning.
        var coverage = Math.Round(alignment.Coverage, 2);
        var correlation = Math.Round(alignment.Correlation, 2) / 10.0;
        var uniqueness = Math.Min(alignment.PeakRatio, 5.0) / 1000.0;

        // A file needing no correction is preferable to one needing a large one, all else equal.
        var penalty = Math.Min(Math.Abs(alignment.Transform.OffsetSeconds), 30) / 100000.0;

        return coverage + correlation + uniqueness - penalty;
    }

    /// <summary>
    /// Ranks candidates by how likely the file is to be usefully Japanese.
    /// </summary>
    /// <remarks>
    /// An unlabelled filename ranks level with an explicitly Japanese one. Jimaku hosts Japanese
    /// subtitles, so a name that simply does not mention a language says nothing against the file -
    /// and demoting it meant a better subtitle could lose to a worse one purely for lacking a
    /// "[JPN]" tag, before coverage was ever compared. Only a bilingual release is genuinely worse,
    /// because it renders Chinese as the prominent line.
    /// </remarks>
    private static int LanguageRank(SubtitleLanguages languages) => languages switch
    {
        SubtitleLanguages.JapaneseOnly => 0,
        SubtitleLanguages.Unknown => 0,
        SubtitleLanguages.Multilingual => 1,
        _ => 2,
    };

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
