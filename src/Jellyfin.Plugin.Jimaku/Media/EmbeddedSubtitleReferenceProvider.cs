using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Timing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Builds a reference from a subtitle track already embedded in the media file.
/// </summary>
/// <remarks>
/// This is by far the best reference available and is always tried first. It needs no audio
/// analysis, takes about a second, and compares cue structure rather than acoustic energy, which
/// matters enormously for anime where a continuous musical score defeats energy-based detection.
/// </remarks>
public sealed class EmbeddedSubtitleReferenceProvider(
    IMediaSourceManager mediaSourceManager,
    ISubtitleEncoder subtitleEncoder,
    ILogger<EmbeddedSubtitleReferenceProvider> logger) : IReferenceTrackProvider
{
    private const int MinimumCues = 10;

    /// <summary>
    /// Cap on tracks compared. The vote is all-pairs, so cost grows with the square: ten tracks is
    /// forty-five cross-correlations, six is fifteen. Six is ample to out-vote an outlier.
    /// </summary>
    private const int MaxTracksToCompare = 6;

    /// <summary>How far apart two tracks may sit and still be counted as agreeing.</summary>
    private const double AgreementToleranceSeconds = 0.15;

    private static readonly string[] TextCodecs = ["subrip", "srt", "ass", "ssa", "mov_text", "text", "webvtt"];

    /// <summary>
    /// Title words that mark a track as annotation rather than dialogue.
    /// </summary>
    /// <remarks>
    /// A signs and songs track cues on title cards and lyrics, not speech, so aligning a dialogue
    /// subtitle against it compares two different things and produces a flat correlation surface.
    /// It is only demoted, never discarded: on a file that has nothing else it is still structure,
    /// and the consensus vote already treats a lone track as unconfirmed.
    /// </remarks>
    private static readonly string[] AnnotationMarkers =
        ["sign", "song", "s&s", "lyric", "karaoke", "credit", "commentary", "forced"];

    /// <inheritdoc />
    public async Task<ReferenceTrack?> TryGetAsync(BaseItem item, ReferenceReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(report);

        MediaSourceInfo? source;
        try
        {
            source = await mediaSourceManager
                .GetMediaSource(item, item.Id.ToString("N"), null, false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve a media source for {Path}.", item.Path);
            return null;
        }

        Describe(source, report);

        var streams = SelectStreams(source);
        if (streams.Count == 0)
        {
            logger.LogDebug("{Path} has no embedded text subtitle track to use as a timing reference.", item.Path);
            return null;
        }

        // Read every text track, then let them vote.
        //
        // Density is the wrong criterion and picking by language is worse. On a multi-subtitle
        // release the densest track is often the group's own, carrying opening and ending karaoke,
        // signs and a staff credit - and frequently authored on a different timing convention from
        // the translations shipped alongside it. Measured on one such file, nine translation tracks
        // agreed within 100ms while both Chinese tracks sat 230ms away, and the Chinese track was
        // the densest. Choosing the track that most others agree with finds the timing the file as
        // a whole is built on, instead of trusting whichever happens to have the most lines.
        var tracks = new List<(MediaStream Stream, CueTrack Cues)>();

        foreach (var candidate in streams.Take(MaxTracksToCompare))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = report.Streams.Find(s => s.Index == candidate.Index);
            var parsed = await TryReadCuesAsync(candidate, source!, item, cancellationToken).ConfigureAwait(false);

            if (parsed is null)
            {
                if (info is not null)
                {
                    info.Status = "could not be extracted";
                }

                continue;
            }

            if (info is not null)
            {
                info.CueCount = parsed.Count;
            }

            if (parsed.Count < MinimumCues)
            {
                if (info is not null)
                {
                    info.Status = "too few cues to align against";
                }

                continue;
            }

            if (info is not null)
            {
                info.Status = "compared";
            }

            tracks.Add((candidate, parsed));
        }

        if (tracks.Count == 0)
        {
            logger.LogDebug("No embedded subtitle track for {Path} had enough cues to align against.", item.Path);
            return null;
        }

        var (bestStream, bestTrack, agreement) = SelectConsensus(tracks, item);

        var chosenInfo = report.Streams.Find(s => s.Index == bestStream.Index);
        if (chosenInfo is not null)
        {
            chosenInfo.Used = true;
            chosenInfo.Status = "used as the timing reference";
        }

        logger.LogInformation(
            "Timing reference for {Path}: embedded stream {Index} ({Language} {Codec}), {Count} cues, agreeing with {Agreement} of {Total} tracks.",
            item.Path,
            bestStream.Index,
            bestStream.Language ?? "und",
            bestStream.Codec,
            bestTrack.Count,
            agreement,
            tracks.Count - 1);

        var duration = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds
            : bestTrack.LastEndSeconds;

        var language = string.IsNullOrEmpty(bestStream.Language) ? bestStream.Codec : bestStream.Language;
        var description = tracks.Count > 1
            ? $"embedded {language} subtitles ({bestTrack.Count} cues, agreeing with {agreement}/{tracks.Count - 1} other tracks)"
            : $"embedded {language} subtitles ({bestTrack.Count} cues)";

        report.Chosen = description;
        report.FromSubtitles = true;

        if (IsAnnotation(bestStream))
        {
            // Said out loud rather than left to be inferred from a weak correlation: this is the
            // one case where the reference itself is the likely problem.
            report.Note =
                "The only usable track looks like a signs and songs track, which cues on title cards rather than speech. Timing measured against it is unreliable.";
        }

        return new ReferenceTrack(ActivitySignal.FromCues(bestTrack, duration), description, true, bestTrack);
    }

    /// <summary>
    /// Picks the track whose timing the most other tracks agree with.
    /// </summary>
    /// <remarks>
    /// Every pair is compared on cue starts, so differences in how lines are split do not count
    /// against agreement. A track that no others corroborate is a poor reference no matter how
    /// detailed it is.
    /// </remarks>
    private (MediaStream Stream, CueTrack Cues, int Agreement) SelectConsensus(
        List<(MediaStream Stream, CueTrack Cues)> tracks,
        BaseItem item)
    {
        if (tracks.Count == 1)
        {
            return (tracks[0].Stream, tracks[0].Cues, 0);
        }

        var duration = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds
            : tracks.Max(t => t.Cues.LastEndSeconds);

        var signals = tracks
            .Select(t => ActivitySignal.FromCueStarts(t.Cues, duration))
            .ToList();

        var search = new LinearFitSearch(new LinearFitOptions
        {
            MaxSearchOffsetSeconds = 30,
            EnableFramerateSearch = false,
        });

        var agreement = new int[tracks.Count];

        for (var i = 0; i < tracks.Count; i++)
        {
            for (var j = i + 1; j < tracks.Count; j++)
            {
                var fits = search.Search(signals[i], tracks[j].Cues, scales: null, onsets: true);
                if (fits.Count == 0)
                {
                    continue;
                }

                if (Math.Abs(fits[0].OffsetSeconds) <= AgreementToleranceSeconds)
                {
                    agreement[i]++;
                    agreement[j]++;
                }
            }
        }

        var bestIndex = 0;
        for (var i = 1; i < tracks.Count; i++)
        {
            // Most corroboration wins; among equally corroborated tracks, take the fullest.
            if (agreement[i] > agreement[bestIndex] ||
                (agreement[i] == agreement[bestIndex] && tracks[i].Cues.Count > tracks[bestIndex].Cues.Count))
            {
                bestIndex = i;
            }
        }

        for (var i = 0; i < tracks.Count; i++)
        {
            logger.LogDebug(
                "  stream {Index} ({Language}): {Count} cues, agrees with {Agreement} other tracks{Chosen}",
                tracks[i].Stream.Index,
                tracks[i].Stream.Language ?? "und",
                tracks[i].Cues.Count,
                agreement[i],
                i == bestIndex ? " <- chosen" : string.Empty);
        }

        return (tracks[bestIndex].Stream, tracks[bestIndex].Cues, agreement[bestIndex]);
    }

    /// <summary>
    /// Records every embedded subtitle stream and why it is or is not a candidate, before any of
    /// them are read.
    /// </summary>
    private static void Describe(MediaSourceInfo? source, ReferenceReport report)
    {
        if (source?.MediaStreams is null)
        {
            return;
        }

        foreach (var stream in source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal))
        {
            var isText = stream.Codec is not null
                && TextCodecs.Contains(stream.Codec, StringComparer.OrdinalIgnoreCase);

            report.Streams.Add(new ReferenceStreamInfo
            {
                Index = stream.Index,
                Codec = stream.Codec ?? "unknown",
                Language = string.IsNullOrEmpty(stream.Language) ? "und" : stream.Language,
                Title = stream.Title ?? string.Empty,
                IsForced = stream.IsForced,

                // Image-based tracks are the common case on disc rips and cannot be read as text at
                // all, which is worth stating: it is the usual reason a file with several subtitle
                // tracks still ends up on the audio fallback.
                Status = isText
                    ? "available"
                    : $"image-based ({stream.Codec ?? "unknown"}), which carries no readable timings",
            });
        }
    }

    private static bool IsAnnotation(MediaStream stream)
    {
        if (stream.IsForced)
        {
            return true;
        }

        var title = stream.Title;
        return !string.IsNullOrEmpty(title)
            && AnnotationMarkers.Any(m => title.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static List<MediaStream> SelectStreams(MediaSourceInfo? source)
    {
        if (source?.MediaStreams is null)
        {
            return [];
        }

        var text = source.MediaStreams
            .Where(s => s.Type == MediaStreamType.Subtitle)
            .Where(s => !s.IsExternal)
            .Where(s => s.Codec is not null && TextCodecs.Contains(s.Codec, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Dialogue first. A signs and songs track cues on title cards and lyrics rather than
        // speech, so it measures something different from the subtitle being checked - but it is
        // sorted down rather than removed, because on a file that carries nothing else it is still
        // the best structure available.
        return text
            .OrderBy(IsAnnotation)
            .ThenByDescending(s => s.IsDefault)
            .ToList();
    }

    /// <summary>Extracts one embedded track and reduces it to cue timings, or null on failure.</summary>
    private async Task<CueTrack?> TryReadCuesAsync(
        MediaStream stream,
        MediaSourceInfo source,
        BaseItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            // Extracts into the server's own subtitle cache and returns a real file, reusing
            // Jellyfin's extraction and locking rather than duplicating it.
            var path = await subtitleEncoder
                .GetSubtitleFilePath(stream, source, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return SubtitleDocument.Parse(bytes).ToCueTrack();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Could not read embedded subtitle stream {Index} of {Path}.",
                stream.Index,
                item.Path);
            return null;
        }
    }
}
