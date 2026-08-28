namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// The outcome of correlating a probe signal against a reference at every candidate lag.
/// </summary>
/// <param name="LagBins">
/// Best lag in 10 ms bins. Positive means the probe must move later to line up with the reference.
/// </param>
/// <param name="Peak">Raw correlation value at the best lag: overlapping active bins.</param>
/// <param name="SecondPeak">Highest correlation outside the guard window around the best lag.</param>
/// <param name="Correlation">
/// Baseline-corrected correlation coefficient in roughly [-1,1], where 1 is a perfect match and 0
/// is chance.
/// </param>
/// <param name="PeakRatio">
/// Peak divided by second peak. Measures how *unique* the alignment is: a correct match spikes,
/// while a subtitle for the wrong episode produces a flat surface where every lag scores alike.
/// </param>
public readonly record struct CorrelationPeak(
    int LagBins,
    double Peak,
    double SecondPeak,
    double Correlation,
    double PeakRatio)
{
    /// <summary>Gets the best lag expressed in seconds.</summary>
    public double LagSeconds => LagBins * ActivitySignal.BinSeconds;

    /// <summary>Gets an empty peak, used when there is nothing to correlate.</summary>
    public static CorrelationPeak None { get; } = new CorrelationPeak(0, 0, 0, 0, 0);
}
