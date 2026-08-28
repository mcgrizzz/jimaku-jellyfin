namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// What to do when a framerate correction meets a file containing karaoke timing.
/// </summary>
public enum KaraokeScalePolicy
{
    /// <summary>Rescale karaoke and other inline tag timings along with the cue timings.</summary>
    Rescale = 0,

    /// <summary>
    /// Refuse the correction. Chooses leaving the file alone over risking a visibly broken karaoke
    /// effect, at the cost of not fixing the drift.
    /// </summary>
    Decline = 1,
}
