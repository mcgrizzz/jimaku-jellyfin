using System;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// FFT cross-correlation of a probe signal against a fixed reference.
/// </summary>
/// <remarks>
/// The reference is transformed once at construction and reused for every probe, which matters
/// because the framerate search correlates the same reference against seven differently scaled
/// versions of the same subtitle track.
/// </remarks>
public sealed class CrossCorrelator
{
    private readonly double[] _refRe;
    private readonly double[] _refIm;
    private readonly double _refEnergy;
    private readonly int _referenceLength;
    private readonly int _size;

    /// <summary>Initializes a new instance of the <see cref="CrossCorrelator"/> class.</summary>
    /// <param name="reference">The reference signal.</param>
    /// <param name="maxProbeLength">
    /// Longest probe that will be correlated. The transform is padded to at least the sum of both
    /// lengths so the circular correlation equals the linear one.
    /// </param>
    public CrossCorrelator(ActivitySignal reference, int maxProbeLength)
    {
        ArgumentNullException.ThrowIfNull(reference);

        _size = RealFft.NextPowerOfTwo(reference.Length + Math.Max(maxProbeLength, 1) + 1);
        _refRe = new double[_size];
        _refIm = new double[_size];
        Array.Copy(reference.Bins, _refRe, reference.Length);
        _refEnergy = reference.Energy;
        _referenceLength = reference.Length;
        RealFft.Forward(_refRe, _refIm);
    }

    /// <summary>Gets the padded transform size.</summary>
    public int Size => _size;

    /// <summary>
    /// Correlates <paramref name="probe"/> against the reference and reports the best lag.
    /// </summary>
    /// <param name="probe">The probe signal.</param>
    /// <param name="maxLagBins">Largest absolute lag to consider, in bins.</param>
    /// <param name="guardBins">
    /// Half-width of the window around the peak excluded when looking for the second peak. A
    /// correct alignment produces a plateau a second or so wide; without a guard the "second peak"
    /// would just be the shoulder of the first.
    /// </param>
    /// <returns>The best lag and its confidence measures.</returns>
    public CorrelationPeak Correlate(ActivitySignal probe, int maxLagBins, int guardBins)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.Length == 0 || probe.Energy <= 0 || _refEnergy <= 0)
        {
            return CorrelationPeak.None;
        }

        if (probe.Length > _size)
        {
            throw new ArgumentException("Probe is longer than the configured transform size.", nameof(probe));
        }

        var re = new double[_size];
        var im = new double[_size];
        Array.Copy(probe.Bins, re, probe.Length);
        RealFft.Forward(re, im);

        // Multiply by the conjugate of the probe spectrum: IFFT(R * conj(P))[lag] is
        // sum_t ref[t] * probe[t - lag], i.e. the correlation at that lag.
        for (var i = 0; i < _size; i++)
        {
            var pr = re[i];
            var pi = -im[i];
            var rr = _refRe[i];
            var ri = _refIm[i];
            re[i] = (rr * pr) - (ri * pi);
            im[i] = (rr * pi) + (ri * pr);
        }

        RealFft.Inverse(re, im);

        var limit = Math.Min(maxLagBins, _size / 2 - 1);
        var bestLag = 0;
        var bestValue = double.NegativeInfinity;

        for (var lag = -limit; lag <= limit; lag++)
        {
            var value = re[IndexForLag(lag)];
            if (value > bestValue)
            {
                bestValue = value;
                bestLag = lag;
            }
        }

        if (bestValue <= 0)
        {
            return CorrelationPeak.None;
        }

        var second = 0.0;
        for (var lag = -limit; lag <= limit; lag++)
        {
            if (Math.Abs(lag - bestLag) <= guardBins)
            {
                continue;
            }

            var value = re[IndexForLag(lag)];
            if (value > second)
            {
                second = value;
            }
        }

        // Corrected against chance overlap; see CorrelationScore for why raw NCC will not do.
        // The population size is the union of both spans: when a subtitle runs past the end of the
        // video (or stops short) that genuinely is a partial match, and the score should say so.
        var window = Math.Max(_referenceLength, probe.Length);
        var correlation = CorrelationScore.Compute(bestValue, _refEnergy, probe.Energy, window);

        // With no competing peak at all the alignment is maximally unique; report a large finite
        // ratio rather than infinity so callers can compare and serialize it.
        var ratio = second > 0 ? bestValue / second : 999.0;

        return new CorrelationPeak(bestLag, bestValue, second, correlation, ratio);
    }

    private int IndexForLag(int lag) => lag >= 0 ? lag : _size + lag;
}
