using System;
using Jellyfin.Plugin.Jimaku.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// Overlap and onset comparisons live on different scales and carry different accept floors, so
/// they cannot be ranked against each other by raw correlation.
/// </summary>
/// <remarks>
/// Measured against the chosen reference on a real episode: overlap gave r=0.80 with uniqueness
/// 1.11, onsets gave r=0.52 with uniqueness 6.12. Overlap wins on raw correlation and then fails
/// its own stricter uniqueness bar, so every candidate was declined - while onsets cleared both of
/// their floors with room to spare. Comparing how far each clears its own thresholds picks
/// correctly.
/// </remarks>
public class RepresentationChoiceTests(ITestOutputHelper output)
{
    /// <summary>Mirrors SubtitleAligner.Hypothesis.Margin.</summary>
    private static double Margin(double correlation, double peakRatio, double minCorrelation, double minPeakRatio) =>
        Math.Min(correlation / minCorrelation, peakRatio / minPeakRatio);

    private static bool Passes(double correlation, double peakRatio, double minCorrelation, double minPeakRatio) =>
        correlation >= minCorrelation && peakRatio >= minPeakRatio;

    [Fact]
    public void TheRealMeasurements_ChooseOnsetsNotOverlap()
    {
        var configuration = new PluginConfiguration();

        var overlapPasses = Passes(0.80, 1.11, configuration.MinCorrelation, configuration.MinPeakRatio);
        var onsetPasses = Passes(0.52, 6.12, configuration.MinOnsetCorrelation, configuration.MinPeakRatio);

        var overlapMargin = Margin(0.80, 1.11, configuration.MinCorrelation, configuration.MinPeakRatio);
        var onsetMargin = Margin(0.52, 6.12, configuration.MinOnsetCorrelation, configuration.MinPeakRatio);

        output.WriteLine($"overlap passes={overlapPasses} margin={overlapMargin:0.00}");
        output.WriteLine($"onsets  passes={onsetPasses} margin={onsetMargin:0.00}");

        // Overlap fails on uniqueness despite the higher correlation that used to win it the choice.
        Assert.False(overlapPasses);
        Assert.True(onsetPasses);
        Assert.True(onsetMargin > overlapMargin);
    }

    [Fact]
    public void RawCorrelation_WouldHavePickedTheFailingRepresentation()
    {
        // Guards the reasoning, not just the outcome: the old rule is recorded here so the reason
        // it was wrong stays visible.
        Assert.True(0.80 > 0.52, "overlap really did score higher on raw correlation");

        var configuration = new PluginConfiguration();
        Assert.False(Passes(0.80, 1.11, configuration.MinCorrelation, configuration.MinPeakRatio));
    }

    [Theory]
    // Both comfortably good: prefer the one further above its own floors.
    [InlineData(0.90, 3.00, 0.60, 8.00, false)]
    // Overlap strong and unique, onsets marginal: overlap should win.
    [InlineData(0.95, 4.00, 0.30, 1.25, true)]
    public void Margin_PrefersWhicheverClearsItsOwnThresholdsFurther(
        double overlapCorrelation,
        double overlapPeak,
        double onsetCorrelation,
        double onsetPeak,
        bool expectOverlap)
    {
        var configuration = new PluginConfiguration();

        var overlap = Margin(overlapCorrelation, overlapPeak, configuration.MinCorrelation, configuration.MinPeakRatio);
        var onset = Margin(onsetCorrelation, onsetPeak, configuration.MinOnsetCorrelation, configuration.MinPeakRatio);

        Assert.Equal(expectOverlap, overlap > onset);
    }

    [Fact]
    public void EveryRealCandidate_ClearsTheGateOnOnsets()
    {
        // All seven declined on overlap uniqueness of 1.11-1.15. On cue starts they measure around
        // 5-6, well clear of the 1.20 floor.
        var configuration = new PluginConfiguration();

        foreach (var (correlation, peak) in new[] { (0.52, 6.12), (0.50, 6.03), (0.45, 5.18), (0.55, 5.94) })
        {
            Assert.True(
                Passes(correlation, peak, configuration.MinOnsetCorrelation, configuration.MinPeakRatio),
                $"r={correlation} uniqueness={peak} should clear the onset gate");
        }
    }
}
