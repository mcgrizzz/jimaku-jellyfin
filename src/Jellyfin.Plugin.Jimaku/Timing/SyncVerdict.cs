namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// What the aligner concluded about a candidate subtitle's relationship to the local media.
/// </summary>
public enum SyncVerdict
{
    /// <summary>Nothing was measured, or measurement failed outright.</summary>
    Unknown = 0,

    /// <summary>Already in sync. Written through unmodified.</summary>
    Exact = 1,

    /// <summary>Whole file is shifted by a constant amount. Safe to correct.</summary>
    ConstantOffset = 2,

    /// <summary>Timings drift linearly, consistent with a framerate conversion.</summary>
    FramerateDrift = 3,

    /// <summary>
    /// The subtitle matches a different cut of the episode: several regions each need their own
    /// offset, as happens between a TV broadcast and a Blu-ray release.
    /// </summary>
    PiecewiseCut = 4,

    /// <summary>Confidence was too low to correct safely. Nothing is written.</summary>
    Declined = 5,
}
