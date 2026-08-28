using System;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// Scores how well two binary activity signals agree, corrected for how much of each is active.
/// </summary>
/// <remarks>
/// <para>
/// The obvious measure, normalized cross-correlation, is unusable here. For binary signals it
/// reduces to <c>overlap / sqrt(activeA * activeB)</c>, and two *unrelated* tracks each active a
/// fraction p of the time overlap by chance about <c>p</c> of the time. So NCC has a floor equal to
/// the duty cycle: dialogue-dense subtitle tracks score around 0.6 against a completely different
/// episode. Any fixed accept threshold built on it would wave through the wrong episode.
/// </para>
/// <para>
/// Subtracting the overlap expected under independence fixes this. What remains is the Pearson
/// correlation of the two indicator sequences (the phi coefficient): 1 for an exact match, 0 for
/// chance, negative for anti-correlation, and comparable across files of any density.
/// </para>
/// </remarks>
public static class CorrelationScore
{
    /// <summary>
    /// Computes the baseline-corrected coefficient.
    /// </summary>
    /// <param name="overlap">Count of bins active in both signals at the chosen alignment.</param>
    /// <param name="activeA">Count of active bins in the first signal.</param>
    /// <param name="activeB">Count of active bins in the second signal.</param>
    /// <param name="length">Total bins in the window the two are compared over.</param>
    /// <returns>The coefficient, or zero when it is undefined.</returns>
    public static double Compute(double overlap, double activeA, double activeB, double length)
    {
        if (length <= 0 || activeA <= 0 || activeB <= 0 || activeA >= length || activeB >= length)
        {
            return 0;
        }

        var expected = activeA * activeB / length;
        var variance = activeA * (1 - (activeA / length)) * activeB * (1 - (activeB / length));
        if (variance <= 0)
        {
            return 0;
        }

        return (overlap - expected) / Math.Sqrt(variance);
    }
}
