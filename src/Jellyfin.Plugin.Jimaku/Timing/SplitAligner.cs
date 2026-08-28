using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// Options for the piecewise (differing-cut) aligner.
/// </summary>
public sealed class SplitAlignerOptions
{
    /// <summary>
    /// Gets or sets the cost, in overlap-seconds, of letting one cue take a different offset from
    /// its predecessor. Low values shatter the subtitle into noise-fitting fragments; high values
    /// miss real cuts. alass defaults to 7 and documents the optimum as 7 plus or minus 1.
    /// </summary>
    public double SplitPenaltySeconds { get; set; } = 6.0;

    /// <summary>
    /// Gets or sets the weight of the guard-band penalty, which discourages parking a cue so that
    /// speech spills out either side of it. Matches ffsubsync's split length penalty.
    /// </summary>
    public double LengthPenalty { get; set; } = 0.25;

    /// <summary>Gets or sets the half-width of the offset search around the global fit, in seconds.</summary>
    public double SearchRadiusSeconds { get; set; } = 30;

    /// <summary>Gets or sets the guard band width considered on each side of a cue, in bins.</summary>
    public int MaxGuardBins { get; set; } = 200;
}

/// <summary>
/// The outcome of a piecewise alignment.
/// </summary>
/// <param name="Blocks">Contiguous runs of cues, each with its own offset.</param>
/// <param name="Score">Total DP objective value achieved.</param>
/// <param name="Correlation">Baseline-corrected correlation after applying the per-block offsets.</param>
public readonly record struct SplitResult(IReadOnlyList<SplitBlock> Blocks, double Score, double Correlation);

/// <summary>
/// Assigns each cue its own offset via dynamic programming, penalizing changes of offset.
/// </summary>
/// <remarks>
/// This is the case a single global offset provably cannot fix: a TV broadcast and a Blu-ray release
/// of the same episode differ by inserted or removed footage, so the subtitle is correct in pieces
/// but the pieces need different shifts. The DP is ffsubsync's <c>split_aligner</c> approach, which
/// is in turn alass's idea: maximize total overlap minus a fixed penalty per offset change, and let
/// the optimum decide where the cuts are.
/// </remarks>
public sealed class SplitAligner
{
    private readonly SplitAlignerOptions _options;

    /// <summary>Initializes a new instance of the <see cref="SplitAligner"/> class.</summary>
    /// <param name="options">Aligner options, or null for defaults.</param>
    public SplitAligner(SplitAlignerOptions? options = null)
    {
        _options = options ?? new SplitAlignerOptions();
    }

    /// <summary>
    /// Aligns each cue independently, subject to a per-split penalty.
    /// </summary>
    /// <param name="reference">Binned reference activity.</param>
    /// <param name="probe">Candidate subtitle cue timings.</param>
    /// <param name="centreOffsetSeconds">
    /// Offset to centre the search on, normally the best global fit. Searching the full range around
    /// zero would be both slower and more prone to fitting noise.
    /// </param>
    /// <returns>The blocks found, or an empty result when there is nothing to align.</returns>
    public SplitResult Align(ActivitySignal reference, CueTrack probe, double centreOffsetSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(probe);

        var cueCount = probe.Count;
        if (cueCount == 0 || reference.Energy <= 0)
        {
            return new SplitResult(Array.Empty<SplitBlock>(), 0, 0);
        }

        var centreBin = (int)Math.Round(centreOffsetSeconds * ActivitySignal.BinsPerSecond);
        var radiusBins = (int)Math.Round(_options.SearchRadiusSeconds * ActivitySignal.BinsPerSecond);
        var minOffset = centreBin - radiusBins;
        var offsetCount = (2 * radiusBins) + 1;
        var splitPenalty = _options.SplitPenaltySeconds * ActivitySignal.BinsPerSecond;

        // Precompute each cue's bin span once; the DP touches them offsetCount times.
        var starts = new int[cueCount];
        var ends = new int[cueCount];
        var guards = new int[cueCount];
        for (var i = 0; i < cueCount; i++)
        {
            var cue = probe.Cues[i];
            starts[i] = (int)Math.Round(cue.StartSeconds * ActivitySignal.BinsPerSecond);
            ends[i] = Math.Max(starts[i] + 1, (int)Math.Round(cue.EndSeconds * ActivitySignal.BinsPerSecond));
            guards[i] = Math.Min(ends[i] - starts[i], _options.MaxGuardBins);
        }

        var previous = new double[offsetCount];
        var current = new double[offsetCount];

        // jumped[i][o] records whether cue i changed offset rather than inheriting cue i-1's.
        var jumped = new bool[cueCount][];
        var jumpTargets = new int[cueCount];

        for (var o = 0; o < offsetCount; o++)
        {
            previous[o] = Rate(reference, starts[0], ends[0], guards[0], minOffset + o);
        }

        jumped[0] = new bool[offsetCount];

        for (var i = 1; i < cueCount; i++)
        {
            // The best place to jump from is the same for every destination offset, so it is found
            // once per cue rather than once per (cue, offset) pair. This is what keeps the DP at
            // O(cues * offsets) instead of O(cues * offsets^2).
            var bestPrev = double.NegativeInfinity;
            var bestPrevIndex = 0;
            for (var o = 0; o < offsetCount; o++)
            {
                if (previous[o] > bestPrev)
                {
                    bestPrev = previous[o];
                    bestPrevIndex = o;
                }
            }

            jumpTargets[i] = bestPrevIndex;
            var jumpValue = bestPrev - splitPenalty;
            var flags = new bool[offsetCount];

            for (var o = 0; o < offsetCount; o++)
            {
                var rating = Rate(reference, starts[i], ends[i], guards[i], minOffset + o);
                var keep = previous[o];
                if (jumpValue > keep)
                {
                    current[o] = rating + jumpValue;
                    flags[o] = true;
                }
                else
                {
                    current[o] = rating + keep;
                }
            }

            jumped[i] = flags;
            (previous, current) = (current, previous);
        }

        var finalBest = double.NegativeInfinity;
        var finalIndex = 0;
        for (var o = 0; o < offsetCount; o++)
        {
            if (previous[o] > finalBest)
            {
                finalBest = previous[o];
                finalIndex = o;
            }
        }

        // Walk backwards recovering each cue's offset.
        var offsets = new int[cueCount];
        var cursor = finalIndex;
        for (var i = cueCount - 1; i >= 0; i--)
        {
            offsets[i] = minOffset + cursor;
            if (i > 0)
            {
                cursor = jumped[i][cursor] ? jumpTargets[i] : cursor;
            }
        }

        var blocks = new List<SplitBlock>();
        var blockStart = 0;
        for (var i = 1; i <= cueCount; i++)
        {
            if (i == cueCount || offsets[i] != offsets[blockStart])
            {
                blocks.Add(new SplitBlock(
                    blockStart,
                    i - 1,
                    offsets[blockStart] * ActivitySignal.BinSeconds));
                blockStart = i;
            }
        }

        var correlation = ScorePiecewise(reference, probe, offsets);
        return new SplitResult(blocks, finalBest, correlation);
    }

    /// <summary>
    /// Rates a cue placed at a given offset: reference activity under the cue, minus activity
    /// immediately outside it. The guard bands are what stop a cue from parking on the edge of a
    /// long speech run and collecting overlap it has not earned.
    /// </summary>
    private double Rate(ActivitySignal reference, int start, int end, int guard, int offset)
    {
        var inside = reference.SumRange(start + offset, end + offset);
        if (guard <= 0 || _options.LengthPenalty <= 0)
        {
            return inside;
        }

        var before = reference.SumRange(start + offset - guard, start + offset);
        var after = reference.SumRange(end + offset, end + offset + guard);
        return inside - (_options.LengthPenalty * (before + after));
    }

    /// <summary>
    /// Recomputes the correlation once per-cue offsets are applied, so a piecewise fit can be
    /// compared on equal terms against the global one it is meant to beat.
    /// </summary>
    private static double ScorePiecewise(ActivitySignal reference, CueTrack probe, int[] offsets)
    {
        var shifted = new List<Cue>(probe.Count);
        for (var i = 0; i < probe.Count; i++)
        {
            var shift = offsets[i] * ActivitySignal.BinSeconds;
            var cue = probe.Cues[i];
            shifted.Add(new Cue(cue.StartSeconds + shift, cue.EndSeconds + shift));
        }

        var signal = ActivitySignal.FromCues(new CueTrack(shifted), reference.DurationSeconds);
        if (signal.Energy <= 0 || reference.Energy <= 0)
        {
            return 0;
        }

        var overlap = 0.0;
        var limit = Math.Min(signal.Length, reference.Length);
        for (var i = 0; i < limit; i++)
        {
            overlap += signal.Bins[i] * reference.Bins[i];
        }

        return CorrelationScore.Compute(overlap, reference.Energy, signal.Energy, reference.Length);
    }
}
