using System;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// A dependency-free voice activity detector based on energy within the speech band, combined with
/// a spectral flatness test.
/// </summary>
/// <remarks>
/// <para>
/// Plain energy thresholding, and ffmpeg's <c>silencedetect</c> with it, is close to useless on
/// anime: the mix is scored almost continuously, so nothing is ever silent and the resulting
/// activity track has no structure to correlate against.
/// </para>
/// <para>
/// Restricting to roughly 300-3400 Hz and additionally requiring the spectrum to be *peaky* rather
/// than flat helps, because voiced speech has strong harmonic structure while music beds and
/// ambience are comparatively flat. It is still clearly weaker than a trained neural detector, and
/// that is acceptable here only because the confidence gate turns a weak reference into a decline
/// rather than into a bad correction.
/// </para>
/// </remarks>
public sealed class BandEnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private const int Fft = 512;
    private const int Rate = 16000;

    private readonly double[] _re = new double[Fft];
    private readonly double[] _im = new double[Fft];
    private readonly double[] _window = new double[Fft];
    private readonly int _lowBin;
    private readonly int _highBin;

    private double _noiseFloor = 1e-6;

    /// <summary>Initializes a new instance of the <see cref="BandEnergyVoiceActivityDetector"/> class.</summary>
    public BandEnergyVoiceActivityDetector()
    {
        for (var i = 0; i < Fft; i++)
        {
            // Hann window, to keep spectral leakage from smearing the band edges.
            _window[i] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (Fft - 1)));
        }

        _lowBin = (int)Math.Floor(300.0 * Fft / Rate);
        _highBin = (int)Math.Ceiling(3400.0 * Fft / Rate);
    }

    /// <inheritdoc />
    public string Name => "band-energy";

    /// <inheritdoc />
    public int SampleRate => Rate;

    /// <inheritdoc />
    public int FrameSamples => Fft;

    /// <inheritdoc />
    public float ScoreFrame(ReadOnlySpan<float> samples)
    {
        var count = Math.Min(samples.Length, Fft);
        for (var i = 0; i < count; i++)
        {
            _re[i] = samples[i] * _window[i];
            _im[i] = 0;
        }

        for (var i = count; i < Fft; i++)
        {
            _re[i] = 0;
            _im[i] = 0;
        }

        Timing.RealFft.Forward(_re, _im);

        var bandEnergy = 0.0;
        var logSum = 0.0;
        var linearSum = 0.0;
        var bins = 0;

        for (var i = _lowBin; i <= _highBin && i < Fft / 2; i++)
        {
            var power = (_re[i] * _re[i]) + (_im[i] * _im[i]);
            bandEnergy += power;
            logSum += Math.Log(power + 1e-12);
            linearSum += power;
            bins++;
        }

        if (bins == 0 || bandEnergy <= 0)
        {
            return 0;
        }

        // Track a slow-moving floor so the threshold adapts to each file's own level rather than
        // to an absolute dB figure that means nothing across different masters.
        _noiseFloor = bandEnergy < _noiseFloor
            ? (0.95 * _noiseFloor) + (0.05 * bandEnergy)
            : (0.999 * _noiseFloor) + (0.001 * bandEnergy);

        var snr = bandEnergy / Math.Max(_noiseFloor, 1e-12);

        // Geometric over arithmetic mean: near 1 for noise-like or flat spectra, well below 1 when
        // harmonics dominate, as they do in voiced speech.
        var flatness = Math.Exp(logSum / bins) / (linearSum / bins);

        var loudness = Math.Clamp(Math.Log10(snr) / 1.5, 0, 1);
        var peakiness = Math.Clamp((0.5 - flatness) / 0.35, 0, 1);

        return (float)Math.Clamp((0.6 * loudness) + (0.4 * peakiness), 0, 1);
    }

    /// <inheritdoc />
    public void Reset() => _noiseFloor = 1e-6;

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing unmanaged to release.
    }
}
