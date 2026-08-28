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

    /// <summary>Cap on tracks compared, to bound the pairwise comparison on many-subtitle releases.</summary>
    private const int MaxTracksToCompare = 10;

    /// <summary>How far apart two tracks may sit and still be counted as agreeing.</summary>
    private const double AgreementToleranceSeconds = 0.15;

    private static readonly string[] TextCodecs = ["subrip", "srt", "ass", "ssa", "mov_text", "text", "webvtt"];

    /// <inheritdoc />
    public async Task<ReferenceTrack?> TryGetAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

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

            var parsed = await TryReadCuesAsync(candidate, source!, item, cancellationToken).ConfigureAwait(false);
            if (parsed is not null && parsed.Count >= MinimumCues)
            {
                tracks.Add((candidate, parsed));
            }
        }

        if (tracks.Count == 0)
        {
            logger.LogDebug("No embedded subtitle track for {Path} had enough cues to align against.", item.Path);
            return null;
        }

        var (bestStream, bestTrack, agreement) = SelectConsensus(tracks, item);

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

    private static List<MediaStream> SelectStreams(MediaSourceInfo? source)
    {
        if (source?.MediaStreams is null)
        {
            return [];
        }

        return source.MediaStreams
            .Where(s => s.Type == MediaStreamType.Subtitle)
            .Where(s => !s.IsExternal)
            .Where(s => s.Codec is not null && TextCodecs.Contains(s.Codec, StringComparer.OrdinalIgnoreCase))
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
