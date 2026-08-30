using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Media;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Media;

/// <summary>
/// What happens when a container reports no cue durations at all.
/// </summary>
/// <remarks>
/// Extending each cue to the start of the next is sound when durations are occasionally missing and
/// disastrous when they always are. Dialogue sits a few seconds apart, so every cue stretches to
/// meet its neighbour and the track becomes one continuous block - and a signal that is on almost
/// all the time correlates about equally well with everything, so every candidate scores alike and
/// the choice between them is made on noise.
/// </remarks>
public class PacketDurationTests
{
    private static List<string> Packets(int count, double spacing, string duration) =>
        Enumerable.Range(0, count)
            .Select(i => string.Create(
                CultureInfo.InvariantCulture,
                $"{20 + (i * spacing):0.000000},{duration},7000"))
            .ToList();

    [Fact]
    public void NoDurationsAnywhereDoesNotProduceOneContinuousBlock()
    {
        var track = SubtitlePacketTimings.Parse(Packets(300, 4.5, "N/A"));

        Assert.Equal(300, track.Count);
        Assert.True(
            SubtitlePacketTimings.DutyCycle(track) < 0.6,
            $"on screen {SubtitlePacketTimings.DutyCycle(track):P0} of the time");
    }

    [Fact]
    public void RealDurationsAreStillUsedWhenPresent()
    {
        var track = SubtitlePacketTimings.Parse(Packets(300, 4.5, "3.000000"));

        Assert.Equal(3.0, SubtitlePacketTimings.MedianDuration(track), 3);
    }

    [Fact]
    public void OccasionalGapsStillBridgeToTheNextCue()
    {
        // Mostly good data with one missing duration: filling it from the next cue is right here.
        var lines = Packets(20, 5.0, "2.000000");
        lines[5] = "45.000000,N/A,7000";

        var track = SubtitlePacketTimings.Parse(lines);

        Assert.Equal(5.0, track.Cues[5].DurationSeconds, 2);
    }

    [Fact]
    public void CloselySpacedCuesAreNotStretchedPastEachOther()
    {
        var track = SubtitlePacketTimings.Parse(Packets(50, 1.2, "N/A"));

        // The final cue has nothing after it to be bounded by, so it takes the nominal length.
        Assert.All(
            track.Cues.Take(track.Count - 1),
            c => Assert.True(c.DurationSeconds <= 1.2001, $"cue ran {c.DurationSeconds:0.000}s"));
    }
}
