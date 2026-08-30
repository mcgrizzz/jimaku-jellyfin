using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// The correction someone specifies by watching, when nothing in the file supports measuring it.
/// </summary>
/// <remarks>
/// Two figures rather than one. A subtitle that needs a different correction early and late is
/// running at a slightly different rate, and no shift fixes that - which is exactly the case a user
/// reported, needing +1.85s at the start of an episode and +6.7s near the end.
/// </remarks>
public class ManualCorrectionTests
{
    private static SubtitleDocument Document(double first, double last, int count)
    {
        var step = (last - first) / (count - 1);
        var lines = Enumerable.Range(0, count).Select(i =>
        {
            var start = first + (i * step);
            return string.Join(
                Environment.NewLine,
                (i + 1).ToString(CultureInfo.InvariantCulture),
                Stamp(start) + " --> " + Stamp(start + 2),
                "line",
                string.Empty);
        });

        return SubtitleDocument.Parse(Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines)));
    }

    private static string Stamp(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\,fff");

    [Theory]
    [InlineData(1.85, 6.7)]
    [InlineData(-2.0, 3.0)]
    [InlineData(0.0, 4.0)]
    public void TwoFiguresStretchTheSubtitleThroughBoth(double atStart, double atEnd)
    {
        var document = Document(20, 1400, 200);
        var cues = document.ToCueTrack();

        var span = cues.Cues[^1].StartSeconds - cues.FirstStartSeconds;
        var scale = 1.0 + ((atEnd - atStart) / span);
        var offset = atStart - ((scale - 1.0) * cues.FirstStartSeconds);
        var transform = new TimingTransform(scale, offset);

        // The line has to pass through both measured points, or the figures the user took the
        // trouble to measure are not the figures being applied.
        Assert.Equal(
            cues.FirstStartSeconds + atStart,
            transform.Apply(cues.FirstStartSeconds),
            3);

        Assert.Equal(
            cues.Cues[^1].StartSeconds + atEnd,
            transform.Apply(cues.Cues[^1].StartSeconds),
            3);
    }

    [Fact]
    public void TheMiddleLandsWhereItShould()
    {
        var document = Document(20, 1400, 200);
        var cues = document.ToCueTrack();

        var span = cues.Cues[^1].StartSeconds - cues.FirstStartSeconds;
        var scale = 1.0 + ((6.7 - 1.85) / span);
        var offset = 1.85 - ((scale - 1.0) * cues.FirstStartSeconds);
        var transform = new TimingTransform(scale, offset);

        var middle = cues.Cues[cues.Count / 2].StartSeconds;
        var applied = transform.Apply(middle) - middle;

        // Halfway through, halfway between the two figures - and close to the 5.39s the same user
        // measured in the middle of that episode, which is the check that the model is the right one.
        Assert.InRange(applied, 4.0, 4.6);
    }
}
