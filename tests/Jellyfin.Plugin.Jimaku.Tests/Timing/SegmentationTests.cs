using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

/// <summary>
/// Two subtitles can mark the same moments while disagreeing about where one line ends and the
/// next begins. Overlap scoring punishes that; comparing cue starts does not.
/// </summary>
public class SegmentationTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1440;

    /// <summary>
    /// Re-splits a track the way another group might: some lines merged into one longer cue, some
    /// held on screen longer than the original. Start times are preserved.
    /// </summary>
    private static CueTrack Resegment(CueTrack track, int seed = 17)
    {
        var random = new Random(seed);
        var cues = new List<Cue>();
        var i = 0;

        while (i < track.Count)
        {
            var cue = track.Cues[i];

            // Occasionally swallow the following cue, as a group that keeps an exchange on screen
            // as a single line would.
            if (i + 1 < track.Count && random.NextDouble() < 0.35)
            {
                cues.Add(new Cue(cue.StartSeconds, track.Cues[i + 1].EndSeconds));
                i += 2;
                continue;
            }

            // Otherwise vary how long it lingers.
            var duration = cue.DurationSeconds * (0.6 + (random.NextDouble() * 1.1));
            cues.Add(new Cue(cue.StartSeconds, cue.StartSeconds + duration));
            i++;
        }

        return new CueTrack(cues);
    }

    [Fact]
    public void ResegmentedButCorrectlyTimed_ScoresPoorlyOnOverlapAndWellOnOnsets()
    {
        var truth = SyntheticTrack.Episode(seed: 21);
        var resegmented = SyntheticTrack.Transform(Resegment(truth), 1.0, -3.0);

        var search = new LinearFitSearch();

        var overlap = search.Search(ActivitySignal.FromCues(truth, EpisodeSeconds), resegmented)[0];
        var onset = search.Search(
            ActivitySignal.FromCueStarts(truth, EpisodeSeconds),
            resegmented,
            scales: null,
            onsets: true)[0];

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"overlap: r={overlap.Correlation:0.000} uniqueness={overlap.PeakRatio:0.00} offset={overlap.OffsetSeconds:0.000}"));
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"onsets : r={onset.Correlation:0.000} uniqueness={onset.PeakRatio:0.00} offset={onset.OffsetSeconds:0.000}"));

        // Both find the right offset; the point is how confident each is about it.
        Assert.Equal(3.0, overlap.OffsetSeconds, 1);
        Assert.Equal(3.0, onset.OffsetSeconds, 1);

        // Comparing starts alone should judge this correctly timed subtitle far more favourably.
        Assert.True(
            onset.Correlation > overlap.Correlation,
            $"onset r={onset.Correlation:0.000} did not beat overlap r={overlap.Correlation:0.000}");

        Assert.True(onset.Correlation > 0.8, $"onset correlation was only {onset.Correlation:0.000}");
    }

    [Fact]
    public void OnsetMatching_StillRejectsTheWrongEpisode()
    {
        // Loosening the comparison must not make it credulous.
        var reference = ActivitySignal.FromCueStarts(SyntheticTrack.Episode(seed: 4), EpisodeSeconds);
        var wrong = SyntheticTrack.Episode(seed: 900);

        var fit = new LinearFitSearch().Search(reference, wrong, scales: null, onsets: true)[0];

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"wrong episode on onsets: r={fit.Correlation:0.000} uniqueness={fit.PeakRatio:0.00}"));

        Assert.True(fit.Correlation < 0.45, $"wrong episode scored r={fit.Correlation:0.000}");
    }

    [Fact]
    public void OnsetMatching_RecoversAKnownOffsetExactly()
    {
        var truth = SyntheticTrack.Episode(seed: 8);
        var shifted = SyntheticTrack.Transform(truth, 1.0, -7.5);

        var fit = new LinearFitSearch().Search(
            ActivitySignal.FromCueStarts(truth, EpisodeSeconds),
            shifted,
            scales: null,
            onsets: true)[0];

        Assert.Equal(7.5, fit.OffsetSeconds, 2);
        Assert.True(fit.Correlation > 0.95);
    }
}
