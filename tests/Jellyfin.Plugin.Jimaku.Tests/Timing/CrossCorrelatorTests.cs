using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

public class CrossCorrelatorTests
{
    private const double EpisodeSeconds = 1440;

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.42)]
    [InlineData(-2.75)]
    [InlineData(12.5)]
    [InlineData(-30.0)]
    public void Correlate_RecoversAKnownConstantOffset(double offsetSeconds)
    {
        var truth = SyntheticTrack.Episode();
        var reference = ActivitySignal.FromCues(truth, EpisodeSeconds);
        var probe = ActivitySignal.FromCues(
            SyntheticTrack.Transform(truth, 1.0, -offsetSeconds),
            EpisodeSeconds);

        var correlator = new CrossCorrelator(reference, probe.Length);
        var peak = correlator.Correlate(probe, maxLagBins: 6000, guardBins: 100);

        // Within a single 10 ms bin.
        Assert.InRange(peak.LagSeconds, offsetSeconds - 0.011, offsetSeconds + 0.011);
        Assert.True(peak.Correlation > 0.95, $"correlation was {peak.Correlation}");
    }

    [Fact]
    public void Correlate_IdenticalSignals_ScoresCorrelationOfOne()
    {
        var track = SyntheticTrack.Episode();
        var signal = ActivitySignal.FromCues(track, EpisodeSeconds);

        var peak = new CrossCorrelator(signal, signal.Length)
            .Correlate(signal, maxLagBins: 6000, guardBins: 100);

        Assert.Equal(0, peak.LagBins);
        Assert.InRange(peak.Correlation, 0.999, 1.0001);
    }

    [Fact]
    public void Correlate_UnrelatedTracks_ScoresLowAndUnconfidently()
    {
        var reference = ActivitySignal.FromCues(SyntheticTrack.Episode(seed: 1), EpisodeSeconds);
        var probe = ActivitySignal.FromCues(SyntheticTrack.Episode(seed: 999), EpisodeSeconds);

        var peak = new CrossCorrelator(reference, probe.Length)
            .Correlate(probe, maxLagBins: 6000, guardBins: 100);

        // The wrong episode still produces *a* best lag - it always will. What distinguishes it is
        // that the peak is neither strong nor unique, which is what the accept gate tests.
        Assert.True(peak.Correlation < 0.30, $"correlation was {peak.Correlation}, expected a weak match");
        Assert.True(peak.PeakRatio < 1.4, $"Peak ratio was {peak.PeakRatio}, expected an ambiguous surface");
    }

    [Fact]
    public void Correlate_CorrectMatch_IsBothStrongAndUnique()
    {
        var truth = SyntheticTrack.Episode();
        var reference = ActivitySignal.FromCues(truth, EpisodeSeconds);
        var probe = ActivitySignal.FromCues(SyntheticTrack.Transform(truth, 1.0, -4.0), EpisodeSeconds);

        var peak = new CrossCorrelator(reference, probe.Length)
            .Correlate(probe, maxLagBins: 6000, guardBins: 100);

        Assert.True(peak.Correlation > 0.9);
        Assert.True(peak.PeakRatio > 1.4, $"Peak ratio was {peak.PeakRatio}");
    }

    [Fact]
    public void Correlate_EmptyProbe_ReturnsNoPeak()
    {
        var reference = ActivitySignal.FromCues(SyntheticTrack.Episode(), EpisodeSeconds);
        var probe = ActivitySignal.FromCues(CueTrack.Empty, EpisodeSeconds);

        var peak = new CrossCorrelator(reference, Math.Max(probe.Length, 1))
            .Correlate(probe, maxLagBins: 6000, guardBins: 100);

        Assert.Equal(CorrelationPeak.None, peak);
    }
}

public class RealFftTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(1000, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void NextPowerOfTwo_RoundsUp(int input, int expected)
    {
        Assert.Equal(expected, RealFft.NextPowerOfTwo(input));
    }

    [Fact]
    public void ForwardThenInverse_RoundTrips()
    {
        var random = new Random(7);
        var re = new double[256];
        var im = new double[256];
        var original = new double[256];
        for (var i = 0; i < re.Length; i++)
        {
            original[i] = re[i] = random.NextDouble();
        }

        RealFft.Forward(re, im);
        RealFft.Inverse(re, im);

        for (var i = 0; i < re.Length; i++)
        {
            Assert.InRange(re[i] - original[i], -1e-9, 1e-9);
        }
    }
}
