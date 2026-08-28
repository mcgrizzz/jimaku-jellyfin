using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

/// <summary>
/// Measures how far apart good and bad matches actually score, so the accept thresholds in
/// <see cref="Jellyfin.Plugin.Jimaku.Configuration.PluginConfiguration"/> are grounded in observed
/// separation rather than guessed.
/// </summary>
public class ScoreCharacterizationTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1440;

    public static TheoryData<string, int, double, double> Scenarios => new()
    {
        { "identical", 1, 1.0, 0.0 },
        { "offset +3.4s", 1, 1.0, 3.4 },
        { "offset -12s", 1, 1.0, -12.0 },
        { "framerate 25/23.976", 1, 25.0 / 23.976, 0.0 },
        { "framerate 1001/1000", 1, 1001.0 / 1000.0, 0.0 },
        { "wrong episode", 999, 1.0, 0.0 },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Characterize(string label, int probeSeed, double scale, double offset)
    {
        var truth = SyntheticTrack.Episode(seed: 1);
        var reference = ActivitySignal.FromCues(truth, EpisodeSeconds);

        var probeTrack = probeSeed == 1
            ? SyntheticTrack.Transform(truth, 1.0 / scale, -offset)
            : SyntheticTrack.Episode(seed: probeSeed);

        var fits = new LinearFitSearch().Search(reference, probeTrack);
        Assert.NotEmpty(fits);
        var best = fits[0];

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label,-24} scale={best.Scale:0.######} offset={best.OffsetSeconds,7:0.000}s r={best.Correlation:0.000} peakRatio={best.PeakRatio:0.00}"));

        if (probeSeed == 999)
        {
            // The wrong episode must land clearly below anything a real match produces.
            Assert.True(best.Correlation < 0.45, $"wrong-episode correlation {best.Correlation}");
        }
        else
        {
            Assert.True(best.Correlation > 0.80, $"{label} correlation {best.Correlation}");
            Assert.InRange(best.Scale, scale - 1e-6, scale + 1e-6);
            Assert.InRange(best.OffsetSeconds, offset - 0.05, offset + 0.05);
        }
    }

    /// <summary>
    /// Sweeps many unrelated episode pairs to find the worst-case score a wrong match can reach.
    /// The accept thresholds have to clear this, not merely clear one example.
    /// </summary>
    [Fact]
    public void WrongEpisode_NeverReachesTheAcceptThresholds()
    {
        var worstCorrelation = double.NegativeInfinity;
        var worstRatio = double.NegativeInfinity;
        var bestGoodCorrelation = double.PositiveInfinity;
        var bestGoodRatio = double.PositiveInfinity;

        for (var seed = 2; seed <= 21; seed++)
        {
            var truth = SyntheticTrack.Episode(seed: seed);
            var reference = ActivitySignal.FromCues(truth, EpisodeSeconds);

            var wrong = new LinearFitSearch().Search(reference, SyntheticTrack.Episode(seed: seed + 500))[0];
            worstCorrelation = Math.Max(worstCorrelation, wrong.Correlation);
            worstRatio = Math.Max(worstRatio, wrong.PeakRatio);

            var good = new LinearFitSearch().Search(reference, SyntheticTrack.Transform(truth, 1.0, -5.5))[0];
            bestGoodCorrelation = Math.Min(bestGoodCorrelation, good.Correlation);
            bestGoodRatio = Math.Min(bestGoodRatio, good.PeakRatio);
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"wrong: max r={worstCorrelation:0.000} max ratio={worstRatio:0.00} | right: min r={bestGoodCorrelation:0.000} min ratio={bestGoodRatio:0.00}"));

        Assert.True(worstCorrelation < bestGoodCorrelation, "a wrong match outscored a right one");
        Assert.True(worstRatio < bestGoodRatio, "a wrong match was more unique than a right one");
    }
}
