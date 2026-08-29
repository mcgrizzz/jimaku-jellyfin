using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Timing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Builds a reference by running voice activity detection over the Japanese audio track.
/// </summary>
/// <remarks>
/// Used only when the media has no embedded subtitle track. It is the weaker path by a wide margin,
/// particularly for anime, where a near-continuous musical score gives energy-based detection very
/// little to work with. That weakness is contained rather than hidden: a poor reference produces a
/// flat correlation surface, which fails the confidence gate, and the episode is declined instead of
/// being given a plausible-looking but wrong correction.
/// </remarks>
public sealed class AudioActivityReferenceProvider(
    IMediaSourceManager mediaSourceManager,
    FfmpegRunner ffmpeg,
    IVoiceActivityDetectorFactory detectorFactory,
    ILogger<AudioActivityReferenceProvider> logger) : IReferenceTrackProvider
{
    /// <inheritdoc />
    public async Task<ReferenceTrack?> TryGetAsync(BaseItem item, ReferenceReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        ArgumentNullException.ThrowIfNull(report);

        if (!ffmpeg.IsAvailable)
        {
            report.Note = "ffmpeg is not available, so the audio could not be analysed either.";
            return null;
        }

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

        var stream = SelectAudioStream(source);
        if (stream is null)
        {
            report.Note = "This file has no audio stream to analyse.";
            return null;
        }

        report.AudioTrack = string.Create(
            CultureInfo.InvariantCulture,
            $"stream {stream.Index} ({stream.Language ?? "und"} {stream.Codec})");

        using var detector = detectorFactory.Create();
        detector.Reset();
        report.Detector = detector.Name;

        var probabilities = new List<float>(4096);
        var frameSeconds = (double)detector.FrameSamples / detector.SampleRate;

        try
        {
            await foreach (var frame in ffmpeg
                .DecodeFramesAsync(item.Path, stream.Index, detector.FrameSamples, cancellationToken)
                .ConfigureAwait(false))
            {
                probabilities.Add(detector.ScoreFrame(frame));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Voice activity analysis of {Path} failed.", item.Path);
            return null;
        }

        if (probabilities.Count < 100)
        {
            logger.LogDebug("Voice activity analysis of {Path} produced too little data.", item.Path);
            return null;
        }

        var signal = ActivitySignal.FromProbabilities(probabilities, frameSeconds);

        // An activity track that is almost entirely on, or almost entirely off, carries no timing
        // information: correlating against it would return an arbitrary lag with a flat surface.
        var duty = signal.Energy / Math.Max(signal.Length, 1);
        if (duty is < 0.02 or > 0.95)
        {
            logger.LogInformation(
                "Voice activity analysis of {Path} produced a {Duty:P0} duty cycle, which is too uniform to align against.",
                item.Path,
                duty);

            report.Note = string.Create(
                CultureInfo.InvariantCulture,
                $"Voice activity analysis found speech {duty:P0} of the time, which is too uniform to align against.");
            return null;
        }

        logger.LogDebug(
            "Voice activity analysis of {Path} using {Detector}: {Frames} frames, {Duty:P0} speech.",
            item.Path,
            detector.Name,
            probabilities.Count,
            duty);

        var description = string.Create(
            CultureInfo.InvariantCulture,
            $"{detector.Name} voice activity on audio {report.AudioTrack}");

        report.Chosen = description;
        report.FromSubtitles = false;

        // Worth saying plainly rather than leaving to be inferred from a low correlation. Anime is
        // scored almost continuously, so energy-based detection has very little silence to key on -
        // this is the weakest reference the plugin has, and a decline against it usually means the
        // reference was poor rather than the subtitle wrong.
        if (!detector.IsNeural)
        {
            report.Note =
                "Energy-based detection is unreliable for anime, where a near-continuous score leaves little silence to key on. A neural model (Silero) gives a far better reference.";
        }

        return new ReferenceTrack(signal, description);
    }

    private static MediaStream? SelectAudioStream(MediaSourceInfo? source)
    {
        if (source?.MediaStreams is null)
        {
            return null;
        }

        var audio = source.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        return audio.FirstOrDefault(s => string.Equals(s.Language, "jpn", StringComparison.OrdinalIgnoreCase))
            ?? audio.FirstOrDefault(s => s.IsDefault)
            ?? audio[0];
    }
}
