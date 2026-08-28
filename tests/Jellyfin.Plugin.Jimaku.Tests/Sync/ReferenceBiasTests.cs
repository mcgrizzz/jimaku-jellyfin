using System.Collections.Generic;
using Jellyfin.Plugin.Jimaku.Sync;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// Several independently produced subtitles needing the identical correction is evidence about the
/// reference track, not about the subtitles.
/// </summary>
public class ReferenceBiasTests(ITestOutputHelper output)
{
    [Fact]
    public void Detect_TheRealCase_FindsTheSharedOffset()
    {
        // Measured on one episode: six subtitles from six unrelated groups, plus one that is
        // genuinely for a different cut. Applying the shared 0.22s made playback visibly late.
        List<double> offsets = [0.21, 0.23, 0.21, -5.84, 0.23, 0.23, 0.21];

        var bias = ReferenceBias.Detect(offsets);

        output.WriteLine($"detected={bias.Detected} offset={bias.OffsetSeconds:0.000} agreeing={bias.Agreeing}/{bias.Total}");

        Assert.True(bias.Detected);
        Assert.Equal(0.22, bias.OffsetSeconds, 2);

        // The outlier must not be counted as agreeing, nor drag the estimate.
        Assert.Equal(6, bias.Agreeing);
    }

    [Fact]
    public void Detect_Outlier_DoesNotDragTheEstimate()
    {
        // A mean would land near -0.6; the median-based vote must ignore the outlier entirely.
        List<double> offsets = [0.20, 0.20, 0.20, 0.20, -5.0];

        var bias = ReferenceBias.Detect(offsets);

        Assert.True(bias.Detected);
        Assert.Equal(0.20, bias.OffsetSeconds, 3);
    }

    [Fact]
    public void Detect_CandidatesDisagree_FindsNothing()
    {
        // Genuinely different timings mean there is no shared bias to remove, and each subtitle's
        // measured offset is its own.
        List<double> offsets = [0.20, 2.50, -1.80, 5.00];

        Assert.False(ReferenceBias.Detect(offsets).Detected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Detect_TooFewMeasurements_FindsNothing(int count)
    {
        // Two agreeing files could easily be two rips of the same source.
        var offsets = new List<double>();
        for (var i = 0; i < count; i++)
        {
            offsets.Add(0.21);
        }

        Assert.False(ReferenceBias.Detect(offsets).Detected);
    }

    [Fact]
    public void Detect_BareMajority_IsNotEnough()
    {
        // Three of six agreeing is a coincidence, not a consensus.
        List<double> offsets = [0.21, 0.21, 0.21, 3.0, -2.0, 7.5];

        Assert.False(ReferenceBias.Detect(offsets).Detected);
    }

    [Fact]
    public void Detect_AllInSync_ReportsNoMeaningfulBias()
    {
        List<double> offsets = [0.0, 0.01, -0.01, 0.0];

        var bias = ReferenceBias.Detect(offsets);

        Assert.True(bias.Detected);
        Assert.Equal(0.0, bias.OffsetSeconds, 2);
    }

    [Fact]
    public void Detect_ScatterWithinTolerance_StillCounts()
    {
        // The real measurements span 0.21 to 0.23; that spread must not defeat detection.
        List<double> offsets = [0.21, 0.23, 0.22, 0.21, 0.23];

        var bias = ReferenceBias.Detect(offsets);

        Assert.True(bias.Detected);
        Assert.Equal(5, bias.Agreeing);
        Assert.InRange(bias.OffsetSeconds, 0.21, 0.23);
    }
}
