using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Sync;
using Jellyfin.Plugin.Jimaku.Tests.Timing;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// Tests the accept/decline judgement, which is the behaviour the whole plugin exists to get right.
/// </summary>
public class SubtitleAlignerTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1440;

    private static ReferenceTrack Reference(CueTrack track) =>
        new(ActivitySignal.FromCues(track, EpisodeSeconds), "test reference", true);

    /// <summary>Renders a cue track as an ASS file, so the aligner sees a real parsed document.</summary>
    private static SubtitleDocument Document(CueTrack track, string text = "line")
    {
        var lines = new List<string>
        {
            "[Script Info]\n",
            "ScriptType: v4.00+\n",
            "\n",
            "[Events]\n",
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n",
        };

        foreach (var cue in track.Cues)
        {
            var start = SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, cue.StartSeconds);
            var end = SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, cue.EndSeconds);
            lines.Add($"Dialogue: 0,{start},{end},Default,,0,0,0,,{text}\n");
        }

        return SubtitleDocument.Parse(string.Concat(lines));
    }

    private static PluginConfiguration Config() => new();

    [Fact]
    public void Align_AlreadyInSync_IsExactAndChangesNothing()
    {
        var truth = SyntheticTrack.Episode();
        var result = new SubtitleAligner(Config())
            .Align(Reference(truth), Document(truth), allowPiecewise: false, expectDifferentCut: false);

        Assert.Equal(SyncVerdict.Exact, result.Verdict);
        Assert.True(result.Transform.IsIdentity);
    }

    [Theory]
    [InlineData(2.5)]
    [InlineData(-4.25)]
    [InlineData(11.0)]
    public void Align_ConstantOffset_IsDetectedAndTheCorrectionInverts(double offset)
    {
        var truth = SyntheticTrack.Episode();

        // The subtitle sits `offset` seconds early, so the correction must be +offset.
        var shifted = SyntheticTrack.Transform(truth, 1.0, -offset);

        var result = new SubtitleAligner(Config())
            .Align(Reference(truth), Document(shifted), allowPiecewise: false, expectDifferentCut: false);

        Assert.Equal(SyncVerdict.ConstantOffset, result.Verdict);
        Assert.Equal(offset, result.Transform.OffsetSeconds, 1);
        Assert.True(result.Transform.IsShiftOnly);
    }

    [Fact]
    public void Align_FramerateDrift_IsDetectedAsAScaleNotAShift()
    {
        var truth = SyntheticTrack.Episode();

        // A PAL-speed subtitle against NTSC-film media.
        var drifted = SyntheticTrack.Transform(truth, 23.976 / 25.0, 0);

        var result = new SubtitleAligner(Config())
            .Align(Reference(truth), Document(drifted), allowPiecewise: false, expectDifferentCut: false);

        output.WriteLine($"{result.Verdict}: {result.Transform.Describe()} r={result.Correlation:0.000}");

        Assert.Equal(SyncVerdict.FramerateDrift, result.Verdict);
        Assert.Equal(25.0 / 23.976, result.Transform.Scale, 4);
    }

    [Fact]
    public void Align_WrongEpisode_IsDeclinedWithAnExplanation()
    {
        // The single most important case: a plausible-looking but wrong subtitle must never be
        // written, and the user must be told why.
        var result = new SubtitleAligner(Config()).Align(
            Reference(SyntheticTrack.Episode(seed: 1)),
            Document(SyntheticTrack.Episode(seed: 999)),
            allowPiecewise: true,
            expectDifferentCut: false);

        output.WriteLine($"{result.Verdict}: {result.Reason}");

        Assert.Equal(SyncVerdict.Declined, result.Verdict);
        Assert.False(result.IsAcceptable);
        Assert.NotEmpty(result.Reason);
        Assert.Contains("different episode", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Align_DifferentCut_IsCorrectedPiecewiseWhenAllowed()
    {
        var truth = SyntheticTrack.Episode(seed: 42);
        var recut = SyntheticTrack.Cut(truth, cutAtSeconds: 480, firstOffset: -2.0, secondOffset: -9.0);

        var result = new SubtitleAligner(Config())
            .Align(Reference(truth), Document(recut), allowPiecewise: true, expectDifferentCut: true);

        output.WriteLine($"{result.Verdict}: {result.Reason}");

        Assert.Equal(SyncVerdict.PiecewiseCut, result.Verdict);
        Assert.Equal(2, result.Blocks.Count);
    }

    [Fact]
    public void Align_DifferentCut_IsDeclinedWhenPiecewiseIsNotAllowed()
    {
        // With splitting disabled there is no correction that fixes both halves, so the only honest
        // answer is to decline rather than shift the whole file and break one end.
        var truth = SyntheticTrack.Episode(seed: 42);
        var recut = SyntheticTrack.Cut(truth, cutAtSeconds: 480, firstOffset: -2.0, secondOffset: -9.0);

        var result = new SubtitleAligner(Config())
            .Align(Reference(truth), Document(recut), allowPiecewise: false, expectDifferentCut: false);

        Assert.NotEqual(SyncVerdict.PiecewiseCut, result.Verdict);
    }

    [Fact]
    public void Align_OffsetBeyondTheConfiguredLimit_IsDeclined()
    {
        var configuration = Config();
        configuration.MaxOffsetSeconds = 5;

        var truth = SyntheticTrack.Episode();
        var shifted = SyntheticTrack.Transform(truth, 1.0, -20.0);

        var result = new SubtitleAligner(configuration)
            .Align(Reference(truth), Document(shifted), allowPiecewise: false, expectDifferentCut: false);

        Assert.Equal(SyncVerdict.Declined, result.Verdict);
        Assert.Contains("beyond the", result.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Align_TooFewCues_IsDeclinedRatherThanGuessed()
    {
        var truth = SyntheticTrack.Episode();
        var tiny = new CueTrack(truth.Cues.Take(3).ToList());

        var result = new SubtitleAligner(Config())
            .Align(Reference(truth), Document(tiny), allowPiecewise: false, expectDifferentCut: false);

        Assert.Equal(SyncVerdict.Declined, result.Verdict);
        Assert.Contains("too few", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Align_KaraokeWithDeclinePolicy_RefusesAFramerateCorrection()
    {
        var configuration = Config();
        configuration.KaraokePolicy = KaraokeScalePolicy.Decline;

        var truth = SyntheticTrack.Episode();
        var drifted = SyntheticTrack.Transform(truth, 23.976 / 25.0, 0);

        var result = new SubtitleAligner(configuration).Align(
            Reference(truth),
            Document(drifted, "{\\k30}ka{\\k25}ze"),
            allowPiecewise: false,
            expectDifferentCut: false);

        Assert.Equal(SyncVerdict.Declined, result.Verdict);
        Assert.Contains("karaoke", result.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactRelease_SkipsAlignmentEntirely()
    {
        var result = SubtitleAligner.ExactRelease();

        Assert.Equal(SyncVerdict.Exact, result.Verdict);
        Assert.True(result.Transform.IsIdentity);
        Assert.True(result.IsAcceptable);
    }
}

/// <summary>
/// A measured offset of a couple of hundred milliseconds says more about the reference track's
/// lead-in than about the subtitle. Applying it can push a well-timed file out of sync, which is
/// exactly what happened to a user on a +0.21s "correction".
/// </summary>
public class SmallCorrectionTests
{
    private const double EpisodeSeconds = 1440;

    private static ReferenceTrack Reference(CueTrack track) =>
        new(ActivitySignal.FromCues(track, EpisodeSeconds), "test reference", true);

    private static SubtitleDocument Document(CueTrack track)
    {
        var lines = new List<string>
        {
            "[Script Info]\n", "ScriptType: v4.00+\n", "\n", "[Events]\n",
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n",
        };

        foreach (var cue in track.Cues)
        {
            lines.Add($"Dialogue: 0,{SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, cue.StartSeconds)},{SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, cue.EndSeconds)},Default,,0,0,0,,line\n");
        }

        return SubtitleDocument.Parse(string.Concat(lines));
    }

    [Theory]
    [InlineData(0.21)]
    [InlineData(-0.21)]
    [InlineData(0.30)]
    public void Align_OffsetBelowTheThreshold_IsLeftAlone(double offset)
    {
        var truth = SyntheticTrack.Episode();
        var nudged = SyntheticTrack.Transform(truth, 1.0, -offset);

        var result = new SubtitleAligner(new PluginConfiguration())
            .Align(Reference(truth), Document(nudged), false, false);

        Assert.Equal(SyncVerdict.Exact, result.Verdict);
        Assert.True(result.Transform.IsIdentity, "a sub-threshold offset must not be applied");
        Assert.Contains("below the", result.Reason, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void Align_OffsetAboveTheThreshold_IsStillCorrected(double offset)
    {
        var truth = SyntheticTrack.Episode();
        var shifted = SyntheticTrack.Transform(truth, 1.0, -offset);

        var result = new SubtitleAligner(new PluginConfiguration())
            .Align(Reference(truth), Document(shifted), false, false);

        Assert.Equal(SyncVerdict.ConstantOffset, result.Verdict);
        Assert.Equal(offset, result.Transform.OffsetSeconds, 1);
    }

    [Fact]
    public void Align_ThresholdIsConfigurable()
    {
        var configuration = new PluginConfiguration { MinCorrectionSeconds = 0.05 };
        var truth = SyntheticTrack.Episode();
        var nudged = SyntheticTrack.Transform(truth, 1.0, -0.21);

        var result = new SubtitleAligner(configuration)
            .Align(Reference(truth), Document(nudged), false, false);

        Assert.Equal(SyncVerdict.ConstantOffset, result.Verdict);
    }
}
