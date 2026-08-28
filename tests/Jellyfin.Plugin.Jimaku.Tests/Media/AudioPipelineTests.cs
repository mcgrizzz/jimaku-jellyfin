using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Timing;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Media;

/// <summary>
/// Exercises the audio fallback end to end: ffmpeg decode, voice activity detection, binning, and
/// correlation. Requires ffmpeg on PATH.
/// </summary>
[Trait("Category", "RequiresFfmpeg")]
public class AudioPipelineTests(ITestOutputHelper output)
{
    private const int Rate = 48000;

    private static string? FindFfmpeg()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var directory in paths)
        {
            foreach (var name in new[] { "ffmpeg", "ffmpeg.exe" })
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Writes a mono 16-bit WAV that imitates an anime mix: harmonic-rich "speech" bursts over a
    /// continuous low-level musical bed. The bed matters, because it is exactly what makes silence
    /// detection useless on this material.
    /// </summary>
    private static void WriteWav(string path, IReadOnlyList<Cue> speech, double totalSeconds)
    {
        var samples = new short[(int)(totalSeconds * Rate)];
        var random = new Random(11);

        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / Rate;

            // A quiet, continuous score plus a little noise: never silent.
            var value = 0.05 * Math.Sin(2 * Math.PI * 440 * t)
                      + 0.03 * Math.Sin(2 * Math.PI * 660 * t)
                      + 0.01 * (random.NextDouble() - 0.5);

            foreach (var cue in speech)
            {
                if (t < cue.StartSeconds || t >= cue.EndSeconds)
                {
                    continue;
                }

                // A voiced fundamental with harmonics, which is what separates speech from a
                // musical bed spectrally.
                var envelope = 0.35 * (1 + (0.4 * Math.Sin(2 * Math.PI * 4 * t)));
                for (var harmonic = 1; harmonic <= 12; harmonic++)
                {
                    value += envelope / harmonic * Math.Sin(2 * Math.PI * 150 * harmonic * t);
                }

                break;
            }

            samples[i] = (short)(Math.Clamp(value, -1, 1) * 20000);
        }

        using var writer = new BinaryWriter(File.Create(path));
        var dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
    }

    private static List<Cue> SpeechPattern()
    {
        var random = new Random(3);
        var cues = new List<Cue>();
        var t = 2.0;
        while (t < 115)
        {
            var duration = 1.0 + (random.NextDouble() * 2.0);
            cues.Add(new Cue(t, t + duration));
            t += duration + 0.8 + (random.NextDouble() * 2.0);
        }

        return cues;
    }

    [Fact]
    public async Task AudioFallback_RecoversAKnownOffsetFromVoiceActivityAlone()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            output.WriteLine("ffmpeg is not on PATH; skipping.");
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"jimaku-vad-{Guid.NewGuid():N}.wav");
        var speech = SpeechPattern();

        try
        {
            WriteWav(path, speech, 120);

            var encoder = new Mock<IMediaEncoder>();
            encoder.SetupGet(e => e.EncoderPath).Returns(ffmpeg!);

            var runner = new FfmpegRunner(encoder.Object, NullLogger<FfmpegRunner>.Instance);
            using var detector = new BandEnergyVoiceActivityDetector();

            var probabilities = new List<float>();
            await foreach (var frame in runner.DecodeFramesAsync(path, 0, detector.FrameSamples, CancellationToken.None))
            {
                probabilities.Add(detector.ScoreFrame(frame));
            }

            Assert.NotEmpty(probabilities);

            var frameSeconds = (double)detector.FrameSamples / detector.SampleRate;
            var reference = ActivitySignal.FromProbabilities(probabilities, frameSeconds);

            var duty = reference.Energy / reference.Length;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"decoded {probabilities.Count} frames, duty cycle {duty:P1}"));

            // The detector must find structure, not just declare everything speech or nothing.
            Assert.InRange(duty, 0.05, 0.9);

            // A subtitle for this audio, sitting 4.2 s early. The aligner should say +4.2.
            const double Offset = 4.2;
            var probe = new CueTrack(speech.Select(c => new Cue(c.StartSeconds - Offset, c.EndSeconds - Offset)));

            var fit = new LinearFitSearch().Search(reference, probe)[0];
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"recovered offset {fit.OffsetSeconds:0.000}s r={fit.Correlation:0.000} ratio={fit.PeakRatio:0.00}"));

            Assert.InRange(fit.OffsetSeconds, Offset - 0.35, Offset + 0.35);
            Assert.True(fit.Correlation > 0.4, $"correlation was {fit.Correlation}");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Decode_MissingFfmpeg_YieldsNothingRatherThanThrowing()
    {
        var encoder = new Mock<IMediaEncoder>();
        encoder.SetupGet(e => e.EncoderPath).Returns(string.Empty);

        var runner = new FfmpegRunner(encoder.Object, NullLogger<FfmpegRunner>.Instance);
        Assert.False(runner.IsAvailable);

        var frames = 0;
        await foreach (var _ in runner.DecodeFramesAsync("/nonexistent.mkv", 0, 512, CancellationToken.None))
        {
            frames++;
        }

        Assert.Equal(0, frames);
    }
}
