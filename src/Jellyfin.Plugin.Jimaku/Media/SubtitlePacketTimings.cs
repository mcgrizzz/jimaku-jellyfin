using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Timing;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Reads cue timings out of an image-based subtitle track without decoding a single picture.
/// </summary>
/// <remarks>
/// <para>
/// A PGS or VobSub track carries no text, which is why it cannot be read as a subtitle - but its
/// timing is not hidden or approximate. Every display set is a packet with a presentation
/// timestamp, and in a Matroska container it carries a block duration too. Only the words need
/// optical recognition; the timings are stated outright in the packet headers.
/// </para>
/// <para>
/// That matters because timing is the entire point here. Alignment compares cue structure and never
/// looks at what a cue says, so a picture subtitle is exactly as good a reference as a text one -
/// and disc rips, which are the releases most likely to carry only picture subtitles, are also the
/// ones where the audio fallback performs worst.
/// </para>
/// </remarks>
public sealed class SubtitlePacketTimings(IMediaEncoder mediaEncoder, ILogger<SubtitlePacketTimings> logger)
{
    /// <summary>
    /// Packets smaller than this are composition segments that clear the screen rather than draw on
    /// it. They mark the end of the cue before them, not the start of a new one.
    /// </summary>
    private const int MinimumPacketBytes = 48;

    /// <summary>Longest a cue may run when its end has to be inferred from the next one.</summary>
    private const double MaxInferredDurationSeconds = 10.0;

    /// <summary>
    /// Duration assumed when the container reports none at all, in seconds.
    /// </summary>
    /// <remarks>
    /// Extending every cue to the start of the next one is only sound when the missing durations
    /// are occasional. When none are present it is disastrous: dialogue cues sit a few seconds
    /// apart, so every cue stretches to meet its neighbour and the track becomes one continuous
    /// block. That signal is on almost all the time, and a signal that is always on carries no
    /// timing information at all - every alignment correlates about equally well with it. A typical
    /// subtitle duration keeps the gaps, which is where the information lives.
    /// </remarks>
    private const double NominalDurationSeconds = 2.0;

    /// <summary>Fraction of packets that must carry a duration before durations are trusted.</summary>
    private const double DurationCoverageThreshold = 0.5;

    /// <summary>Shorter than this and it is a flicker, not a subtitle.</summary>
    private const double MinimumDurationSeconds = 0.05;

    /// <summary>Gets a value indicating whether ffprobe is available to read packets with.</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(mediaEncoder.ProbePath);

    /// <summary>
    /// Turns ffprobe's packet listing into cues.
    /// </summary>
    /// <remarks>
    /// Separated from running the process so the parsing rules - which carry all the awkwardness -
    /// can be tested against real captures without a media file.
    /// </remarks>
    /// <param name="lines">CSV lines of <c>pts_time,duration_time,size</c>.</param>
    /// <returns>The cues, in order.</returns>
    public static CueTrack Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var starts = new List<(double Start, double Duration)>();

        foreach (var line in lines)
        {
            var fields = line.Split(',');
            if (fields.Length < 3)
            {
                continue;
            }

            if (!TryParse(fields[0], out var pts) || pts < 0)
            {
                continue;
            }

            var duration = TryParse(fields[1], out var parsedDuration) ? parsedDuration : 0;
            var size = int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize)
                ? parsedSize
                : 0;

            // An erase packet ends whatever is on screen. Recording it as a cue would double the
            // apparent cue count and halve every gap, which is precisely the structure alignment
            // keys on.
            if (size > 0 && size < MinimumPacketBytes)
            {
                if (starts.Count > 0 && starts[^1].Duration <= 0)
                {
                    var (start, _) = starts[^1];
                    starts[^1] = (start, Math.Max(0, pts - start));
                }

                continue;
            }

            starts.Add((pts, duration));
        }

        var withDuration = starts.Count(s => s.Duration > 0);
        var trustDurations = starts.Count > 0
            && (double)withDuration / starts.Count >= DurationCoverageThreshold;

        var cues = new List<Cue>(starts.Count);

        for (var i = 0; i < starts.Count; i++)
        {
            var (start, duration) = starts[i];

            if (duration <= 0)
            {
                var toNext = i + 1 < starts.Count ? starts[i + 1].Start - start : NominalDurationSeconds;

                duration = trustDurations
                    // Occasional gaps in otherwise good data: the cue runs until the next appears.
                    ? Math.Min(toNext, MaxInferredDurationSeconds)
                    // No durations anywhere. Assume a normal one rather than joining every cue to
                    // its neighbour, which would erase the gaps the alignment depends on.
                    : Math.Min(toNext, NominalDurationSeconds);
            }

            if (duration >= MinimumDurationSeconds)
            {
                cues.Add(new Cue(start, start + duration));
            }
        }

        return new CueTrack(cues);
    }

    /// <summary>
    /// Reads the cue timings of every subtitle stream in one pass.
    /// </summary>
    /// <remarks>
    /// One pass rather than one per track, for two reasons. Listing packets demuxes the whole
    /// container, so asking twice costs twice; and asking for a particular stream index means
    /// trusting that the index the library reports is the index ffprobe uses. Reading them all and
    /// keying on the index ffprobe itself reports removes that assumption - which mattered, because
    /// a file whose two subtitle tracks were requested individually returned 318 cues for one and
    /// nothing at all for the other.
    /// </remarks>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cues by stream index, empty when nothing could be read.</returns>
    public async Task<IReadOnlyDictionary<int, CueTrack>> ReadAllAsync(
        string mediaPath,
        CancellationToken cancellationToken)
    {
        var empty = new Dictionary<int, CueTrack>();

        if (!IsAvailable)
        {
            logger.LogDebug("ffprobe is not available; cannot read subtitle packet timings.");
            return empty;
        }

        var lines = await RunAsync(
            mediaPath,
            ["-select_streams", "s", "-show_entries", "packet=stream_index,pts_time,duration_time,size"],
            cancellationToken).ConfigureAwait(false);

        if (lines is null)
        {
            return empty;
        }

        var byStream = GroupByStream(lines);

        var tracks = new Dictionary<int, CueTrack>();
        foreach (var (index, packets) in byStream)
        {
            var track = Parse(packets);
            tracks[index] = track;

            logger.LogInformation(
                "Stream {Index} of {Path}: {Packets} packets, {Cues} cues, median {Median:0.00}s, on screen {Duty:P0}.",
                index,
                mediaPath,
                packets.Count,
                track.Count,
                MedianDuration(track),
                DutyCycle(track));
        }

        return tracks;
    }

    /// <summary>
    /// Splits a combined packet listing by the stream index ffprobe reported for each packet.
    /// </summary>
    /// <param name="lines">CSV lines of <c>stream_index,pts_time,duration_time,size</c>.</param>
    /// <returns>The remaining fields of each line, grouped by stream.</returns>
    public static Dictionary<int, List<string>> GroupByStream(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var byStream = new Dictionary<int, List<string>>();

        foreach (var line in lines)
        {
            var comma = line.IndexOf(',', StringComparison.Ordinal);
            if (comma <= 0
                || !int.TryParse(line[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                continue;
            }

            if (!byStream.TryGetValue(index, out var packets))
            {
                packets = [];
                byStream[index] = packets;
            }

            packets.Add(line[(comma + 1)..]);
        }

        return byStream;
    }

    private async Task<List<string>?> RunAsync(
        string mediaPath,
        string[] selection,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            logger.LogDebug("ffprobe is not available; cannot read subtitle packet timings.");
            return null;
        }

        var startInfo = new ProcessStartInfo(mediaEncoder.ProbePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Packet headers only: no decoding, no image handling, and nothing is written to disk.
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");

        foreach (var argument in selection)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("csv=p=0");
        startInfo.ArgumentList.Add(mediaPath);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var lines = new List<string>(2048);

            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lines.Add(line);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = await stderrTask.ConfigureAwait(false);
                logger.LogWarning(
                    "Reading subtitle packet timings from {Path} failed: {Error}",
                    mediaPath,
                    error.Trim());
                return null;
            }

            return lines;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read subtitle packet timings from {Path}.", mediaPath);
            return null;
        }
    }

    /// <summary>Median cue length, which reveals a track whose durations were fabricated.</summary>
    /// <param name="track">The cues.</param>
    /// <returns>The median duration in seconds.</returns>
    public static double MedianDuration(CueTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Count == 0)
        {
            return 0;
        }

        var durations = track.Cues.Select(c => c.DurationSeconds).OrderBy(d => d).ToArray();
        return durations[durations.Length / 2];
    }

    /// <summary>
    /// Share of the episode with something on screen. A track approaching 1 is one continuous
    /// block, and correlates about equally well with everything.
    /// </summary>
    /// <param name="track">The cues.</param>
    /// <returns>The fraction between 0 and 1.</returns>
    public static double DutyCycle(CueTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (track.Count == 0)
        {
            return 0;
        }

        var span = track.LastEndSeconds - track.FirstStartSeconds;
        if (span <= 0)
        {
            return 0;
        }

        return Math.Min(1.0, track.Cues.Sum(c => c.DurationSeconds) / span);
    }

    private static bool TryParse(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
