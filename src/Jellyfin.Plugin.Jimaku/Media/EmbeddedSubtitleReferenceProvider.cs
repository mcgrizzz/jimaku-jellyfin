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

        // Choose by cue density, not by language.
        //
        // Language is irrelevant to timing - an English or Chinese dialogue track marks the same
        // moments as the Japanese audio. What is *not* irrelevant is whether the track is full
        // dialogue or just signs and songs. A signs track has a few dozen cues spread thinly across
        // the episode, which produces a nearly flat correlation surface: every candidate then scores
        // a uniqueness around 1.0 and is declined, including the correct one.
        //
        // Reading several tracks is cheap because Jellyfin extracts every text track in a single
        // ffmpeg pass, so all but the first are cache hits.
        MediaStream? bestStream = null;
        CueTrack? bestTrack = null;

        foreach (var candidate in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parsed = await TryReadCuesAsync(candidate, source!, item, cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                continue;
            }

            logger.LogDebug(
                "Stream {Index} ({Language} {Codec}) of {Path} has {Count} usable cues.",
                candidate.Index,
                candidate.Language ?? "und",
                candidate.Codec,
                item.Path,
                parsed.Count);

            if (bestTrack is null || parsed.Count > bestTrack.Count)
            {
                bestStream = candidate;
                bestTrack = parsed;
            }
        }

        if (bestStream is null || bestTrack is null || bestTrack.Count < MinimumCues)
        {
            logger.LogDebug(
                "No embedded subtitle track for {Path} had enough cues to align against; best had {Count}.",
                item.Path,
                bestTrack?.Count ?? 0);
            return null;
        }

        logger.LogInformation(
            "Timing reference for {Path}: embedded stream {Index} ({Language} {Codec}) with {Count} cues.",
            item.Path,
            bestStream.Index,
            bestStream.Language ?? "und",
            bestStream.Codec,
            bestTrack.Count);

        var duration = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds
            : bestTrack.LastEndSeconds;

        var language = string.IsNullOrEmpty(bestStream.Language) ? bestStream.Codec : bestStream.Language;
        var description = $"embedded {language} subtitles ({bestTrack.Count} cues)";

        return new ReferenceTrack(ActivitySignal.FromCues(bestTrack, duration), description, true, bestTrack);
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
