using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// One contiguous run of cues sharing a single offset, produced by the split aligner.
/// </summary>
/// <param name="FirstCueIndex">Index of the first cue in the run.</param>
/// <param name="LastCueIndex">Index of the last cue in the run, inclusive.</param>
/// <param name="OffsetSeconds">Offset applied to every cue in the run.</param>
public readonly record struct SplitBlock(int FirstCueIndex, int LastCueIndex, double OffsetSeconds)
{
    /// <summary>Gets the number of cues in the run.</summary>
    public int CueCount => LastCueIndex - FirstCueIndex + 1;
}

/// <summary>
/// The complete outcome of aligning one candidate subtitle against the local media.
/// </summary>
public sealed class AlignmentResult
{
    /// <summary>Gets or sets the verdict.</summary>
    public SyncVerdict Verdict { get; set; } = SyncVerdict.Unknown;

    /// <summary>Gets or sets the global linear correction to apply.</summary>
    public TimingTransform Transform { get; set; } = TimingTransform.Identity;

    /// <summary>Gets or sets the baseline-corrected correlation at the chosen alignment.</summary>
    public double Correlation { get; set; }

    /// <summary>Gets or sets the peak-to-second-peak ratio at the chosen alignment.</summary>
    public double PeakRatio { get; set; }

    /// <summary>
    /// Gets or sets the per-block offsets when <see cref="Verdict"/> is
    /// <see cref="SyncVerdict.PiecewiseCut"/>. Empty otherwise.
    /// </summary>
    public IReadOnlyList<SplitBlock> Blocks { get; set; } = Array.Empty<SplitBlock>();

    /// <summary>Gets or sets how the reference timings were obtained, for display and logging.</summary>
    public string ReferenceSource { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a plain-language explanation. Always populated when the verdict is
    /// <see cref="SyncVerdict.Declined"/>, so the user is told why nothing was written.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether a subtitle may be written based on this result.</summary>
    public bool IsAcceptable =>
        Verdict is SyncVerdict.Exact
                or SyncVerdict.ConstantOffset
                or SyncVerdict.FramerateDrift
                or SyncVerdict.PiecewiseCut;

    /// <summary>Creates a declined result carrying an explanation.</summary>
    /// <param name="reason">Why the candidate was rejected.</param>
    /// <param name="referenceSource">How reference timings were obtained, if at all.</param>
    /// <returns>A declined result.</returns>
    public static AlignmentResult Decline(string reason, string referenceSource = "") => new()
    {
        Verdict = SyncVerdict.Declined,
        Reason = reason,
        ReferenceSource = referenceSource,
    };
}
