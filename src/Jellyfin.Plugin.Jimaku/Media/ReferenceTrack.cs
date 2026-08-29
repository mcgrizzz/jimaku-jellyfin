using Jellyfin.Plugin.Jimaku.Timing;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// The activity pattern of the local media, which a candidate subtitle is aligned against.
/// </summary>
/// <param name="Signal">Binned activity across the episode.</param>
/// <param name="Source">
/// How it was obtained, for display and logging: an embedded subtitle track is far more
/// trustworthy than voice activity analysis, and the user should be able to see which was used.
/// </param>
/// <param name="Cues">
/// The underlying cues, when the reference came from a subtitle track. Needed to compare cue starts
/// alone, and to check a piecewise correction actually landed on the dialogue - neither of which a
/// voice-activity reference can offer, having no cue structure at all.
/// </param>
public readonly record struct ReferenceTrack(
    ActivitySignal Signal,
    string Source,
    CueTrack? Cues = null)
{
    /// <summary>
    /// Gets a value indicating whether the reference came from cue timings rather than audio.
    /// </summary>
    /// <remarks>
    /// Derived rather than declared. As a separate flag it could disagree with the cues, and a test
    /// fixture that claimed subtitle provenance while supplying none silently exercised a state the
    /// plugin cannot actually produce.
    /// </remarks>
    public bool IsFromSubtitles => Cues is { Count: > 0 };
}
