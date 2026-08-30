using System;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Media;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Media;

/// <summary>
/// Cue timings recovered from an image-based subtitle track.
/// </summary>
/// <remarks>
/// A PGS track carries no text, which is why it cannot be read as a subtitle - but its timing is
/// stated outright: every display set is a packet with a presentation timestamp, and in Matroska a
/// block duration too. Since alignment compares cue structure and never looks at what a cue says,
/// a picture subtitle is exactly as good a reference as a text one. Getting these parsing rules
/// right is what makes disc rips workable, and they are the releases where the audio fallback is
/// weakest.
/// </remarks>
public class SubtitlePacketTimingTests
{
    [Fact]
    public void BlockDurationsAreUsedWhenPresent()
    {
        // The Matroska case: one packet per subtitle, duration carried by the container.
        var track = SubtitlePacketTimings.Parse([
            "12.500000,2.000000,8102",
            "18.250000,3.500000,9440",
            "30.000000,1.750000,7220",
        ]);

        Assert.Equal(3, track.Count);
        Assert.Equal(12.5, track.Cues[0].StartSeconds, 3);
        Assert.Equal(14.5, track.Cues[0].EndSeconds, 3);
        Assert.Equal(21.75, track.Cues[1].EndSeconds, 3);
    }

    [Fact]
    public void EraseSegmentsEndTheCueBeforeThemRatherThanStartingOne()
    {
        // A display set that clears the screen is a tiny packet. Counted as a cue it would double
        // the apparent cue count and halve every gap - which is exactly the structure alignment
        // keys on, so it would corrupt the reference rather than merely add noise.
        var track = SubtitlePacketTimings.Parse([
            "10.000000,N/A,6400",
            "13.000000,N/A,23",
            "20.000000,N/A,7100",
            "24.500000,N/A,23",
        ]);

        Assert.Equal(2, track.Count);
        Assert.Equal(10.0, track.Cues[0].StartSeconds, 3);
        Assert.Equal(13.0, track.Cues[0].EndSeconds, 3);
        Assert.Equal(20.0, track.Cues[1].StartSeconds, 3);
        Assert.Equal(24.5, track.Cues[1].EndSeconds, 3);
    }

    [Fact]
    public void AMissingDurationIsInferredFromTheNextCueWhenTheRestAreKnown()
    {
        // Bridging to the next cue is only sound when the missing durations are the exception. A
        // track with no durations at all takes the nominal length instead, so that every cue does
        // not stretch to meet its neighbour and erase the gaps between them.
        var track = SubtitlePacketTimings.Parse([
            "1.000000,2.000000,4000",
            "5.000000,N/A,4000",
            "8.000000,2.000000,4000",
            "12.000000,2.000000,4000",
        ]);

        Assert.Equal(8.0, track.Cues[1].EndSeconds, 3);
    }

    [Fact]
    public void AnInferredDurationIsCappedSoAGapDoesNotBecomeACue()
    {
        // Without a cap, a subtitle followed by a five-minute silent stretch would read as a cue
        // five minutes long, swamping the activity signal it is supposed to describe.
        var track = SubtitlePacketTimings.Parse([
            "5.000000,N/A,4000",
            "305.000000,2.000000,4000",
        ]);

        Assert.Equal(15.0, track.Cues[0].EndSeconds, 3);
    }

    [Fact]
    public void TheFinalCueGetsAModestDefaultRatherThanRunningToTheEnd()
    {
        var track = SubtitlePacketTimings.Parse(["100.000000,N/A,4000"]);

        Assert.Single(track.Cues);
        Assert.Equal(102.0, track.Cues[0].EndSeconds, 3);
    }

    [Theory]
    [InlineData("N/A,N/A,4000")]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("-1.0,2.0,4000")]
    public void UnusableLinesAreSkippedRatherThanThrowing(string line)
    {
        Assert.Empty(SubtitlePacketTimings.Parse([line]).Cues);
    }

    [Fact]
    public void FlickersAreDropped()
    {
        var track = SubtitlePacketTimings.Parse([
            "1.000000,0.010000,4000",
            "2.000000,1.500000,4000",
        ]);

        Assert.Single(track.Cues);
        Assert.Equal(2.0, track.Cues[0].StartSeconds, 3);
    }

    [Fact]
    public void ARealisticTrackProducesPlausibleDialogueStructure()
    {
        // Roughly what a PGS dialogue track looks like: frequent short cues with small gaps.
        var lines = Enumerable.Range(0, 300)
            .Select(i => string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{i * 4.0:0.000000},{2.2:0.000000},7000"))
            .ToList();

        var track = SubtitlePacketTimings.Parse(lines);

        Assert.Equal(300, track.Count);
        Assert.All(track.Cues, c => Assert.InRange(c.DurationSeconds, 2.19, 2.21));
    }
}
