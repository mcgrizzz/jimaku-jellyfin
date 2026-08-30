using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// Recovers a rate difference the fixed framerate grid cannot represent.
/// </summary>
/// <remarks>
/// <para>
/// The grid tries the ratios that arise from standards conversion - NTSC pulldown at 0.1%, the PAL
/// speedup at 4.17%, PAL against NTSC-film at 4.27% - and nothing in between. That is the right set
/// for a subtitle retimed between broadcast standards, and useless for a drift of a few tenths of a
/// percent, which the grid cannot express at all. Faced with one, the search returns the best it
/// can: scale 1 and a compromise offset that is wrong at both ends of the episode and least wrong
/// in the middle.
/// </para>
/// <para>
/// The drift is directly measurable instead. Align the front of the subtitle and the back of it
/// separately; if the two need different corrections, the difference divided by the time between
/// them is the rate error. Two points determine the line, and no grid is involved.
/// </para>
/// </remarks>
public static class DriftRefiner
{
    /// <summary>Fraction of the cues taken from each end to measure against.</summary>
    private const double SegmentFraction = 0.35;

    /// <summary>How far apart the two ends must disagree before this is worth acting on.</summary>
    private const double MinimumDisagreementSeconds = 0.25;

    /// <summary>Widest rate error to believe. Beyond this it is a wrong file, not a drifting one.</summary>
    private const double MaxScaleDeviation = 0.05;

    /// <summary>How far around the coarse offset each end is searched.</summary>
    private const double LocalSearchSeconds = 30;

    /// <summary>Fewest cues an end segment needs for its offset to mean anything.</summary>
    private const int MinimumSegmentCues = 8;

    /// <summary>
    /// How many times to measure and correct before settling.
    /// </summary>
    /// <remarks>
    /// One pass leaves a residue. Each end's offset is found by correlating a third of the cues,
    /// which locates a peak to within a bin or two - and a slope drawn through two points that are
    /// each slightly wrong is itself slightly wrong, by enough at the far end of an episode to push
    /// cues outside the half-second that counts as a match. Correcting again against what remains
    /// converges quickly, because each pass starts from a much smaller error than the last.
    /// </remarks>
    private const int MaxPasses = 4;

    /// <summary>
    /// Measures the rate difference between a subtitle and a reference, and returns the improved fit.
    /// </summary>
    /// <param name="reference">The reference signal.</param>
    /// <param name="probe">The candidate's cues.</param>
    /// <param name="coarse">The best fit the grid search found.</param>
    /// <param name="onsets">Whether the reference marks cue starts rather than cue spans.</param>
    /// <param name="searchSeconds">How far around the coarse offset each end may move.</param>
    /// <returns>A better fit, or null when there is no drift worth correcting.</returns>
    public static LinearFit? Refine(
        ActivitySignal reference,
        CueTrack probe,
        LinearFit coarse,
        bool onsets,
        double searchSeconds = LocalSearchSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(probe);

        LinearFit? best = null;
        var current = coarse;

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var next = RefineOnce(reference, probe, current, onsets, searchSeconds);
            if (next is not { } improved || improved.Correlation <= current.Correlation)
            {
                break;
            }

            best = improved;
            current = improved;
        }

        return best;
    }

    private static LinearFit? RefineOnce(
        ActivitySignal reference,
        CueTrack probe,
        LinearFit coarse,
        bool onsets,
        double searchSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(probe);

        var segmentSize = (int)(probe.Count * SegmentFraction);
        if (segmentSize < MinimumSegmentCues)
        {
            return null;
        }

        // Measured after the coarse transform, so each end reports what is left over rather than
        // its absolute offset. A correct coarse fit leaves both near zero.
        var corrected = new CueTrack(probe.Cues
            .Select(c => new Cue(coarse.Transform.Apply(c.StartSeconds), coarse.Transform.Apply(c.EndSeconds)))
            .ToList());

        var search = new LinearFitSearch(new LinearFitOptions
        {
            MaxSearchOffsetSeconds = searchSeconds,
            EnableFramerateSearch = false,
        });

        var front = Measure(search, reference, corrected, 0, segmentSize, onsets);
        var back = Measure(search, reference, corrected, corrected.Count - segmentSize, segmentSize, onsets);

        if (front is null || back is null)
        {
            return null;
        }

        var (frontResidual, frontTime) = front.Value;
        var (backResidual, backTime) = back.Value;

        var span = backTime - frontTime;
        if (span < 60)
        {
            return null;
        }

        var disagreement = backResidual - frontResidual;
        if (Math.Abs(disagreement) < MinimumDisagreementSeconds)
        {
            return null;
        }

        // t' = scale * t + offset, fitted through the two measured points.
        var extraScale = 1.0 + (disagreement / span);
        if (Math.Abs(extraScale - 1.0) > MaxScaleDeviation)
        {
            return null;
        }

        var extraOffset = frontResidual - ((extraScale - 1.0) * frontTime);

        // Composed with the coarse transform rather than replacing it.
        var scale = coarse.Scale * extraScale;
        var offset = (coarse.OffsetSeconds * extraScale) + extraOffset;
        var refined = new TimingTransform(scale, offset);

        // Verified by re-running the same search on the corrected cues, rather than by scoring the
        // transform by hand. The search clips long cues, pads to its own length and normalises
        // against it; a score computed differently is not comparable to the one it produced, and
        // comparing them anyway is how this refinement silently declined to fire on a real drift.
        var check = search.Search(
            reference,
            new CueTrack(probe.Cues.Select(c => new Cue(refined.Apply(c.StartSeconds), refined.Apply(c.EndSeconds))).ToList()),
            [1.0],
            onsets);

        if (check.Count == 0 || check[0].Correlation <= coarse.Correlation)
        {
            return null;
        }

        // Uniqueness is taken from the verification, not carried over from the coarse fit. A
        // drifting subtitle has no single offset that works, so its uncorrected peak is necessarily
        // indistinct - and inheriting that number meant a drift could be measured correctly and
        // then declined for the vagueness the correction had just removed. The verification
        // measures how unique the alignment is once the drift is gone, which is the question that
        // was being asked.
        return new LinearFit(scale, offset, check[0].Correlation, check[0].PeakRatio);
    }

    /// <summary>
    /// Finds the residual offset of one end of a subtitle, and the reference time it applies at.
    /// </summary>
    private static (double Residual, double Time)? Measure(
        LinearFitSearch search,
        ActivitySignal reference,
        CueTrack corrected,
        int start,
        int count,
        bool onsets)
    {
        var slice = corrected.Cues.Skip(start).Take(count).ToList();
        if (slice.Count < MinimumSegmentCues)
        {
            return null;
        }

        // The same cross-correlation the whole-file search uses, over a third of the cues. Scanning
        // offsets one bin at a time would be exact and hopelessly slow: six thousand positions,
        // each scoring a signal of a hundred and forty thousand bins.
        var fits = search.Search(reference, new CueTrack(slice), null, onsets);
        if (fits.Count == 0)
        {
            return null;
        }

        return (fits[0].OffsetSeconds, slice.Average(c => c.StartSeconds));
    }

}

