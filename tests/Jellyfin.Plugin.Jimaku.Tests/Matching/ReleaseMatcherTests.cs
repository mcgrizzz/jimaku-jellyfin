using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

public class ReleaseInfoTests(ITestOutputHelper output)
{
    [Fact]
    public void Parse_CanonicalAnimeFilename_ExtractsEveryField()
    {
        var info = ReleaseInfo.Parse("[BM&T] Toradora! - 07v2 - Pool Opening (2008) [720p Hi10p FLAC] [BD] [8F59F2BA].mkv");

        output.WriteLine($"title={info.Title} group={info.ReleaseGroup} ep={info.EpisodeNumber} res={info.Resolution} src={info.Source} crc={info.Checksum}");

        Assert.Equal("Toradora!", info.Title);
        Assert.Equal("BM&T", info.ReleaseGroup);
        Assert.Equal(7, info.EpisodeNumber);
        Assert.Equal("720p", info.Resolution);
        Assert.Equal("8F59F2BA", info.Checksum);
    }

    [Fact]
    public void Parse_TypicalSimulcastFilename_ExtractsGroupAndEpisode()
    {
        var info = ReleaseInfo.Parse("[SubsPlease] Sousou no Frieren - 12 (1080p) [A1B2C3D4].mkv");

        Assert.Equal("SubsPlease", info.ReleaseGroup);
        Assert.Equal(12, info.EpisodeNumber);
        Assert.Equal("1080p", info.Resolution);
        Assert.Equal("A1B2C3D4", info.Checksum);
    }
}

public class ReleaseMatcherTests
{
    [Fact]
    public void Compare_SameChecksum_IsAnExactReleaseMatch()
    {
        // The strongest evidence available: the subtitle was released against these exact bytes.
        var match = ReleaseMatcher.Compare(
            "[SubsPlease] Sousou no Frieren - 12 (1080p) [A1B2C3D4].mkv",
            "[SubsPlease] Sousou no Frieren - 12 (1080p) [A1B2C3D4].ass",
            12);

        Assert.True(match.IsExactRelease);
        Assert.Equal(100, match.Score);
    }

    [Fact]
    public void Compare_DifferentEpisode_IsRejectedOutright()
    {
        var match = ReleaseMatcher.Compare(
            "[SubsPlease] Sousou no Frieren - 12 (1080p) [A1B2C3D4].mkv",
            "[SubsPlease] Sousou no Frieren - 05 (1080p) [99999999].ass",
            12);

        Assert.True(match.EpisodeMismatch);
        Assert.Equal(0, match.Score);
        Assert.Contains("episode 5", match.Notes, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_SameGroupDifferentChecksum_ScoresWell()
    {
        var match = ReleaseMatcher.Compare(
            "[SubsPlease] Sousou no Frieren - 12 (1080p) [A1B2C3D4].mkv",
            "[SubsPlease] Sousou no Frieren - 12 (1080p) [ZZZZZZZZ].ass",
            12);

        Assert.False(match.IsExactRelease);
        Assert.False(match.EpisodeMismatch);
        Assert.True(match.Score >= 60, $"score was {match.Score}");
    }

    [Fact]
    public void Compare_BroadcastSubtitleOnDiscVideo_FlagsTheSourceMismatch()
    {
        // This is the combination that most reliably predicts a differing cut, so it is surfaced
        // rather than merely scored down: disc releases re-cut openings and drop previews.
        var match = ReleaseMatcher.Compare(
            "[Group] Some Show - 03 [BD 1080p].mkv",
            "[Other] Some Show - 03 [TV 720p].ass",
            3);

        Assert.True(match.SourceMismatch);
        Assert.Contains("TV subtitle on BD video", match.Notes, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_UnrelatedReleases_ScoresLow()
    {
        var match = ReleaseMatcher.Compare(
            "[GroupA] Some Show - 03 [BD 1080p].mkv",
            "Some Show 03 raw.srt",
            3);

        Assert.True(match.Score < 40, $"score was {match.Score}");
    }
}
