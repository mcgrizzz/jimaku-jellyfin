using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// A single subtitle cue reduced to its timing. Text is deliberately absent: alignment only ever
/// looks at when something is on screen, never at what it says.
/// </summary>
/// <param name="StartSeconds">Start time, in seconds from the beginning of the media.</param>
/// <param name="EndSeconds">End time, in seconds from the beginning of the media.</param>
public readonly record struct Cue(double StartSeconds, double EndSeconds)
{
    /// <summary>Gets the cue duration in seconds, never negative.</summary>
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);
}

/// <summary>
/// An ordered set of cue timings, used as either the reference (what the video actually does) or
/// the probe (what a downloaded subtitle claims) in an alignment.
/// </summary>
public sealed class CueTrack
{
    /// <summary>Initializes a new instance of the <see cref="CueTrack"/> class.</summary>
    /// <param name="cues">The cues. Sorted by start time on construction.</param>
    public CueTrack(IEnumerable<Cue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        Cues = cues.Where(c => c.EndSeconds > c.StartSeconds)
                   .OrderBy(c => c.StartSeconds)
                   .ToArray();
    }

    /// <summary>Gets the cues, ordered by start time.</summary>
    public IReadOnlyList<Cue> Cues { get; }

    /// <summary>Gets the number of cues.</summary>
    public int Count => Cues.Count;

    /// <summary>Gets the end time of the last cue, or zero for an empty track.</summary>
    public double LastEndSeconds => Cues.Count == 0 ? 0 : Cues[^1].EndSeconds;

    /// <summary>Gets the start time of the first cue, or zero for an empty track.</summary>
    public double FirstStartSeconds => Cues.Count == 0 ? 0 : Cues[0].StartSeconds;

    /// <summary>Gets an empty track.</summary>
    public static CueTrack Empty { get; } = new CueTrack(Array.Empty<Cue>());
}
