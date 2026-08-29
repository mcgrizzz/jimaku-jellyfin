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
    public void ImageBasedTracksAreReportedAsTheReason()
    {
        // The common case on disc rips: several subtitle tracks, none of them readable as text.
        var report = new ReferenceReport { FromSubtitles = false };
        report.Streams.Add(new ReferenceStreamInfo { Index = 2, Codec = "pgssub", Status = "image-based (pgssub)" });
        report.Streams.Add(new ReferenceStreamInfo { Index = 3, Codec = "pgssub", Status = "image-based (pgssub)" });

        var explanation = report.Explain();

        Assert.Contains("2 embedded subtitle track(s) could be used", explanation, StringComparison.Ordinal);
        Assert.Contains("voice activity", explanation, StringComparison.Ordinal);
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
