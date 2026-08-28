using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// A speech-activity track discretized into fixed 10 ms bins.
/// </summary>
/// <remarks>
/// 10 ms matches ffsubsync's <c>SAMPLE_RATE = 100</c>. It sits comfortably below the ~100 ms
/// threshold at which a viewer notices desync, and keeps a 24-minute episode to ~144k bins, so a
/// zero-padded FFT stays at 2^19 points.
/// </remarks>
public sealed class ActivitySignal
{
    /// <summary>Bins per second.</summary>
    public const int BinsPerSecond = 100;

    /// <summary>Duration of one bin, in seconds.</summary>
    public const double BinSeconds = 1.0 / BinsPerSecond;

    private double[]? _prefixSums;

    /// <summary>Initializes a new instance of the <see cref="ActivitySignal"/> class.</summary>
    /// <param name="bins">The bin values. Taken by reference, not copied.</param>
    public ActivitySignal(double[] bins)
    {
        ArgumentNullException.ThrowIfNull(bins);
        Bins = bins;
        var energy = 0.0;
        for (var i = 0; i < bins.Length; i++)
        {
            energy += bins[i] * bins[i];
        }

        Energy = energy;
    }

    /// <summary>Gets the bin values.</summary>
    public double[] Bins { get; }

    /// <summary>Gets the number of bins.</summary>
    public int Length => Bins.Length;

    /// <summary>
    /// Gets the sum of squared bin values. For a binary signal this equals the number of active
    /// bins, and it is the normalization term of the cross-correlation coefficient.
    /// </summary>
    public double Energy { get; }

    /// <summary>Gets the total duration covered, in seconds.</summary>
    public double DurationSeconds => Bins.Length * BinSeconds;

    /// <summary>
    /// Gets an inclusive prefix-sum array of length <c>Length + 1</c>, built on first use.
    /// Lets the split aligner rate a cue at any offset in constant time.
    /// </summary>
    public double[] PrefixSums
    {
        get
        {
            if (_prefixSums is null)
            {
                var sums = new double[Bins.Length + 1];
                for (var i = 0; i < Bins.Length; i++)
                {
                    sums[i + 1] = sums[i] + Bins[i];
                }

                _prefixSums = sums;
            }

            return _prefixSums;
        }
    }

    /// <summary>
    /// Sums bin values over the half-open bin range <paramref name="startBin"/>..<paramref name="endBin"/>,
    /// clamping to the signal bounds. Out-of-range regions contribute zero.
    /// </summary>
    /// <param name="startBin">Inclusive start bin.</param>
    /// <param name="endBin">Exclusive end bin.</param>
    /// <returns>The sum of bin values in range.</returns>
    public double SumRange(int startBin, int endBin)
    {
        var sums = PrefixSums;
        var lo = Math.Clamp(startBin, 0, Bins.Length);
        var hi = Math.Clamp(endBin, 0, Bins.Length);
        return hi <= lo ? 0 : sums[hi] - sums[lo];
    }

    /// <summary>
    /// Bins a cue track into a binary activity signal.
    /// </summary>
    /// <param name="track">The cues to bin.</param>
    /// <param name="totalSeconds">
    /// Length of the signal. When zero or negative, the track's own last cue end is used.
    /// </param>
    /// <param name="scale">
    /// Linear time scale applied to every cue before binning, used to test framerate-ratio
    /// hypotheses. 1.0 leaves timings untouched.
    /// </param>
    /// <param name="maxCueSeconds">
    /// Cues longer than this are clipped. A handful of very long cues (signs, credit blocks,
    /// karaoke backgrounds) would otherwise dominate the correlation. ffsubsync clips at 10 s.
    /// </param>
    /// <returns>The binned signal.</returns>
    public static ActivitySignal FromCues(
        CueTrack track,
        double totalSeconds = 0,
        double scale = 1.0,
        double maxCueSeconds = 10.0)
    {
        ArgumentNullException.ThrowIfNull(track);

        var span = totalSeconds > 0 ? totalSeconds : track.LastEndSeconds * scale;
        var length = (int)Math.Ceiling(span * BinsPerSecond) + 2;
        if (length < 2)
        {
            return new ActivitySignal(new double[2]);
        }

        var bins = new double[length];
        foreach (var cue in track.Cues)
        {
            var start = cue.StartSeconds * scale;
            var end = Math.Min(cue.EndSeconds * scale, start + maxCueSeconds);
            var startBin = (int)Math.Round(start * BinsPerSecond);
            var endBin = (int)Math.Round(end * BinsPerSecond);
            if (endBin <= startBin)
            {
                endBin = startBin + 1;
            }

            startBin = Math.Clamp(startBin, 0, length);
            endBin = Math.Clamp(endBin, 0, length);
            for (var i = startBin; i < endBin; i++)
            {
                bins[i] = 1.0;
            }
        }

        return new ActivitySignal(bins);
    }

    /// <summary>
    /// Builds a signal from per-frame speech probabilities emitted at an arbitrary rate, resampling
    /// onto the 10 ms grid and thresholding.
    /// </summary>
    /// <remarks>
    /// Silero emits one probability per 512-sample window at 16 kHz (32 ms), so its output never
    /// lines up with the 10 ms grid and has to be resampled.
    /// </remarks>
    /// <param name="probabilities">Speech probability per frame, in order.</param>
    /// <param name="frameSeconds">Duration each probability covers.</param>
    /// <param name="threshold">Probability at or above which a bin counts as speech.</param>
    /// <returns>The binned signal.</returns>
    public static ActivitySignal FromProbabilities(
        IReadOnlyList<float> probabilities,
        double frameSeconds,
        double threshold = 0.5)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameSeconds);

        var length = (int)Math.Ceiling(probabilities.Count * frameSeconds * BinsPerSecond) + 2;
        var bins = new double[Math.Max(length, 2)];
        for (var i = 0; i < bins.Length; i++)
        {
            var frame = (int)((i * BinSeconds) / frameSeconds);
            if (frame >= 0 && frame < probabilities.Count && probabilities[frame] >= threshold)
            {
                bins[i] = 1.0;
            }
        }

        return new ActivitySignal(bins);
    }
}
