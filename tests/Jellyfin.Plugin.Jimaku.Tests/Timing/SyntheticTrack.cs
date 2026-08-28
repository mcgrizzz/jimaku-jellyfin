using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jimaku.Timing;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

/// <summary>
/// Builds deterministic cue tracks that stand in for a real episode's dialogue pattern.
/// </summary>
internal static class SyntheticTrack
{
    /// <summary>
    /// Produces cues with irregular gaps and durations. Irregularity matters: a perfectly periodic
    /// track correlates equally well at many lags, which is exactly the degenerate case the
    /// peak-ratio confidence measure is meant to catch.
    /// </summary>
    public static CueTrack Episode(int seed = 1234, int cueCount = 280, double startAt = 12.0)
    {
        var random = new Random(seed);
        var cues = new List<Cue>(cueCount);
        var t = startAt;
        for (var i = 0; i < cueCount; i++)
        {
            var duration = 0.8 + (random.NextDouble() * 3.2);
            var gap = 0.3 + (random.NextDouble() * 3.0);
            cues.Add(new Cue(t, t + duration));
            t += duration + gap;
        }

        return new CueTrack(cues);
    }

    /// <summary>Applies a linear time map to every cue.</summary>
    public static CueTrack Transform(CueTrack track, double scale, double offset)
    {
        var cues = new List<Cue>(track.Count);
        foreach (var cue in track.Cues)
        {
            cues.Add(new Cue((cue.StartSeconds * scale) + offset, (cue.EndSeconds * scale) + offset));
        }

        return new CueTrack(cues);
    }

    /// <summary>
    /// Applies one offset to cues before <paramref name="cutAtSeconds"/> and another after, standing
    /// in for a broadcast-versus-disc cut where footage was inserted partway through.
    /// </summary>
    public static CueTrack Cut(CueTrack track, double cutAtSeconds, double firstOffset, double secondOffset)
    {
        var cues = new List<Cue>(track.Count);
        foreach (var cue in track.Cues)
        {
            var shift = cue.StartSeconds < cutAtSeconds ? firstOffset : secondOffset;
            cues.Add(new Cue(cue.StartSeconds + shift, cue.EndSeconds + shift));
        }

        return new CueTrack(cues);
    }
}
