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
/// <param name="IsFromSubtitles">Whether it came from an embedded subtitle track.</param>
/// <param name="Cues">
/// The underlying cues, when the reference came from a subtitle track. Needed to compare cue starts
/// alone, which voice-activity references cannot offer since they have no cue structure.
/// </param>
public readonly record struct ReferenceTrack(
    ActivitySignal Signal,
    string Source,
    bool IsFromSubtitles,
    CueTrack? Cues = null);
