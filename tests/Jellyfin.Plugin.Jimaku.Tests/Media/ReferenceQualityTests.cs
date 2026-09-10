using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Media;

/// <summary>
/// What makes a subtitle track useless as a timing reference even though it parses fine.
/// </summary>
/// <remarks>
/// From a BILIBILI multi-subtitle release whose six embedded tracks included one of 10,135 cues -
/// 420 a minute - and one on screen every second of the episode, every event written twice. Both
/// parse, both look like subtitles, and neither says when anyone is speaking: correlating against
/// them gave seven candidates scores between 0.13 and 0.25 with uniqueness 1.00 across the board,
/// which is the arithmetic of a reference that overlaps everything equally.
/// </remarks>
public class ReferenceQualityTests
{
    private const double EpisodeSeconds = 1420;

    /// <summary>A track written twice over, as multi-language scripts routinely are.</summary>
    private static CueTrack Duplicated(int count)
    {
        var random = new Random(4242);
        var cues = new List<Cue>();
        var t = 20.0;

        for (var i = 0; i < count; i++)
        {
            t += 2.0 + (random.NextDouble() * 6.0);
            var cue = new Cue(t, t + 1.5);
            cues.Add(cue);
            cues.Add(cue);
        }

        return new CueTrack(cues);
    }

    [Fact]
    public void DuplicatedEventsAreOneMomentNotTwo()
    {
        var track = Duplicated(200);

        var distinct = track.Cues
            .Select(c => ((long)Math.Round(c.StartSeconds * 100), (long)Math.Round(c.EndSeconds * 100)))
            .Distinct()
            .Count();

        // Coverage is counted per reference cue, so a script written twice halves every figure it
        // reports about how much of the dialogue a candidate actually covers.
        Assert.Equal(400, track.Count);
        Assert.Equal(200, distinct);
    }

    [Fact]
    public void ATrackTilingTheWholeEpisodeCarriesNoTiming()
    {
        // Back-to-back cues with no gaps: on screen 100% of the time. The signal is then a constant,
        // and a constant correlates with every possible alignment equally.
        var cues = new List<Cue>();
        for (var t = 60.0; t < EpisodeSeconds - 60; t += 8.7)
        {
            cues.Add(new Cue(t, t + 8.7));
        }

        var track = new CueTrack(cues);
        var signal = ActivitySignal.FromCues(track, EpisodeSeconds);
        var duty = signal.Energy / signal.Length;

        Assert.True(duty > 0.85, $"duty was {duty:P0}");
    }

    [Fact]
    public void OrdinaryDialogueSitsWellBelowTheLimits()
    {
        // The other side of the bar: a normal track has to keep passing.
        var random = new Random(7);
        var cues = new List<Cue>();
        var t = 20.0;

        while (t < EpisodeSeconds - 60)
        {
            t += 2.0 + (random.NextDouble() * 8.0);
            cues.Add(new Cue(t, t + 1.0 + (random.NextDouble() * 2.0)));
        }

        var track = new CueTrack(cues);
        var signal = ActivitySignal.FromCues(track, EpisodeSeconds);

        var perMinute = track.Count / (EpisodeSeconds / 60.0);
        var duty = signal.Energy / signal.Length;

        Assert.InRange(perMinute, 5, 90);
        Assert.InRange(duty, 0.05, 0.85);
    }
}
