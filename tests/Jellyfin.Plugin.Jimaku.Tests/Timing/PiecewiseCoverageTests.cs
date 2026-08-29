using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

/// <summary>
/// Coverage measured against a piecewise correction rather than the global one.
/// </summary>
/// <remarks>
/// Coverage leads the ranking between candidates, and it was being measured with the global fit
/// even for candidates the aligner had decided to correct in sections. For a differently-cut
/// subtitle the global fit is wrong for most of the file by construction, so its coverage read far
/// below the truth - handing the decision to whichever file could be explained by a single offset,
/// however badly, over one that genuinely matched in two.
/// </remarks>
public class PiecewiseCoverageTests
{
    /// <summary>A reference with a 9.5s insert halfway through, as a disc cut produces.</summary>
    private static (CueTrack Reference, CueTrack Candidate, List<SplitBlock> Blocks) DifferingCut()
    {
        var random = new Random(20260829);
        var reference = new List<Cue>();
        var candidate = new List<Cue>();

        var t = 10.0;
        for (var i = 0; i < 40; i++)
        {
            t += 2.0 + (random.NextDouble() * 8.0);
            var duration = 1.0 + (random.NextDouble() * 2.0);

            candidate.Add(new Cue(t, t + duration));

            var mediaStart = t + (i >= 20 ? 9.5 : 0.0);
            reference.Add(new Cue(mediaStart, mediaStart + duration));
        }

        var blocks = new List<SplitBlock>
        {
            new(0, 19, 0.0),
            new(20, 39, 9.5),
        };

        return (new CueTrack(reference), new CueTrack(candidate), blocks);
    }

    [Fact]
    public void TheGlobalFitUnderstatesADifferentlyCutSubtitle()
    {
        var (reference, candidate, _) = DifferingCut();

        // Whatever single offset is chosen, half the file is ~9.5s out.
        var global = CueCoverage.Measure(reference, candidate, TimingTransform.Identity, 600);

        Assert.InRange(global.ReferenceCovered, 0.0, 0.6);
    }

    [Fact]
    public void MeasuringAgainstTheBlocksRecoversTheTruth()
    {
        var (reference, candidate, blocks) = DifferingCut();

        var piecewise = CueCoverage.Measure(reference, candidate, blocks, 600);

        Assert.InRange(piecewise.ReferenceCovered, 0.95, 1.0);
    }

    [Fact]
    public void ThePiecewiseFigureBeatsTheGlobalOneOnTheSameFile()
    {
        // The comparison that decides which candidate wins, so it is the one worth pinning.
        var (reference, candidate, blocks) = DifferingCut();

        var global = CueCoverage.Measure(reference, candidate, TimingTransform.Identity, 600);
        var piecewise = CueCoverage.Measure(reference, candidate, blocks, 600);

        Assert.True(piecewise.ReferenceCovered > global.ReferenceCovered + 0.3);
    }

    [Fact]
    public void NoBlocksFallsBackToAnUncorrectedComparison()
    {
        var (reference, candidate, _) = DifferingCut();

        var empty = CueCoverage.Measure(reference, candidate, new List<SplitBlock>(), 600);
        var identity = CueCoverage.Measure(reference, candidate, TimingTransform.Identity, 600);

        Assert.Equal(identity.ReferenceCovered, empty.ReferenceCovered, 6);
    }
}
