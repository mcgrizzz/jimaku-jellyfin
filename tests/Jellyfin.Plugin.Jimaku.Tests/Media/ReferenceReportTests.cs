using System;
using Jellyfin.Plugin.Jimaku.Media;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Media;

/// <summary>
/// The account of what a subtitle's timing was compared against.
/// </summary>
/// <remarks>
/// A run of declines usually says more about the reference than about the subtitles, and the
/// numbers alone cannot distinguish the two. Previously a decline named the method - "band-energy
/// voice activity" - but never the track, so an episode whose subtitle tracks are all image-based
/// looked identical to one where a signs track had been chosen.
/// </remarks>
public class ReferenceReportTests
{
    [Fact]
    public void AnEmbeddedReferenceNeedsNoExcuse()
    {
        var report = new ReferenceReport { FromSubtitles = true, Chosen = "embedded eng subtitles" };

        Assert.Equal(string.Empty, report.Explain());
    }

    [Fact]
    public void AFileWithNoSubtitleTracksSaysSo()
    {
        var report = new ReferenceReport { FromSubtitles = false };

        Assert.Contains("no embedded subtitle track at all", report.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void ImageBasedTracksAreNamedAsTheReason()
    {
        // The case that catches people out. A PGS track appears in Jellyfin's subtitle menu exactly
        // like a text one, so the file looks well supplied with subtitles while offering nothing a
        // timing comparison can read - and the old message said only "band-energy voice activity",
        // which invited the reasonable but wrong guess that a signs track had been picked.
        var report = new ReferenceReport { FromSubtitles = false };
        report.Streams.Add(new ReferenceStreamInfo { Index = 2, Codec = "pgssub", IsText = false });
        report.Streams.Add(new ReferenceStreamInfo { Index = 3, Codec = "pgssub", IsText = false });

        var explanation = report.Explain();

        Assert.Contains("image-based", explanation, StringComparison.Ordinal);
        Assert.Contains("2 embedded subtitle track(s)", explanation, StringComparison.Ordinal);
        Assert.Contains("voice activity", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void TextTracksThatCouldNotBeReadAreDistinguishedFromImageOnes()
    {
        // A different problem with a different fix, so it must not read the same. Here the tracks
        // are readable in principle and extraction failed.
        var report = new ReferenceReport { FromSubtitles = false };
        report.Streams.Add(new ReferenceStreamInfo { Index = 2, Codec = "ass", IsText = true, CueCount = 0 });

        var explanation = report.Explain();

        Assert.Contains("1 text subtitle track(s) could be read", explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("image-based", explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetectorWarningIsCarriedThrough()
    {
        var report = new ReferenceReport
        {
            FromSubtitles = false,
            Note = "Energy-based detection is unreliable for anime.",
        };

        Assert.Contains("Energy-based detection", report.Explain(), StringComparison.Ordinal);
    }
}
