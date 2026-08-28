using System;
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
/// The track's language is irrelevant: an English or signs-only track marks the same moments in
/// time as the Japanese dialogue it accompanies.
/// </remarks>
public sealed class EmbeddedSubtitleReferenceProvider(
    IMediaSourceManager mediaSourceManager,
    ISubtitleEncoder subtitleEncoder,
    ILogger<EmbeddedSubtitleReferenceProvider> logger) : IReferenceTrackProvider
{
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

        var stream = SelectStream(source);
        if (stream is null)
        {
            logger.LogDebug("{Path} has no embedded text subtitle track to use as a timing reference.", item.Path);
            return null;
        }

        string path;
        try
        {
            // Extracts into the server's own subtitle cache and hands back a real file, reusing
            // Jellyfin's extraction and locking rather than duplicating it.
            path = await subtitleEncoder
                .GetSubtitleFilePath(stream, source!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Extracting embedded subtitle stream {Index} from {Path} failed.", stream.Index, item.Path);
            return null;
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        CueTrack track;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            track = SubtitleDocument.Parse(bytes).ToCueTrack();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Parsing the extracted subtitle at {Path} failed.", path);
            return null;
        }

        if (track.Count < 10)
        {
            logger.LogDebug(
                "The embedded subtitle track for {Path} has only {Count} usable cues, which is too few to align against.",
                item.Path,
                track.Count);
            return null;
        }

        var duration = item.RunTimeTicks.HasValue
            ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds
            : track.LastEndSeconds;

        var description = string.IsNullOrEmpty(stream.Language)
            ? $"embedded {stream.Codec} subtitles"
            : $"embedded {stream.Language} subtitles";

        return new ReferenceTrack(ActivitySignal.FromCues(track, duration), description, true);
    }

    private static MediaStream? SelectStream(MediaSourceInfo? source)
    {
        if (source?.MediaStreams is null)
        {
            return null;
        }

        var candidates = source.MediaStreams
            .Where(s => s.Type == MediaStreamType.Subtitle)
            .Where(s => !s.IsExternal)
            .Where(s => s.Codec is not null && TextCodecs.Contains(s.Codec, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // Any language works as a timing reference, but prefer Japanese, then English, purely
        // because full dialogue tracks have denser and more evenly spread cues than signs-only ones.
        return candidates.FirstOrDefault(s => string.Equals(s.Language, "jpn", StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(s => string.Equals(s.Language, "eng", StringComparison.OrdinalIgnoreCase))
            ?? candidates[0];
    }
}
