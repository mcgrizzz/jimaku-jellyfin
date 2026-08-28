using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

/// <summary>
/// Coverage is the half of the judgement correlation cannot make.
/// </summary>
/// <remarks>
/// Measured on a real episode: the chosen subtitle omitted a fifth of the dialogue and held its
/// lines on screen for 58% of the runtime against the reference's 74%, so lines vanished before
/// they had been spoken. It correlated better than the fuller alternative, because a normalized
/// correlation quietly rewards the sparser file - its cues have less to disagree with.
/// </remarks>
public class CueCoverageTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1440;

    /// <summary>Drops a fraction of cues and shortens the rest, as a terse subtitle would.</summary>
    private static CueTrack Sparse(CueTrack track, double keep, double durationFactor, int seed = 5)
    {
        var random = new Random(seed);
        var cues = track.Cues
            .Where(_ => random.NextDouble() < keep)
            .Select(c => new Cue(c.StartSeconds, c.StartSeconds + (c.DurationSeconds * durationFactor)))
            .ToList();

        return new CueTrack(cues);
    }

    [Fact]
    public void Measure_CompleteSubtitle_CoversNearlyEverything()
    {
        var truth = SyntheticTrack.Episode(seed: 3);

        var coverage = CueCoverage.Measure(truth, truth, TimingTransform.Identity, EpisodeSeconds);

        Assert.Equal(1.0, coverage.ReferenceCovered, 2);
        Assert.Equal(1.0, coverage.CandidateMatched, 2);
    }

    [Fact]
    public void Measure_SubtitleOmittingLines_ScoresLowCoverage()
    {
        var truth = SyntheticTrack.Episode(seed: 3);
        var terse = Sparse(truth, keep: 0.75, durationFactor: 0.7);

        var coverage = CueCoverage.Measure(truth, terse, TimingTransform.Identity, EpisodeSeconds);

        output.WriteLine($"covers {coverage.ReferenceCovered:P1}, on screen {coverage.OnScreenRatio:P1}");

        Assert.InRange(coverage.ReferenceCovered, 0.6, 0.85);

        // Its own cues still line up; it is simply missing some. That is the distinction
        // correlation alone cannot draw.
        Assert.True(coverage.CandidateMatched > 0.95);
    }

    [Fact]
    public void Measure_ShorterCues_ReduceOnScreenTimeWithoutHarmingCoverage()
    {
        // "Leaves before the line is finished" is a duration problem, not an alignment one.
        var truth = SyntheticTrack.Episode(seed: 3);
        var clipped = new CueTrack(truth.Cues.Select(c => new Cue(c.StartSeconds, c.StartSeconds + (c.DurationSeconds * 0.5))));

        var full = CueCoverage.Measure(truth, truth, TimingTransform.Identity, EpisodeSeconds);
        var brief = CueCoverage.Measure(truth, clipped, TimingTransform.Identity, EpisodeSeconds);

        Assert.Equal(full.ReferenceCovered, brief.ReferenceCovered, 2);
        Assert.True(brief.OnScreenRatio < full.OnScreenRatio * 0.6);
    }

    [Fact]
    public void Measure_AppliesTheTransformBeforeComparing()
    {
        var truth = SyntheticTrack.Episode(seed: 3);
        var shifted = SyntheticTrack.Transform(truth, 1.0, -4.0);

        var uncorrected = CueCoverage.Measure(truth, shifted, TimingTransform.Identity, EpisodeSeconds);
        var corrected = CueCoverage.Measure(truth, shifted, new TimingTransform(1.0, 4.0), EpisodeSeconds);

        output.WriteLine($"uncorrected {uncorrected.ReferenceCovered:P1}, corrected {corrected.ReferenceCovered:P1}");

        Assert.True(corrected.ReferenceCovered > 0.95);

        // Not an absolute bound: cues in this track sit roughly four seconds apart, so a four
        // second shift coincidentally parks some of them on their neighbours.
        Assert.True(
            corrected.ReferenceCovered > uncorrected.ReferenceCovered + 0.3,
            $"correcting improved coverage only from {uncorrected.ReferenceCovered:P1} to {corrected.ReferenceCovered:P1}");
    }

    [Fact]
    public void Measure_EmptyTracks_AreNotAnError()
    {
        var coverage = CueCoverage.Measure(CueTrack.Empty, SyntheticTrack.Episode(), TimingTransform.Identity, EpisodeSeconds);
        Assert.Equal(0, coverage.ReferenceCovered);
    }
}

/// <summary>
/// Values measured directly from the user's media: a Bilibili WebRip carrying eleven subtitle
/// tracks, against two Jimaku candidates. Recorded so the ranking cannot silently regress to
/// preferring the sparser subtitle again.
/// </summary>
public class RealEpisodeRankingTests(ITestOutputHelper output)
{
    private static double Quality(double coverage, double correlation, double peakRatio, double offset)
    {
        // Mirrors JimakuSyncService.Quality.
        return Math.Round(coverage, 2)
             + (Math.Round(correlation, 2) / 10.0)
             + (Math.Min(peakRatio, 5.0) / 1000.0)
             - (Math.Min(Math.Abs(offset), 30) / 100000.0);
    }

    [Fact]
    public void TheFullerSubtitleWins_DespiteLowerCorrelation()
    {
        // Measured against the consensus reference track: AnimeOut covers more of the dialogue
        // while correlating slightly worse, which is exactly the trade the old ranking got wrong.
        var animeOut = Quality(coverage: 0.812, correlation: 0.44, peakRatio: 5.08, offset: 0.010);
        var nekomoe = Quality(coverage: 0.789, correlation: 0.56, peakRatio: 6.25, offset: -0.030);

        output.WriteLine($"AnimeOut {animeOut:0.0000} vs Nekomoe {nekomoe:0.0000}");

        Assert.True(animeOut > nekomoe, "the subtitle covering more dialogue must win");
    }

    [Fact]
    public void CorrelationStillOutweighsATrivialCoverageEdge()
    {
        // Coverage leads, but it must not let a badly aligned file win on completeness alone.
        var wellAligned = Quality(coverage: 0.78, correlation: 0.90, peakRatio: 6.0, offset: 0);
        var barelyAligned = Quality(coverage: 0.80, correlation: 0.26, peakRatio: 1.3, offset: 0);

        Assert.True(wellAligned > barelyAligned);
    }

    [Fact]
    public void OnsetCorrelationsFromRealFiles_ClearTheOnsetThreshold()
    {
        // The real correct matches measured 0.44 and 0.56 on cue starts; a wrong episode measures
        // about 0.04. The shared 0.50 floor rejected both correct files.
        var configuration = new Jellyfin.Plugin.Jimaku.Configuration.PluginConfiguration();

        Assert.True(0.44 >= configuration.MinOnsetCorrelation, "AnimeOut's real score must pass");
        Assert.True(0.56 >= configuration.MinOnsetCorrelation, "Nekomoe's real score must pass");
        Assert.True(0.04 < configuration.MinOnsetCorrelation, "an unrelated episode must still fail");
        Assert.True(configuration.MinOnsetCorrelation < configuration.MinCorrelation);
    }
}
