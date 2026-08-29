using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Timing;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// Decides whether a candidate subtitle matches the local media, and how it should be corrected.
/// </summary>
/// <remarks>
/// This class holds the accept/decline judgement. Everything else in the plugin either gathers the
/// evidence it needs or acts on what it decides. Its guiding rule is that declining is always
/// preferable to guessing: an absent subtitle is a minor annoyance, whereas one that drifts or sits
/// seconds out is worse than nothing and erodes trust in every other subtitle the plugin wrote.
/// </remarks>
public sealed class SubtitleAligner(PluginConfiguration configuration)
{
    /// <summary>
    /// Aligns a candidate subtitle against a reference derived from the local media.
    /// </summary>
    /// <param name="reference">The reference track.</param>
    /// <param name="candidate">The parsed candidate subtitle.</param>
    /// <param name="allowPiecewise">Whether differing-cut correction may be attempted.</param>
    /// <param name="expectDifferentCut">
    /// Whether the filenames suggest different releases, in which case the piecewise aligner is
    /// worth trying even if the global fit looks passable.
    /// </param>
    /// <returns>The alignment result, including a reason when it declines.</returns>
    public AlignmentResult Align(
        ReferenceTrack reference,
        SubtitleDocument candidate,
        bool allowPiecewise,
        bool expectDifferentCut)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var probe = candidate.ToCueTrack();
        if (probe.Count < 5)
        {
            return AlignmentResult.Decline(
                $"The subtitle has only {probe.Count} usable cues, too few to verify.",
                reference.Source);
        }

        var search = new LinearFitSearch(new LinearFitOptions
        {
            MaxSearchOffsetSeconds = configuration.MaxSearchOffsetSeconds,
            EnableFramerateSearch = configuration.EnableFramerateCorrection,
        });

        // Two ways of looking at the same pair, on two different scales.
        //
        // Overlap fills every bin a cue occupies; onsets place one short pulse per cue. The onset
        // signal is far sparser, so it scores lower correlation on an identical match, which is why
        // each carries its own accept floor. That also means they cannot be compared to each other
        // by raw correlation: doing so always favoured overlap, which then failed its own stricter
        // uniqueness bar and declined subtitles that onsets accepted comfortably. Compare them by
        // how far each clears its own floors instead.
        var hypotheses = new List<Hypothesis>();

        var overlapFits = search.Search(reference.Signal, probe);
        if (overlapFits.Count > 0)
        {
            hypotheses.Add(new Hypothesis(overlapFits[0], "cue overlap", configuration.MinCorrelation));
        }

        if (reference.Cues is { Count: > 0 } referenceCues)
        {
            var onsetFits = search.Search(
                ActivitySignal.FromCueStarts(referenceCues, reference.Signal.DurationSeconds),
                probe,
                onsets: true);

            if (onsetFits.Count > 0)
            {
                hypotheses.Add(new Hypothesis(onsetFits[0], "cue starts", configuration.MinOnsetCorrelation));
            }
        }

        if (hypotheses.Count == 0)
        {
            return AlignmentResult.Decline("The alignment search produced no result.", reference.Source);
        }

        var chosen = hypotheses
            .OrderByDescending(h => h.Passes(configuration.MinPeakRatio))
            .ThenByDescending(h => h.Margin(configuration.MinPeakRatio))
            .First();

        var best = chosen.Fit;
        var minCorrelation = chosen.MinCorrelation;
        var representation = chosen.Representation;

        var result = new AlignmentResult
        {
            Transform = best.Transform,
            Correlation = best.Correlation,
            PeakRatio = best.PeakRatio,
            ReferenceSource = $"{reference.Source}, matched on {representation}",
        };

        // How aligned it is and how much of the dialogue it covers are separate questions, and a
        // subtitle that omits lines can align perfectly. Measure both.
        if (reference.Cues is { Count: > 0 } coverageReference)
        {
            var coverage = CueCoverage.Measure(
                coverageReference,
                probe,
                best.Transform,
                reference.Signal.DurationSeconds);

            result.Coverage = coverage.ReferenceCovered;
            result.OnScreenRatio = coverage.OnScreenRatio;
        }

        var globalIsGood = best.Correlation >= minCorrelation
                           && best.PeakRatio >= configuration.MinPeakRatio;

        // A differing cut shows up as a global fit that is weak but not absent: parts of the
        // subtitle line up, so try the piecewise aligner before giving up on the file.
        var shouldTrySplit = allowPiecewise
                             && (!globalIsGood || expectDifferentCut)
                             && best.Correlation > minCorrelation / 2;

        if (shouldTrySplit)
        {
            var split = new SplitAligner().Align(reference.Signal, probe, best.OffsetSeconds);
            if (IsCredibleSplit(split, best.Correlation, minCorrelation))
            {
                result.Verdict = SyncVerdict.PiecewiseCut;
                result.Blocks = split.Blocks;
                result.Correlation = split.Correlation;
                result.Reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Matched a different cut: {split.Blocks.Count} sections, offsets {string.Join(", ", split.Blocks.Select(b => b.OffsetSeconds.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)))}s.");
                return result;
            }
        }

        if (!globalIsGood)
        {
            result.Verdict = SyncVerdict.Declined;
            result.Reason = DescribeWeakMatch(best, minCorrelation);
            return result;
        }

        if (Math.Abs(best.Scale - 1.0) > 1e-9)
        {
            if (Math.Abs(best.Scale - 1.0) > configuration.MaxScaleDeviation)
            {
                result.Verdict = SyncVerdict.Declined;
                result.Reason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Implied time scale {best.Scale:0.####} is further from 1 than the configured limit of {configuration.MaxScaleDeviation:0.##}.");
                return result;
            }

            if (candidate.HasKaraoke && configuration.KaraokePolicy == KaraokeScalePolicy.Decline)
            {
                result.Verdict = SyncVerdict.Declined;
                result.Reason = "The file needs a framerate correction but contains karaoke timing, and the karaoke policy is set to decline.";
                return result;
            }

            result.Verdict = SyncVerdict.FramerateDrift;
            result.Reason = string.Create(
                CultureInfo.InvariantCulture,
                $"Framerate drift: time scaled by {best.Scale:0.######}, offset {best.OffsetSeconds:+0.000;-0.000}s.");
            return result;
        }

        if (Math.Abs(best.OffsetSeconds) > configuration.MaxOffsetSeconds)
        {
            result.Verdict = SyncVerdict.Declined;
            result.Reason = string.Create(
                CultureInfo.InvariantCulture,
                $"Needs a {best.OffsetSeconds:+0.0;-0.0}s shift, beyond the {configuration.MaxOffsetSeconds:0.#}s limit. A file this far out is more likely the wrong one than a badly timed right one.");
            return result;
        }

        // Corrections smaller than the reference's own timing uncertainty are not worth making:
        // applying one can easily push a well-timed subtitle out of sync rather than into it.
        if (Math.Abs(best.OffsetSeconds) < configuration.MinCorrectionSeconds)
        {
            result.Verdict = SyncVerdict.Exact;
            result.Transform = TimingTransform.Identity;
            result.Reason = string.Create(
                CultureInfo.InvariantCulture,
                $"Already in sync; the measured difference of {best.OffsetSeconds:+0.000;-0.000}s is below the {configuration.MinCorrectionSeconds:0.00}s worth correcting.");
            return result;
        }

        result.Verdict = SyncVerdict.ConstantOffset;
        result.Reason = string.Create(
            CultureInfo.InvariantCulture,
            $"Constant offset of {best.OffsetSeconds:+0.000;-0.000}s.");
        return result;
    }

    /// <summary>
    /// One way of comparing a candidate with the reference, carrying the accept floor that applies
    /// to its own scale.
    /// </summary>
    /// <param name="Fit">The best fit this comparison found.</param>
    /// <param name="Representation">How the signals were built, for display.</param>
    /// <param name="MinCorrelation">The correlation floor for this representation.</param>
    private readonly record struct Hypothesis(LinearFit Fit, string Representation, double MinCorrelation)
    {
        /// <summary>Whether this comparison clears its own thresholds.</summary>
        public bool Passes(double minPeakRatio) =>
            Fit.Correlation >= MinCorrelation && Fit.PeakRatio >= minPeakRatio;

        /// <summary>
        /// How far the weaker of the two measures clears its floor. Expressing both as a multiple
        /// of their own thresholds is what makes comparisons across the two scales meaningful.
        /// </summary>
        public double Margin(double minPeakRatio)
        {
            var correlation = MinCorrelation > 0 ? Fit.Correlation / MinCorrelation : Fit.Correlation;
            var uniqueness = minPeakRatio > 0 ? Fit.PeakRatio / minPeakRatio : Fit.PeakRatio;
            return Math.Min(correlation, uniqueness);
        }
    }

    /// <summary>
    /// Produces a result for a candidate whose filename carries the same CRC32 as the video, where
    /// the subtitle provably belongs to these exact bytes.
    /// </summary>
    /// <returns>An exact-match result.</returns>
    public static AlignmentResult ExactRelease() => new()
    {
        Verdict = SyncVerdict.Exact,
        Transform = TimingTransform.Identity,
        Correlation = 1.0,
        PeakRatio = 999,
        ReferenceSource = "release checksum",
        Reason = "The subtitle filename carries the same CRC32 as the video, so it was released against this exact file.",
    };

    private bool IsCredibleSplit(SplitResult split, double globalCorrelation, double minCorrelation)
    {
        if (split.Blocks.Count is 0 or 1)
        {
            return false;
        }

        if (split.Blocks.Count > configuration.MaxSplitBlocks)
        {
            return false;
        }

        // Shattering into many thin blocks is what fitting noise looks like; a real cut produces a
        // small number of substantial sections.
        if (split.Blocks.Any(b => b.CueCount < configuration.MinCuesPerSplitBlock))
        {
            return false;
        }

        if (split.Correlation < minCorrelation)
        {
            return false;
        }

        // Piecewise correction has more freedom than a global fit, so it must earn its use by
        // beating it clearly rather than marginally.
        return split.Correlation > globalCorrelation + 0.05;
    }

    private string DescribeWeakMatch(LinearFit best, double minCorrelation)
    {
        if (best.Correlation < minCorrelation && best.PeakRatio < configuration.MinPeakRatio)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"No convincing alignment: correlation {best.Correlation:0.00} (need {minCorrelation:0.00}) and the best offset was no better than the alternatives (uniqueness {best.PeakRatio:0.00}, need {configuration.MinPeakRatio:0.00}). This is most likely a subtitle for a different episode.");
        }

        if (best.Correlation < minCorrelation)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Timings only correlate at {best.Correlation:0.00}, below the {minCorrelation:0.00} required to correct safely.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"The best offset is not clearly better than the alternatives (uniqueness {best.PeakRatio:0.00}, need {configuration.MinPeakRatio:0.00}), so the correction cannot be trusted.");
    }
}
