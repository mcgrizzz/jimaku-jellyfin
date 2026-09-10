using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Sync;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// The limit on what "apply it anyway" is allowed to apply.
/// </summary>
/// <remarks>
/// Waiving verification says the evidence was thin, not that any answer will do. A subtitle for the
/// wrong episode still produces a measurement, and taking it on trust wrote a hundred and four
/// seconds of shift with the runtime stretched one and a half percent - a correction no timing
/// error produces, from a comparison whose uniqueness was exactly 1.00.
/// </remarks>
public class SaneCorrectionTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1400;

    private static CueTrack Dialogue(int seed, int count, double offset = 0)
    {
        var random = new Random(seed);
        var cues = new List<Cue>(count);
        var t = 15.0;

        for (var i = 0; i < count; i++)
        {
            t += 2.0 + (random.NextDouble() * 7.0);
            cues.Add(new Cue(t + offset, t + offset + 1.0 + (random.NextDouble() * 2.0)));
        }

        return new CueTrack(cues);
    }

    private static SubtitleDocument Document(CueTrack track) =>
        SubtitleDocument.Parse(Encoding.UTF8.GetBytes(string.Join(
            Environment.NewLine,
            track.Cues.Select((c, i) => string.Join(
                Environment.NewLine,
                (i + 1).ToString(CultureInfo.InvariantCulture),
                Stamp(c.StartSeconds) + " --> " + Stamp(c.EndSeconds),
                "line",
                string.Empty)))));

    private static string Stamp(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\,fff");

    private static ReferenceTrack Reference(CueTrack track) =>
        new(ActivitySignal.FromCues(track, EpisodeSeconds), "test", track);

    [Fact]
    public void AModestShiftIsAppliedWhenVerificationIsWaived()
    {
        // The case the option exists for: the measurement is probably right, the evidence just is
        // not strong enough to act on unattended.
        var reference = Dialogue(1, 160);
        var candidate = Document(Dialogue(1, 160, offset: -2.5));

        var aligner = new SubtitleAligner(new PluginConfiguration());
        var result = aligner.Align(Reference(reference), candidate, false, false);

        output.WriteLine($"{result.Verdict}: {result.Transform.Describe()}");

        Assert.Equal(SyncVerdict.ConstantOffset, result.Verdict);
        Assert.Equal(2.5, result.Transform.OffsetSeconds, 1);
    }

    [Theory]
    [InlineData(103.997, 0.98464)]
    [InlineData(-90.0, 1.0)]
    [InlineData(0.5, 1.3)]
    public void AnAbsurdCorrectionIsOutsideTheLimits(double offset, double scale)
    {
        // These are the numbers that must never reach a file. The check is on size, not on
        // confidence: no timing error moves a subtitle by a hundred seconds, however the
        // measurement was arrived at.
        var configuration = new PluginConfiguration();
        var transform = new TimingTransform(scale, offset);

        var sane = Math.Abs(transform.OffsetSeconds) <= configuration.MaxOffsetSeconds
                   && Math.Abs(transform.Scale - 1.0) <= configuration.MaxScaleDeviation;

        Assert.False(sane);
    }

    [Theory]
    [InlineData(2.5, 1.0)]
    [InlineData(-29.0, 1.0)]
    [InlineData(1.0, 1.0009)]
    public void AnOrdinaryCorrectionIsInside(double offset, double scale)
    {
        var configuration = new PluginConfiguration();
        var transform = new TimingTransform(scale, offset);

        var sane = Math.Abs(transform.OffsetSeconds) <= configuration.MaxOffsetSeconds
                   && Math.Abs(transform.Scale - 1.0) <= configuration.MaxScaleDeviation;

        Assert.True(sane);
    }
}
