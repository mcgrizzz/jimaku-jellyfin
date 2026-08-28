using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// Options controlling the global (scale + offset) alignment search.
/// </summary>
public sealed class LinearFitOptions
{
    /// <summary>Gets or sets the largest absolute offset to search, in seconds.</summary>
    public double MaxSearchOffsetSeconds { get; set; } = 60;

    /// <summary>Gets or sets the guard half-width used when locating the second peak, in seconds.</summary>
    public double PeakGuardSeconds { get; set; } = 1.0;

    /// <summary>Gets or sets the cue duration clip applied before binning, in seconds.</summary>
    public double MaxCueSeconds { get; set; } = 10.0;

    /// <summary>Gets or sets a value indicating whether framerate ratios are tested at all.</summary>
    public bool EnableFramerateSearch { get; set; } = true;

    /// <summary>
    /// Gets or sets how much better a scaled fit must score than the unscaled one before the
    /// framerate hypothesis is accepted. Without a margin, floating-point noise would let a scale
    /// win by a hair and produce a needless, riskier correction.
    /// </summary>
    public double MinScaleImprovement { get; set; } = 0.02;
}

/// <summary>
/// A single (scale, offset) hypothesis and how well it scored.
/// </summary>
/// <param name="Scale">The framerate ratio tested.</param>
/// <param name="OffsetSeconds">Best offset found for that scale.</param>
/// <param name="Correlation">Baseline-corrected correlation coefficient.</param>
/// <param name="PeakRatio">Peak-to-second-peak ratio.</param>
public readonly record struct LinearFit(double Scale, double OffsetSeconds, double Correlation, double PeakRatio)
{
    /// <summary>Gets the transform this fit implies.</summary>
    public TimingTransform Transform => new(Scale, OffsetSeconds);
}

/// <summary>
/// Searches a small grid of framerate ratios, cross-correlating at each, and returns the best
/// global linear fit.
/// </summary>
/// <remarks>
/// A framerate mismatch is a linear time scaling, not a shift, so no single offset can fix it. Every
/// mainstream tool (ffsubsync, alass, autosubsync) handles this the same way: try a short fixed list
/// of real-world ratios and keep whichever correlates best. Continuous optimization buys nothing,
/// because the underlying cause is always one of a handful of standards conversions.
/// </remarks>
public sealed class LinearFitSearch
{
    /// <summary>
    /// The framerate ratios worth testing, with their reciprocals.
    /// <c>1001/1000</c> covers 24/23.976, 30/29.97 and 60/59.94 (NTSC pulldown, all the same ratio);
    /// <c>25/24</c> is the PAL speedup; <c>25/23.976</c> is PAL against NTSC-film.
    /// </summary>
    public static readonly double[] DefaultScales =
    [
        1.0,
        1001.0 / 1000.0,
        1000.0 / 1001.0,
        25.0 / 24.0,
        24.0 / 25.0,
        25.0 / 23.976,
        23.976 / 25.0,
    ];

    private readonly LinearFitOptions _options;

    /// <summary>Initializes a new instance of the <see cref="LinearFitSearch"/> class.</summary>
    /// <param name="options">Search options, or null for defaults.</param>
    public LinearFitSearch(LinearFitOptions? options = null)
    {
        _options = options ?? new LinearFitOptions();
    }

    /// <summary>
    /// Finds the best linear fit of <paramref name="probe"/> onto <paramref name="reference"/>.
    /// </summary>
    /// <param name="reference">Binned reference activity from the local media.</param>
    /// <param name="probe">Cue timings of the candidate subtitle.</param>
    /// <param name="scales">Ratios to test, or null for <see cref="DefaultScales"/>.</param>
    /// <returns>
    /// Every fit tested, best first. Empty when there was nothing to correlate. Returning all of
    /// them lets the caller see how close the runner-up was, which is itself a confidence signal.
    /// </returns>
    /// <param name="onsets">
    /// Compare only the moments cues begin, ignoring how long each stays on screen. Use when the
    /// reference was binned the same way.
    /// </param>
    public IReadOnlyList<LinearFit> Search(
        ActivitySignal reference,
        CueTrack probe,
        IReadOnlyList<double>? scales = null,
        bool onsets = false)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(probe);

        if (reference.Length == 0 || reference.Energy <= 0 || probe.Count == 0)
        {
            return Array.Empty<LinearFit>();
        }

        var candidateScales = scales ?? (_options.EnableFramerateSearch ? DefaultScales : [1.0]);

        var maxScale = 1.0;
        foreach (var scale in candidateScales)
        {
            maxScale = Math.Max(maxScale, scale);
        }

        // Every scaled probe must fit the one padded transform size, so budget for the largest.
        var maxProbeLength = (int)Math.Ceiling(probe.LastEndSeconds * maxScale * ActivitySignal.BinsPerSecond) + 4;
        var correlator = new CrossCorrelator(reference, maxProbeLength);

        var maxLagBins = (int)Math.Round(_options.MaxSearchOffsetSeconds * ActivitySignal.BinsPerSecond);
        var guardBins = (int)Math.Round(_options.PeakGuardSeconds * ActivitySignal.BinsPerSecond);

        var results = new List<LinearFit>(candidateScales.Count);
        foreach (var scale in candidateScales)
        {
            var probeSignal = onsets
                ? ActivitySignal.FromCueStarts(probe, totalSeconds: 0, scale: scale)
                : ActivitySignal.FromCues(
                    probe,
                    totalSeconds: 0,
                    scale: scale,
                    maxCueSeconds: _options.MaxCueSeconds);

            if (probeSignal.Length > correlator.Size)
            {
                continue;
            }

            var peak = correlator.Correlate(probeSignal, maxLagBins, guardBins);
            results.Add(new LinearFit(scale, peak.LagSeconds, peak.Correlation, peak.PeakRatio));
        }

        if (results.Count == 0)
        {
            return Array.Empty<LinearFit>();
        }

        results.Sort(static (a, b) => b.Correlation.CompareTo(a.Correlation));

        // Prefer the unscaled fit unless a scaled one is meaningfully better. A framerate correction
        // rewrites inline ASS tag timings as well, so it should not be chosen on a coin flip.
        var best = results[0];
        if (Math.Abs(best.Scale - 1.0) > 1e-9)
        {
            foreach (var fit in results)
            {
                if (Math.Abs(fit.Scale - 1.0) < 1e-9)
                {
                    if (best.Correlation - fit.Correlation < _options.MinScaleImprovement)
                    {
                        results.Remove(fit);
                        results.Insert(0, fit);
                    }

                    break;
                }
            }
        }

        return results;
    }
}
