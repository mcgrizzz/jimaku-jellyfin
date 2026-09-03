using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// Which episode number a candidate is allowed to claim.
/// </summary>
/// <remarks>
/// The entry for the second cour of a split season holds both numberings at once: uploads named by
/// fansubbers who numbered the season straight through, and uploads named by the part. Jimaku
/// returns both, because its own relations table knows they describe the same episode. Enforcing a
/// single number against that list rejected six of seven correct files for a season one episode
/// fourteen, leaving one that happened to use the other convention.
/// </remarks>
public class EpisodeNumberingTests
{
    private const string Video =
        "[Feibanyama] Mushoku Tensei Jobless Reincarnation S01E14 [BILIBILI WebRip 2160p].mkv";

    [Theory]
    [InlineData("[AnimeOut] Mushoku Tensei Jobless Reincarnation - 14 BD Remux 720p FLAC.srt")]
    [InlineData("[SubsPlease] Mushoku Tensei - 14 (1080p) [63A05157].ja.srt")]
    [InlineData("無職転生.～異世界行ったら本気だす～.S01E14.只より高いものはない.WEBRip.Netflix.ja[cc].srt")]
    public void TheLibrarysOwnNumberingIsAccepted(string subtitle)
    {
        // Season numbering, against an entry whose own episode number for this is three.
        var match = ReleaseMatcher.Compare(Video, subtitle, expectedEpisode: 3, alternateEpisode: 14);

        Assert.False(match.EpisodeMismatch);
    }

    [Fact]
    public void ThePartsOwnNumberingIsAcceptedToo()
    {
        var match = ReleaseMatcher.Compare(
            Video,
            "[Funimation] Mushoku Tensei S1 Part 2 - E03 [retimed from Netflix].srt",
            expectedEpisode: 3,
            alternateEpisode: 14);

        Assert.False(match.EpisodeMismatch);
    }

    [Fact]
    public void AGenuinelyDifferentEpisodeIsStillRejected()
    {
        var match = ReleaseMatcher.Compare(
            Video,
            "[SubsPlease] Mushoku Tensei - 09 (1080p).ja.srt",
            expectedEpisode: 3,
            alternateEpisode: 14);

        Assert.True(match.EpisodeMismatch);
        Assert.Contains("3 or 14", match.Notes, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NoExpectationMeansNoVeto()
    {
        // What a server-filtered listing gets: Jimaku has already decided which episode these are,
        // with a relations table this cannot see.
        var match = ReleaseMatcher.Compare(Video, "[Nekomoe] Mushoku Tensei [14].ass", null);

        Assert.False(match.EpisodeMismatch);
    }

    [Fact]
    public void ASingleExpectationStillWorksOnItsOwn()
    {
        Assert.True(ReleaseMatcher.Compare(Video, "Show - 09.srt", expectedEpisode: 14).EpisodeMismatch);
        Assert.False(ReleaseMatcher.Compare(Video, "Show - 14.srt", expectedEpisode: 14).EpisodeMismatch);
    }
}
