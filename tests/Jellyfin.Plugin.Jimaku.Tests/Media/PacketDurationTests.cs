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

/// <summary>
/// Splitting a combined packet listing back out per stream.
/// </summary>
/// <remarks>
/// Every subtitle track is read in one demux pass now, keyed on the index ffprobe itself reports.
/// Asking for one stream at a time meant trusting that the index the library reports is the index
/// ffprobe uses; on the file that prompted this, one track came back with 318 cues and the other
/// with nothing at all, and the labels said the empty one was the dialogue.
/// </remarks>
public class PacketStreamGroupingTests
{
    [Fact]
    public void PacketsAreSplitByTheStreamThatReportedThem()
    {
        var grouped = SubtitlePacketTimings.GroupByStream([
            "4,10.000000,2.000000,8000",
            "5,10.500000,2.000000,400",
            "4,15.000000,2.000000,8000",
            "5,20.000000,2.000000,400",
            "5,30.000000,2.000000,400",
        ]);

        Assert.Equal(2, grouped.Count);
        Assert.Equal(2, grouped[4].Count);
        Assert.Equal(3, grouped[5].Count);
        Assert.Equal("10.000000,2.000000,8000", grouped[4][0]);
    }

    [Fact]
    public void EachStreamParsesIndependently()
    {
        var grouped = SubtitlePacketTimings.GroupByStream([
            "4,10.000000,2.000000,8000",
            "4,20.000000,2.000000,8000",
            "5,12.000000,1.000000,8000",
        ]);

        Assert.Equal(2, SubtitlePacketTimings.Parse(grouped[4]).Count);
        Assert.Equal(1, SubtitlePacketTimings.Parse(grouped[5]).Count);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData(",10.0,2.0,8000")]
    public void MalformedLinesAreSkipped(string line)
    {
        Assert.Empty(SubtitlePacketTimings.GroupByStream([line]));
    }
}
