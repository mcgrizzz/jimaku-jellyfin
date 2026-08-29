using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// How completely one cue track covers the moments marked by another.
/// </summary>
/// <param name="ReferenceCovered">
/// Fraction of reference cues with a candidate cue starting nearby. Low means the subtitle omits
/// dialogue the media clearly contains.
/// </param>
/// <param name="CandidateMatched">
/// Fraction of candidate cues that correspond to a reference cue. Low means the subtitle marks
/// moments the reference does not, which is normal for a track carrying signs or songs.
/// </param>
/// <param name="OnScreenRatio">Share of the runtime the candidate has something on screen.</param>
public readonly record struct CoverageResult(
    double ReferenceCovered,
    double CandidateMatched,
    double OnScreenRatio);

/// <summary>
/// Measures how much of the dialogue a subtitle actually covers.
/// </summary>
/// <remarks>
/// Correlation answers "is this subtitle aligned", which is not the same question as "is this
/// subtitle any good". A file that omits a fifth of the lines and holds the rest on screen briefly
/// can align perfectly and still read badly: lines vanish before they have been spoken, and stretches
/// of dialogue go untranslated. Worse, a normalized correlation actively penalises the more complete
/// file, because its extra cues have nothing to match in a sparser reference. Coverage is the
/// missing half of the judgement.
/// </remarks>
public static class CueCoverage
{
    /// <summary>How far apart two cue starts may be and still be considered the same moment.</summary>
    public const double DefaultToleranceSeconds = 0.5;

    /// <summary>
    /// Compares a candidate against a reference, after applying a piecewise correction.
    /// </summary>
    /// <remarks>
    /// Needed because coverage was being measured against the global fit even for candidates the
    /// aligner had decided to correct in sections. For a differently-cut subtitle the global fit is
    /// wrong for most of the file by construction, so its coverage read far lower than the truth -
    /// and coverage leads the ranking. The effect was to hand the decision to whichever file could
    /// be explained by a single offset, however badly, over one that genuinely matched in two.
    /// </remarks>
    /// <param name="reference">The reference cues.</param>
    /// <param name="candidate">The candidate cues.</param>
    /// <param name="blocks">The per-section corrections that would be applied.</param>
    /// <param name="totalSeconds">Runtime, for the on-screen ratio.</param>
    /// <param name="toleranceSeconds">Matching tolerance.</param>
    /// <returns>The coverage measures.</returns>
    public static CoverageResult Measure(
        CueTrack reference,
        CueTrack candidate,
        IReadOnlyList<SplitBlock> blocks,
        double totalSeconds,
        double toleranceSeconds = DefaultToleranceSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(blocks);

        if (blocks.Count == 0)
        {
            return Measure(reference, candidate, TimingTransform.Identity, totalSeconds, toleranceSeconds);
        }

        var shifted = new List<Cue>(candidate.Count);
        var blockIndex = 0;

        for (var i = 0; i < candidate.Count; i++)
        {
            while (blockIndex < blocks.Count - 1 && i > blocks[blockIndex].LastCueIndex)
            {
                blockIndex++;
            }

            var offset = blocks[blockIndex].OffsetSeconds;
            var cue = candidate.Cues[i];
            shifted.Add(new Cue(cue.StartSeconds + offset, cue.EndSeconds + offset));
        }

        return Measure(reference, new CueTrack(shifted), TimingTransform.Identity, totalSeconds, toleranceSeconds);
    }

    /// <summary>
    /// Compares a candidate against a reference, after applying a timing correction.
    /// </summary>
    /// <param name="reference">The reference cues.</param>
    /// <param name="candidate">The candidate cues.</param>
    /// <param name="transform">The correction that would be applied to the candidate.</param>
    /// <param name="totalSeconds">Runtime, for the on-screen ratio.</param>
    /// <param name="toleranceSeconds">Matching tolerance.</param>
    /// <returns>The coverage measures.</returns>
    public static CoverageResult Measure(
        CueTrack reference,
        CueTrack candidate,
        TimingTransform transform,
        double totalSeconds,
        double toleranceSeconds = DefaultToleranceSeconds)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);

        if (reference.Count == 0 || candidate.Count == 0)
        {
            return new CoverageResult(0, 0, 0);
        }

        var shifted = candidate.Cues.Select(c => transform.Apply(c.StartSeconds)).OrderBy(s => s).ToArray();
        var referenceStarts = reference.Cues.Select(c => c.StartSeconds).OrderBy(s => s).ToArray();

        var covered = CountMatched(referenceStarts, shifted, toleranceSeconds);
        var matched = CountMatched(shifted, referenceStarts, toleranceSeconds);

        var onScreen = 0.0;
        foreach (var cue in candidate.Cues)
        {
            onScreen += Math.Max(0, transform.Apply(cue.EndSeconds) - transform.Apply(cue.StartSeconds));
        }

        return new CoverageResult(
            (double)covered / referenceStarts.Length,
            (double)matched / shifted.Length,
            totalSeconds > 0 ? Math.Min(onScreen / totalSeconds, 1.0) : 0);
    }

    /// <summary>
    /// Counts how many values in <paramref name="needles"/> have a neighbour in
    /// <paramref name="haystack"/> within the tolerance. Both are sorted, so a merge walk suffices.
    /// </summary>
    private static int CountMatched(double[] needles, double[] haystack, double tolerance)
    {
        var matched = 0;
        var j = 0;

        foreach (var needle in needles)
        {
            while (j < haystack.Length - 1 && haystack[j] < needle - tolerance)
            {
                j++;
            }

            for (var k = j; k < haystack.Length && haystack[k] <= needle + tolerance; k++)
            {
                if (Math.Abs(haystack[k] - needle) <= tolerance)
                {
                    matched++;
                    break;
                }
            }
        }

        return matched;
    }
}
