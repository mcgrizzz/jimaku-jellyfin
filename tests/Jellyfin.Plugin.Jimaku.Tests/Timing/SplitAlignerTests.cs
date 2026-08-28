using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

public class SplitAlignerTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1440;

    [Fact]
    public void Align_DifferentCut_RecoversBothOffsetsAndTheBoundary()
    {
        // A subtitle timed for a broadcast cut, applied to a disc release where ~7 s of footage
        // was added around the eight-minute mark. No single global offset can fix this.
        var truth = SyntheticTrack.Episode(seed: 42);
        var reference = ActivitySignal.FromCues(truth, EpisodeSeconds);
        var probe = SyntheticTrack.Cut(truth, cutAtSeconds: 480, firstOffset: -2.0, secondOffset: -9.0);

        var result = new SplitAligner().Align(reference, probe, centreOffsetSeconds: 2.0);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"blocks={result.Blocks.Count} r={result.Correlation:0.000} offsets=[{string.Join(", ", result.Blocks.Select(b => b.OffsetSeconds.ToString("0.00", CultureInfo.InvariantCulture)))}]"));

        Assert.Equal(2, result.Blocks.Count);
        Assert.InRange(result.Blocks[0].OffsetSeconds, 1.99, 2.01);
        Assert.InRange(result.Blocks[1].OffsetSeconds, 8.99, 9.01);
        Assert.True(result.Correlation > 0.95, $"correlation was {result.Correlation}");

        // The boundary must fall on the first cue that starts after the inserted footage.
        var firstLateCue = probe.Cues
            .Select((c, i) => (c, i))
            .First(x => x.c.StartSeconds > 480 - 9.0 && x.c.StartSeconds + 9.0 >= 480).i;
        Assert.InRange(result.Blocks[1].FirstCueIndex, firstLateCue - 1, firstLateCue + 1);
    }

    [Fact]
    public void Align_SingleGlobalOffset_DoesNotSplitSpuriously()
    {
        // The split penalty exists to stop the DP fitting noise. A subtitle that only needs one
        // shift must come back as exactly one block.
        var truth = SyntheticTrack.Episode(seed: 7);
        var reference = ActivitySignal.FromCues(truth, EpisodeSeconds);
        var probe = SyntheticTrack.Transform(truth, 1.0, -4.25);

        var result = new SplitAligner().Align(reference, probe, centreOffsetSeconds: 4.25);

        Assert.Single(result.Blocks);
        Assert.InRange(result.Blocks[0].OffsetSeconds, 4.24, 4.26);
    }

    [Fact]
    public void Align_WrongEpisode_ScoresPoorlyEvenWithSplittingAllowed()
    {
        // Piecewise alignment is the most permissive tool available, so it is also the easiest way
        // to accidentally "fix" a subtitle that does not belong to this episode. It must not.
        var reference = ActivitySignal.FromCues(SyntheticTrack.Episode(seed: 3), EpisodeSeconds);
        var probe = SyntheticTrack.Episode(seed: 808);

        var result = new SplitAligner().Align(reference, probe, centreOffsetSeconds: 0);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"wrong-episode piecewise: blocks={result.Blocks.Count} r={result.Correlation:0.000}"));

        Assert.True(result.Correlation < 0.75, $"correlation was {result.Correlation}");
    }
}
