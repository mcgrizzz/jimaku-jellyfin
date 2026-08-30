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
    SeriesProfileStore profiles,
    SyncNotifier notifier,
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

        // Every episode of a series resolves to the same entries, so searching once per episode
        // spends a 25-per-minute budget re-asking a settled question. On a 24-episode sweep that
        // was half the requests.
        var seriesId = episode.SeriesId;
        var lookupKey = LookupKey(lookup);
        var entries = profiles.GetEntries(seriesId, lookupKey, configuration.SeriesEntryCacheHours);

        if (entries is null)
        {
            var found = await SearchEntriesAsync(lookup, configuration.ApiKey, cancellationToken)
                .ConfigureAwait(false);

            entries = found.Select(e => new SeriesEntry
            {
                Id = e.Id,
                Name = e.Name,
                Notes = e.Notes ?? string.Empty,
                Unverified = e.Flags.Unverified,
            }).ToList();

            if (entries.Count > 0)
            {
                await profiles.RememberEntriesAsync(seriesId, lookupKey, entries, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            logger.LogDebug(
                "Reusing {Count} cached Jimaku entries for {Series}.",
                entries.Count,
                episode.SeriesName);
        }

        if (entries.Count == 0)
        {
            return Array.Empty<SubtitleCandidate>();
        }

        var videoName = Path.GetFileName(episode.Path) ?? string.Empty;
        var candidates = new List<SubtitleCandidate>();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var files = await GetFilesAsync(entry.Id, lookup, configuration.ApiKey, cancellationToken)
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
                    EntryNotes = entry.Notes,
                    EntryUnverified = entry.Unverified,
                    File = filtered.File,
                    Rejection = filtered.Rejection,
                    NameMatch = ReleaseMatcher.Compare(videoName, filtered.File.Name, lookup.EpisodeNumber),
                    Languages = SubtitleLanguageHint.Classify(filtered.File.Name),
                    ReleaseGroup = ReleaseInfo.Parse(filtered.File.Name).ReleaseGroup,
                });
            }
        }

        // A bilingual release puts Chinese on the styled, prominent line with Japanese underneath,
        // which is a poor result for someone asking for Japanese subtitles. The same groups almost
        // always publish a Japanese-only file beside it, so rank that first - but keep the
        // bilingual one available, since it still beats nothing.
        // Where earlier episodes of this series settled on one group, put that group's files first.
        // The filename score is a guess about a name; the preference is evidence from subtitles
        // that were actually measured against this library's own copies of the same show.
        var profile = configuration.UseSeriesPreference ? profiles.Get(seriesId) : null;

        return candidates
            .OrderByDescending(c => c.IsUsable)
            .ThenBy(c => LanguageRank(c.Languages))
            .ThenByDescending(c => MatchesSeriesPreference(profile, c, configuration))
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

        // Deleting the sidecar is how a person says "not that one", and it is the only such signal
        // available. Notice it before selecting anything, so this run does not simply download the
        // rejected file again and call it a success.
        await NoteRejectionAsync(episode, configuration, cancellationToken).ConfigureAwait(false);

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
                    ReleaseGroup = ReleaseInfo.Parse(options.ForcedFile.Name).ReleaseGroup,
                }
            ];

            // The user picked this file deliberately, so the filename pre-filter does not get to
            // veto it. Its timing is still verified before anything is written.
            usable = candidates;
        }
        else
        {
            candidates = (await FindCandidatesAsync(episode, cancellationToken).ConfigureAwait(false)).ToList();

            var rejected = history.RejectedFileNames(episode.Id);
            foreach (var candidate in candidates)
            {
                candidate.PreviouslyRejected = rejected.Contains(candidate.File.Name);
            }

            usable = candidates.Where(c => c.IsUsable && !c.PreviouslyRejected).ToList();
        }

        if (usable.Count == 0)
        {
            var wereRejected = candidates.Count(c => c.IsUsable && c.PreviouslyRejected);

            var result = SyncResult.Fail(
                candidates.Count == 0
                    ? "Jimaku has no subtitles for this episode."
                    : wereRejected > 0
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"Every usable file for this episode has already been tried and rejected ({wereRejected}). Pick one explicitly to override that.")
                        : "Jimaku has files for this episode, but none of them are usable.");
            result.Candidates = candidates;
            await FinishAsync(episode, result, options, cancellationToken).ConfigureAwait(false);
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

            // Now the file is in hand, judge its language by reading it. The filename was only
            // ever a guess, and it is wrong in both directions: a correct Japanese subtitle
            // carrying no tag was ranked below a worse one that happened to say "[JPN]".
            var byContent = SubtitleScriptAnalyzer.Classify(document);
            if (byContent != SubtitleLanguages.Unknown && byContent != candidate.Languages)
            {
                logger.LogDebug(
                    "{File}: filename suggested {FromName}, content says {FromContent} ({Profile}).",
                    fileName,
                    candidate.Languages,
                    byContent,
                    SubtitleScriptAnalyzer.Describe(SubtitleScriptAnalyzer.Profile(document)));

                candidate.Languages = byContent;
            }

            if (candidate.Languages == SubtitleLanguages.NoJapanese)
            {
                logger.LogInformation("Skipping {File}: it contains no Japanese.", fileName);
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
        var acceptable = measured
            .Where(m => m.Candidate.Alignment!.IsAcceptable)
            .OrderBy(m => LanguageRank(m.Candidate.Languages))
            .ThenByDescending(m => Quality(m.Candidate))
            .ThenByDescending(m => m.Candidate.NameMatch.Score)
            .ToList();

        if (acceptable.Count > 0)
        {
            // Priors, weakest first, each allowed to overturn the measurement only within a
            // bounded margin. A release from the same source as the local file is evidence about
            // the cut; a group the user has chosen repeatedly is evidence about their judgement,
            // and outranks it.
            var chosen = PreferMatchingSource(episode, acceptable, acceptable[0], configuration);
            chosen = ApplySeriesPreference(episode, acceptable, chosen, options, configuration);
            var (candidate, document, fileName) = chosen;

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
                candidate.EntryId,
                options,
                configuration,
                cancellationToken).ConfigureAwait(false);

            result.Candidates = candidates;

            if (ShouldLearnFrom(result, options, configuration))
            {
                await LearnAsync(episode, candidate, cancellationToken).ConfigureAwait(false);
            }

            await FinishAsync(episode, result, options, cancellationToken).ConfigureAwait(false);
            return result;
        }

        var declined = SyncResult.Fail(
            BuildDeclineMessage(usable, reference)
            + Explain(episode, configuration));
        declined.Candidates = candidates;
        declined.ReferenceSource = reference?.Source ?? "none";
        await FinishAsync(episode, declined, options, cancellationToken).ConfigureAwait(false);
        return declined;
    }

    /// <summary>
    /// Lets a series' established release group break a near-tie between verified candidates.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. Once several candidates all pass verification, the gaps between them are
    /// usually smaller than the measurement's own noise, and picking on that noise is what made the
    /// choice wobble from episode to episode. But a preference formed on earlier episodes is not
    /// allowed to argue with a materially better measurement on this one, so it may only overturn
    /// a decision inside a configured tolerance - and never one that would swap a Japanese-only
    /// file for a bilingual release.
    /// </remarks>
    /// <summary>
    /// Prefers a subtitle released against the same kind of source as the local file.
    /// </summary>
    /// <remarks>
    /// Broadcast and disc releases of the same episode are routinely cut differently - an extra
    /// recap, a different opening placement, a few seconds either side - so a subtitle timed for a
    /// web stream is a poor fit for a Blu-Ray however well its cue starts happen to correlate.
    /// This was already detected for the filename score and used to make the piecewise aligner try
    /// harder, but it had no say in which candidate was chosen.
    /// </remarks>
    private (SubtitleCandidate Candidate, SubtitleDocument Document, string FileName) PreferMatchingSource(
        Episode episode,
        List<(SubtitleCandidate Candidate, SubtitleDocument Document, string FileName)> ranked,
        (SubtitleCandidate Candidate, SubtitleDocument Document, string FileName) chosen,
        PluginConfiguration configuration)
    {
        if (!configuration.PreferMatchingSource || ranked.Count < 2 || chosen.Candidate.NameMatch.SourceMatch)
        {
            return chosen;
        }

        // A positive match, not merely the absence of a mismatch. A filename naming no source at
        // all produces the same "not mismatched" as one naming the right one, and treating the two
        // alike promoted an untagged fansub over the actual disc release it was competing with.
        var index = ranked.FindIndex(m => m.Candidate.NameMatch.SourceMatch);
        if (index < 0)
        {
            return chosen;
        }

        var matching = ranked[index];

        if (LanguageRank(matching.Candidate.Languages) > LanguageRank(chosen.Candidate.Languages))
        {
            return chosen;
        }

        var sacrificed = Quality(chosen.Candidate) - Quality(matching.Candidate);
        if (sacrificed > configuration.SourcePreferenceTolerance)
        {
            return chosen;
        }

        logger.LogInformation(
            "Preferring {File} for {Name}: it was released against the same source as the local file, and measures within {Gap:0.000} of the best.",
            matching.FileName,
            episode.Name,
            sacrificed);

        return matching;
    }

    private (SubtitleCandidate Candidate, SubtitleDocument Document, string FileName) ApplySeriesPreference(
        Episode episode,
        List<(SubtitleCandidate Candidate, SubtitleDocument Document, string FileName)> ranked,
        (SubtitleCandidate Candidate, SubtitleDocument Document, string FileName) chosen,
        SyncOptions options,
        PluginConfiguration configuration)
    {
        if (!configuration.UseSeriesPreference || options.ForcedFile is not null || ranked.Count < 2)
        {
            return chosen;
        }

        var profile = profiles.Get(episode.SeriesId);
        if (profile is null)
        {
            return chosen;
        }

        var index = ranked.FindIndex(m => MatchesSeriesPreference(profile, m.Candidate, configuration));
        if (index <= 0)
        {
            return chosen;
        }

        var preferred = ranked[index];

        if (LanguageRank(preferred.Candidate.Languages) > LanguageRank(chosen.Candidate.Languages))
        {
            return chosen;
        }

        var sacrificed = Quality(chosen.Candidate) - Quality(preferred.Candidate);
        if (sacrificed > configuration.SeriesPreferenceTolerance)
        {
            logger.LogDebug(
                "Not applying the {Group} preference for {Series}: {File} measures {Gap:0.000} better.",
                profile.PreferredReleaseGroup,
                episode.SeriesName,
                chosen.FileName,
                sacrificed);
            return chosen;
        }

        logger.LogInformation(
            "Preferring {File} for {Name}: {Group} has worked for this series {Count} time(s), and it measures within {Gap:0.000} of the best.",
            preferred.FileName,
            episode.Name,
            profile.PreferredReleaseGroup,
            profile.Confirmations,
            sacrificed);

        return preferred;
    }

    private static bool MatchesSeriesPreference(
        SeriesProfile? profile,
        SubtitleCandidate candidate,
        PluginConfiguration configuration) =>
        SeriesProfileStore.IsPreferred(
            profile,
            candidate.ReleaseGroup,
            candidate.EntryId,
            configuration.SeriesPreferenceMinConfirmations);

    /// <summary>
    /// Builds a fingerprint of how a series was identified, so cached entries are dropped rather
    /// than silently reused when the library gets re-scraped onto different provider IDs.
    /// </summary>
    private static string LookupKey(AnimeLookup lookup) => string.Create(
        CultureInfo.InvariantCulture,
        $"a={lookup.AniListId};t={lookup.TmdbId};q={lookup.Query}");

    private static AlignmentResult Evaluate(
        SubtitleCandidate candidate,
        SubtitleDocument document,
        ReferenceTrack? reference,
        SubtitleAligner aligner,
        SyncOptions options)
    {
        // An offset given by hand settles the question before it is asked. Nothing here can be
        // measured more reliably than by someone watching the episode, and this path has to work
        // when there is no usable reference at all - which is exactly when it gets used.
        if (options.ManualOffsetSeconds is { } manual)
        {
            return new AlignmentResult
            {
                Verdict = Math.Abs(manual) < 1e-9 ? SyncVerdict.Exact : SyncVerdict.ConstantOffset,
                Transform = new TimingTransform(1.0, manual),
                ReferenceSource = "an offset you supplied",
                Reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Shifted by {manual:+0.000;-0.000}s as you specified, without verification."),
            };
        }

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
            // Failing verification means the evidence was too thin to act on unattended, not that
            // the measurement was wrong. Writing a misaligned file unchanged is the worst of both
            // outcomes, so the correction is available to anyone willing to own the decision.
            if (options.UseMeasuredTransform && !alignment.Transform.IsIdentity)
            {
                alignment.Reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Applied the measured correction ({alignment.Transform.Describe()}) at your request, despite it failing verification: {alignment.Reason}");

                alignment.Verdict = alignment.Transform.IsShiftOnly
                    ? SyncVerdict.ConstantOffset
                    : SyncVerdict.FramerateDrift;
            }
            else
            {
                alignment.Verdict = SyncVerdict.Exact;
                alignment.Transform = TimingTransform.Identity;
                alignment.Reason = "Applied unchanged at your request, despite failing verification: " + alignment.Reason;
            }
        }

        return alignment;
    }

    private async Task<SyncResult> ApplyAsync(
        Episode episode,
        SubtitleDocument document,
        AlignmentResult alignment,
        string fileName,
        long entryId,
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

        // The sidecar's name is dictated by Jellyfin's external-file resolver and cannot say where
        // the file came from, so say it inside the file instead.
        if (configuration.StampProvenance)
        {
            text = SubtitleProvenance.Stamp(
                text,
                document.Kind,
                SubtitleProvenance.BuildLine(fileName, entryId, alignment.Transform, DateTimeOffset.UtcNow));
        }

        // Remove what this replaces before writing rather than after. The path resolver skips names
        // already taken, so leaving the old file in place would push the new one onto a ".1."
        // counter and leave two subtitles on the episode with nothing to choose between them. The
        // corrected text is already in hand at this point, so the only thing that can still fail is
        // the write itself. This runs for the provider flow too, where core does the writing
        // moments later from the same text.
        if (configuration.RemoveSupersededSidecars)
        {
            foreach (var previous in OwnedSidecars(episode, configuration.LanguageTag))
            {
                sidecarWriter.TryDelete(previous);
            }
        }

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
            EntryId = entryId,
            ReleaseGroup = ReleaseInfo.Parse(fileName).ReleaseGroup ?? string.Empty,
            UserChosen = options.ForcedFile is not null,
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
        long entryId,
        AnimeLookup lookup,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var files = await apiClient
            .GetFilesAsync(entryId, lookup.EpisodeNumber, apiKey, cancellationToken)
            .ConfigureAwait(false);

        if (files.Count > 0 || !lookup.EpisodeNumber.HasValue)
        {
            return files;
        }

        // Jimaku drops files whose episode number it cannot parse from the filename when the
        // episode filter is set, which silently hides season packs. Ask again without the filter.
        logger.LogDebug("No per-episode files in entry {EntryId}; retrying without the episode filter.", entryId);
        return await apiClient.GetFilesAsync(entryId, null, apiKey, cancellationToken).ConfigureAwait(false);
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
        // A reference with no cue structure yields no coverage figure. Treating that as zero would
        // rank every candidate identically on the term that leads the score, so fall back to
        // correlation carrying the decision on its own.
        var coverage = Math.Round(alignment.Coverage ?? 0, 2);
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

    /// <summary>
    /// Adds a sentence about the reference when the reference is the likely problem.
    /// </summary>
    /// <remarks>
    /// Every candidate failing by a wide margin usually says more about what they were compared
    /// against than about the subtitles, and the numbers alone do not distinguish the two. Naming
    /// the reason turns "declined all six" into something that can be acted on.
    /// </remarks>
    private string Explain(Episode episode, PluginConfiguration configuration)
    {
        var report = referenceResolver.PeekReport(episode.Id);
        var explanation = report?.Explain() ?? string.Empty;

        return explanation.Length > 0 ? " " + explanation : string.Empty;
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

        // Prefer the alignment's own description, which records which comparison was used.
        var source = best.Alignment.ReferenceSource;
        var via = !string.IsNullOrEmpty(source)
            ? $"reference: {source}"
            : reference is null
                ? "no timing reference"
                : $"reference: {reference.Value.Source}";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Declined all {usable.Count} candidate(s). Closest was '{best.File.Name}' - {best.Alignment.Reason} ({via}).");
    }

    /// <summary>
    /// Decides whether an outcome is evidence about the series, or merely the plugin agreeing with
    /// itself.
    /// </summary>
    /// <remarks>
    /// Only a file the user picked counts. An automatic selection confirming the preference that
    /// produced it is not an observation, and the error compounds: whatever the first sweep happened
    /// to land on biases the second episode, which confirms it again, until a coin flip has hardened
    /// into a rule that then outranks measurement on every subsequent episode. Rejections are
    /// treated the other way round and always count, whoever chose the file - deleting a subtitle is
    /// a judgement only a person can make.
    /// </remarks>
    /// <param name="result">What happened.</param>
    /// <param name="options">How the attempt was made.</param>
    /// <param name="configuration">The current settings.</param>
    /// <returns><see langword="true"/> when the series profile should be updated.</returns>
    internal static bool ShouldLearnFrom(
        SyncResult result,
        SyncOptions options,
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        return result.Applied
            && configuration.UseSeriesPreference
            && options.ForcedFile is not null;
    }

    /// <summary>
    /// Folds one confirmed success into what is known about the series.
    /// </summary>
    private async Task LearnAsync(Episode episode, SubtitleCandidate candidate, CancellationToken cancellationToken)
    {
        if (episode.SeriesId == Guid.Empty)
        {
            return;
        }

        var profile = profiles.Get(episode.SeriesId) ?? new SeriesProfile();
        var before = profile.PreferredReleaseGroup;

        SeriesProfileStore.RecordSuccess(profile, candidate.ReleaseGroup, candidate.EntryId);

        if (!string.Equals(before, profile.PreferredReleaseGroup, StringComparison.OrdinalIgnoreCase)
            && profile.PreferredReleaseGroup.Length > 0)
        {
            logger.LogInformation(
                "{Series} now prefers subtitles from {Group}.",
                episode.SeriesName,
                profile.PreferredReleaseGroup);
        }

        await profiles.SaveAsync(episode.SeriesId, profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the outcome and reports it. Every exit from the pipeline goes through here, so a
    /// path cannot be added later that quietly does neither.
    /// </summary>
    private async Task FinishAsync(
        Episode episode,
        SyncResult result,
        SyncOptions options,
        CancellationToken cancellationToken)
    {
        await RecordAsync(episode, result, cancellationToken).ConfigureAwait(false);
        await notifier.NotifyAsync(episode, result, options.Interactive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws away the subtitle currently attached to an episode, and records that it was rejected.
    /// </summary>
    /// <remarks>
    /// The explicit form of what deleting the file does implicitly. Worth having as an action
    /// rather than leaving it to the filesystem: it removes the file, records why it went, and
    /// takes back the credit the series preference gave it - which deleting through a file manager
    /// only achieves on the next sync of that episode.
    /// </remarks>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was rejected, or null when nothing was attached.</returns>
    public async Task<SyncAttempt?> RejectCurrentAsync(Episode episode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var configuration = Configuration;
        var owned = OwnedSidecars(episode, configuration.LanguageTag).ToList();

        var rejected = await history.RejectCurrentAsync(episode.Id, cancellationToken).ConfigureAwait(false);
        if (rejected is null)
        {
            return null;
        }

        var removed = owned.Count(sidecarWriter.TryDelete);

        logger.LogInformation(
            "Rejected {File} for {Name}; removed {Removed} sidecar(s) and will not offer it again.",
            rejected.FileName,
            episode.Name,
            removed);

        if (removed > 0)
        {
            await sidecarWriter.RefreshAsync(episode, cancellationToken).ConfigureAwait(false);
        }

        if (configuration.UseSeriesPreference && episode.SeriesId != Guid.Empty)
        {
            var profile = profiles.Get(episode.SeriesId);
            if (profile is not null)
            {
                SeriesProfileStore.RecordRejection(profile, rejected.ReleaseGroup);
                await profiles.SaveAsync(episode.SeriesId, profile, cancellationToken).ConfigureAwait(false);
            }
        }

        return rejected;
    }

    /// <summary>
    /// Reads what has been tried for an episode.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <returns>The recorded history, or null when the episode has none.</returns>
    public SyncHistoryEntry? GetHistory(Episode episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return history.Get(episode.Id);
    }

    /// <summary>
    /// Lists the subtitle sidecars actually present for an episode.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="languageTag">The language tag the sidecar would carry.</param>
    /// <returns>The paths on disk.</returns>
    public IReadOnlyList<string> FindSidecars(Episode episode, string languageTag) =>
        sidecarWriter.FindExisting(episode, languageTag);

    /// <summary>
    /// Reports what the plugin compares an episode's subtitles against, and why that.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account.</returns>
    public Task<Media.ReferenceReport> ExplainReferenceAsync(Episode episode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);

        return referenceResolver.ExplainAsync(
            episode,
            Configuration.EnableAudioFallback,
            cancellationToken);
    }

    /// <summary>
    /// Puts previously rejected files back on the table for an episode.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public Task ClearRejectionsAsync(Episode episode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);
        return history.ClearRejectionsAsync(episode.Id, cancellationToken);
    }

    /// <summary>
    /// Lists the sidecars on disk that this plugin is entitled to remove.
    /// </summary>
    /// <remarks>
    /// Two independent sources of ownership, because neither covers everything. A recorded path
    /// covers what the plugin wrote itself. The provenance stamp covers the native subtitle flow,
    /// where core writes the file and never says where - and it is the stamp, not the naming, that
    /// makes this safe: a subtitle the user placed by hand carries neither and is left alone.
    /// </remarks>
    private IEnumerable<string> OwnedSidecars(Episode episode, string languageTag)
    {
        var recorded = history.AppliedSidecarPaths(episode.Id);

        foreach (var path in recorded)
        {
            yield return path;
        }

        foreach (var path in sidecarWriter.FindExisting(episode, languageTag))
        {
            if (!recorded.Contains(path, StringComparer.Ordinal) && sidecarWriter.WasWrittenHere(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// Treats a vanished sidecar as a rejection of whatever produced it.
    /// </summary>
    private async Task NoteRejectionAsync(
        Episode episode,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Either signal counts as "still there". The naming scan misses hand-renamed variants and
        // the recorded path misses the provider flow, so requiring both would report deletions that
        // never happened - and a false rejection quietly removes a good file from consideration.
        var present = sidecarWriter.FindExisting(episode, configuration.LanguageTag).Count > 0
            || history.AppliedSidecarPaths(episode.Id).Any(File.Exists);

        var rejected = await history.NoteDeletionAsync(episode.Id, present, cancellationToken)
            .ConfigureAwait(false);

        if (rejected is null)
        {
            return;
        }

        logger.LogInformation(
            "The subtitle previously attached to {Name} is gone ({File}); treating that as a rejection and not offering it again.",
            episode.Name,
            rejected.FileName);

        if (!configuration.UseSeriesPreference || episode.SeriesId == Guid.Empty)
        {
            return;
        }

        var profile = profiles.Get(episode.SeriesId);
        if (profile is null)
        {
            return;
        }

        var before = profile.PreferredReleaseGroup;
        SeriesProfileStore.RecordRejection(profile, rejected.ReleaseGroup);

        if (!string.Equals(before, profile.PreferredReleaseGroup, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("{Series} no longer prefers {Group}.", episode.SeriesName, before);
        }

        await profiles.SaveAsync(episode.SeriesId, profile, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordAsync(Episode episode, SyncResult result, CancellationToken cancellationToken)
    {
        await history.RecordAttemptAsync(
            episode.Id,
            new SyncAttempt
            {
                AttemptedUtc = DateTimeOffset.UtcNow,
                Verdict = result.Verdict,
                Status = result.Applied ? AttemptStatus.Applied : AttemptStatus.Declined,
                EntryId = result.EntryId,
                FileName = result.FileName ?? string.Empty,
                ReleaseGroup = result.ReleaseGroup,
                UserChosen = result.UserChosen,
                SidecarPath = result.SidecarPath ?? string.Empty,
                OffsetSeconds = result.Transform.OffsetSeconds,
                Scale = result.Transform.Scale,
                Correlation = result.Correlation,
                Reason = result.Message,
            },
            cancellationToken).ConfigureAwait(false);
    }
}
