using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// The outcome of testing whether a group of measurements share a common offset.
/// </summary>
/// <param name="Detected">Whether a consensus offset was found.</param>
/// <param name="OffsetSeconds">The consensus offset, in seconds.</param>
/// <param name="Agreeing">How many measurements agreed.</param>
/// <param name="Total">How many measurements were considered.</param>
public readonly record struct ReferenceBiasResult(bool Detected, double OffsetSeconds, int Agreeing, int Total);

/// <summary>
/// Detects a timing bias belonging to the reference rather than to the subtitles measured
/// against it.
/// </summary>
/// <remarks>
/// <para>
/// When several independently produced subtitles all need the same correction, the thing they have
/// in common is not each other - it is the reference. An embedded track is authored with its own
/// lead-in, and a subtitle already well matched to the audio will be shifted out of sync by
/// "correcting" it towards that lead-in.
/// </para>
/// <para>
/// Observed on a real episode: six subtitles from six unrelated groups all measured between +0.21
/// and +0.23 seconds against the same embedded track. Applying that shift made the result visibly
/// late. Subtracting the consensus leaves each subtitle's genuine, individual error intact while
/// discarding the part that belongs to the reference.
/// </para>
/// </remarks>
public static class ReferenceBias
{
    /// <summary>How far from the consensus a measurement may sit and still count as agreeing.</summary>
    public const double AgreementToleranceSeconds = 0.15;

    /// <summary>Fewer measurements than this cannot establish a consensus.</summary>
    public const int MinimumMeasurements = 3;

    /// <summary>Fraction of measurements that must agree before the offset is treated as bias.</summary>
    public const double RequiredAgreement = 0.75;

    /// <summary>
    /// Looks for an offset shared by most of the supplied measurements.
    /// </summary>
    /// <param name="offsetsSeconds">The measured offsets.</param>
    /// <returns>The consensus, or a result with <c>Detected</c> false.</returns>
    public static ReferenceBiasResult Detect(IReadOnlyList<double> offsetsSeconds)
    {
        ArgumentNullException.ThrowIfNull(offsetsSeconds);

        if (offsetsSeconds.Count < MinimumMeasurements)
        {
            return new ReferenceBiasResult(false, 0, 0, offsetsSeconds.Count);
        }

        // The median resists the outlier from a subtitle that is simply for a different cut, which
        // a mean would let drag the estimate anywhere.
        var sorted = offsetsSeconds.OrderBy(static o => o).ToArray();
        var median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;

        var agreeing = offsetsSeconds.Count(o => Math.Abs(o - median) <= AgreementToleranceSeconds);

        if (agreeing < MinimumMeasurements ||
            (double)agreeing / offsetsSeconds.Count < RequiredAgreement)
        {
            return new ReferenceBiasResult(false, 0, agreeing, offsetsSeconds.Count);
        }

        // Average only the agreeing measurements, so the estimate is not pulled by the outliers
        // that were excluded from the vote.
        var consensus = offsetsSeconds
            .Where(o => Math.Abs(o - median) <= AgreementToleranceSeconds)
            .Average();

        return new ReferenceBiasResult(true, consensus, agreeing, offsetsSeconds.Count);
    }
}
