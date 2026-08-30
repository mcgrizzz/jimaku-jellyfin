using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

/// <summary>
/// Recovering a rate difference the fixed framerate grid cannot express.
/// </summary>
/// <remarks>
/// Built from a real report: a subtitle needing +1.85s at the start of an episode, about +5.4s in
/// the middle and +6.7s near the end. That is a drift of roughly a third of a percent, and the grid
/// tries 0.1% and then jumps to 4.17% - so no entry in it comes close. The search answered with
/// scale 1 and a compromise offset, correct in the middle and seconds out at both ends, which is
/// exactly what was observed.
/// </remarks>
public class DriftRefinerTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1420;

    /// <summary>
    /// Builds a reference and a subtitle that drifts against it, from two measured points.
    /// </summary>
    private static (ActivitySignal Signal, CueTrack Reference, CueTrack Candidate) Drifting(
        double offsetAtStart,
        double offsetAtEnd)
    {
        var random = new Random(20260829);
        var reference = new List<Cue>();
        var candidate = new List<Cue>();

        var scale = 1.0 + ((offsetAtEnd - offsetAtStart) / EpisodeSeconds);

        var t = 20.0;
        while (t < EpisodeSeconds - 30)
        {
            t += 2.0 + (random.NextDouble() * 8.0);
            var duration = 1.0 + (random.NextDouble() * 2.0);

            // The subtitle sits where it sits; the media plays it later and increasingly so.
            candidate.Add(new Cue(t, t + duration));

            var media = (t * scale) + offsetAtStart;
            reference.Add(new Cue(media, media + duration));
        }

        var track = new CueTrack(reference);
        return (ActivitySignal.FromCues(track, EpisodeSeconds), track, new CueTrack(candidate));
    }

    [Fact]
    public void TheGridAloneCannotExpressAThirdOfAPercent()
    {
        var (signal, reference, candidate) = Drifting(1.85, 6.7);

        var coarse = new LinearFitSearch(new LinearFitOptions()).Search(signal, candidate)[0];
        var coverage = CueCoverage.Measure(reference, candidate, coarse.Transform, EpisodeSeconds);

        output.WriteLine(
            $"grid best: x{coarse.Scale:0.######} {coarse.OffsetSeconds:+0.000} r={coarse.Correlation:0.00}, covers {coverage.ReferenceCovered:P0}");

        // It reaches for its nearest entry - 0.1%, against a true drift of a third of a percent -
        // and lands nowhere useful. What matters is not which ratio it picks but that none of them
        // is close: most of the episode is still seconds out.
        var trueScale = 1.0 + ((6.7 - 1.85) / EpisodeSeconds);
        Assert.True(
            Math.Abs(coarse.Scale - trueScale) > 0.002,
            $"the grid unexpectedly landed on {coarse.Scale:0.######}");

        Assert.True(coverage.ReferenceCovered < 0.75, $"covered {coverage.ReferenceCovered:P0}");
    }

    [Fact]
    public void MeasuringTheTwoEndsRecoversTheDrift()
    {
        var (signal, _, candidate) = Drifting(1.85, 6.7);

        var search = new LinearFitSearch(new LinearFitOptions());
        var coarse = search.Search(signal, candidate)[0];

        var refined = DriftRefiner.Refine(signal, candidate, coarse, onsets: false);

        Assert.NotNull(refined);
        output.WriteLine($"refined: x{refined.Value.Scale:0.######} {refined.Value.OffsetSeconds:+0.000} r={refined.Value.Correlation:0.00}");

        var expected = 1.0 + ((6.7 - 1.85) / EpisodeSeconds);
        Assert.Equal(expected, refined.Value.Scale, 4);
        Assert.True(refined.Value.Correlation > coarse.Correlation);
    }

    [Fact]
    public void TheRecoveredTransformLandsCuesWhereTheyBelong()
    {
        // The measure that actually matters: after correction, is a line on screen when it is said?
        var (signal, reference, candidate) = Drifting(1.85, 6.7);

        var search = new LinearFitSearch(new LinearFitOptions());
        var coarse = search.Search(signal, candidate)[0];
        var refined = DriftRefiner.Refine(signal, candidate, coarse, onsets: false);

        Assert.NotNull(refined);

        var before = CueCoverage.Measure(reference, candidate, coarse.Transform, EpisodeSeconds);
        var after = CueCoverage.Measure(reference, candidate, refined.Value.Transform, EpisodeSeconds);

        output.WriteLine($"coverage {before.ReferenceCovered:P0} -> {after.ReferenceCovered:P0}");

        Assert.True(after.ReferenceCovered > 0.95);
        Assert.True(after.ReferenceCovered > before.ReferenceCovered + 0.2);
    }

    [Fact]
    public void AnAlreadyAlignedSubtitleIsLeftAlone()
    {
        // The refinement has two free parameters against a signal that is already explained, so it
        // must not invent a slope out of measurement noise.
        var (signal, _, candidate) = Drifting(2.0, 2.0);

        var search = new LinearFitSearch(new LinearFitOptions());
        var coarse = search.Search(signal, candidate)[0];

        Assert.Null(DriftRefiner.Refine(signal, candidate, coarse, onsets: false));
    }

    [Fact]
    public void AnAbsurdRateIsRejectedRatherThanFitted()
    {
        // Half a minute of divergence across an episode is not a drifting subtitle, it is the
        // wrong one - and fitting a line through it would produce a confident, wrong answer.
        var (signal, _, candidate) = Drifting(0, 90);

        var search = new LinearFitSearch(new LinearFitOptions());
        var coarse = search.Search(signal, candidate)[0];
        var refined = DriftRefiner.Refine(signal, candidate, coarse, onsets: false);

        Assert.True(refined is null || Math.Abs(refined.Value.Scale - 1.0) <= 0.05);
    }

    [Fact]
    public void DriftIsRecoveredWhenMatchingOnCueStartsToo()
    {
        // The path that reached the user, and the one that was never exercised. Cue starts build a
        // far sparser signal than cue spans, and the refinement has to be checked by the same
        // machinery that produced the fit it is trying to beat - otherwise the two correlations are
        // not comparable and it quietly declines to fire.
        var (signal, reference, candidate) = Drifting(1.85, 6.7);

        var onsetReference = ActivitySignal.FromCueStarts(reference, EpisodeSeconds);
        var search = new LinearFitSearch(new LinearFitOptions());
        var coarse = search.Search(onsetReference, candidate, null, onsets: true)[0];

        var refined = DriftRefiner.Refine(onsetReference, candidate, coarse, onsets: true);

        Assert.NotNull(refined);
        output.WriteLine(
            $"onsets: x{coarse.Scale:0.######} r={coarse.Correlation:0.00} -> x{refined.Value.Scale:0.######} r={refined.Value.Correlation:0.00}");

        // Cue starts give a far sparser signal than cue spans, so the recovered rate is coarser.
        // Asserting it to six figures would be pinning noise; what has to hold is that the drift it
        // removes is the drift that was there, to within a fraction of a second across the episode.
        var recoveredDrift = (refined.Value.Scale - 1.0) * EpisodeSeconds;
        Assert.InRange(recoveredDrift, 6.7 - 1.85 - 1.0, 6.7 - 1.85 + 1.0);

        var after = CueCoverage.Measure(reference, candidate, refined.Value.Transform, EpisodeSeconds);
        Assert.True(after.ReferenceCovered > 0.95, $"covered {after.ReferenceCovered:P0}");
    }

    [Fact]
    public void TheWholeAlignerRecoversDriftEndToEnd()
    {
        // Through Align rather than the refiner directly, so the wiring is covered as well as the
        // arithmetic: the aligner has to hand the refinement the same signal the winning
        // hypothesis was measured against.
        var (_, reference, candidate) = Drifting(1.85, 6.7);

        var document = SubtitleDocument.Parse(System.Text.Encoding.UTF8.GetBytes(
            string.Join(Environment.NewLine, candidate.Cues.Select((c, i) => string.Join(
                Environment.NewLine,
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Stamp(c.StartSeconds) + " --> " + Stamp(c.EndSeconds),
                "line " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Empty)))));

        var track = new Jellyfin.Plugin.Jimaku.Media.ReferenceTrack(
            ActivitySignal.FromCues(reference, EpisodeSeconds), "test", reference);

        var result = new Jellyfin.Plugin.Jimaku.Sync.SubtitleAligner(
            new Jellyfin.Plugin.Jimaku.Configuration.PluginConfiguration())
            .Align(track, document, allowPiecewise: false, expectDifferentCut: false);

        output.WriteLine($"{result.Verdict}: {result.Reason}");

        Assert.Equal(SyncVerdict.FramerateDrift, result.Verdict);
        Assert.True(result.Coverage > 0.95, $"covered {result.Coverage:P0}");
    }

    private static string Stamp(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\,fff");


    [Theory]
    [InlineData(1.0, 3.5)]
    [InlineData(-2.0, 2.0)]
    [InlineData(0.5, 5.0)]
    public void DriftIsRecoveredAcrossARangeOfShapes(double start, double end)
    {
        var (signal, reference, candidate) = Drifting(start, end);

        var search = new LinearFitSearch(new LinearFitOptions());
        var coarse = search.Search(signal, candidate)[0];
        var refined = DriftRefiner.Refine(signal, candidate, coarse, onsets: false);

        Assert.NotNull(refined);

        var after = CueCoverage.Measure(reference, candidate, refined.Value.Transform, EpisodeSeconds);
        Assert.True(after.ReferenceCovered > 0.9, $"coverage was {after.ReferenceCovered:P0}");
    }
}
