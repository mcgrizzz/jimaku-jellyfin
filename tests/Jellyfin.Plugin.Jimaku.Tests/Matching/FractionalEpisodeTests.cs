using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// Recognising a recap episode the library has filed under a whole number.
/// </summary>
/// <remarks>
/// Reported as an episode where nothing matched and every correlation sat around 0.15. The file was
/// episode 23.5, a recap airing between two episodes; Jellyfin read the whole number and filed it
/// as 23, so subtitles for episode 23 were fetched for an episode that is not 23. Anitomy is no
/// help - it declines to parse the number at all and puts the whole string in the title - so
/// nothing anywhere noticed, and a wrong episode looks exactly like a wrong subtitle.
/// </remarks>
public class FractionalEpisodeTests
{
    [Theory]
    [InlineData("[Feibanyama] Mushoku Tensei Jobless Reincarnation S01E23.5 [BILIBILI WebRip 2160p HEVC].mkv", 23, "23.5")]
    [InlineData("Show - 12.5 [1080p].mkv", 12, "12.5")]
    [InlineData("Show S02E07.5 - Recap.mkv", 7, "7.5")]
    [InlineData("[Group] Show - E05.5 (BD).mkv", 5, "5.5")]
    [InlineData("Show.S01E23.5.WEBRip.mkv", 23, "23.5")]
    public void AHalfEpisodeIsRecognised(string fileName, int expectedWhole, string expectedText)
    {
        Assert.True(FractionalEpisode.TryParse(fileName, out var whole, out var text));
        Assert.Equal(expectedWhole, whole);
        Assert.Equal(expectedText, text);
    }

    [Theory]
    [InlineData("[Feibanyama] Mushoku Tensei S01E23 [BILIBILI WebRip 2160p HEVC OPUS].mkv")]
    [InlineData("[SubsPlease] Show - 14 (1080p) [63A05157].mkv")]
    [InlineData("Show S01E07 1920x1080 x264 5.1 FLAC.mkv")]
    [InlineData("Show (2021) S01E03 [10.2 Mbps].mkv")]
    [InlineData("Jujutsu.Kaisen.S01E07.1080p.Blu-Ray.10-Bit.Dual-Audio.LPCM.x265-iAHD.mkv")]
    [InlineData("")]
    public void OrdinaryNamesAreNotMistakenForOne(string fileName)
    {
        // The false positives that would matter: a channel count, a bitrate, a resolution. Each
        // would raise a warning on a perfectly ordinary episode and send someone looking for a
        // problem that is not there.
        Assert.False(FractionalEpisode.TryParse(fileName, out _, out _));
    }
}
