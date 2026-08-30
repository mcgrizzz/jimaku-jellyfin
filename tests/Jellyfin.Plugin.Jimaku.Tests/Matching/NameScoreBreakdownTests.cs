using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// What the filename score is actually made of, on a real set of candidates.
/// </summary>
public class NameScoreBreakdownTests(ITestOutputHelper output)
{
    private const string Video =
        "Jujutsu.Kaisen.S01E07.1080p.Blu-Ray.10-Bit.Dual-Audio.LPCM.x265-iAHD.mkv";

    [Theory]
    [InlineData("[IrizaRaws] Jujutsu Kaisen - 07 (BDRip 1920x1080 x264 10bit FLAC).srt")]
    [InlineData("[Erai-raws] Jujutsu Kaisen - 07 [1080p].srt")]
    [InlineData("[SubsPlease] Jujutsu Kaisen - 07v2 (1080p) [DB074C8D].srt")]
    [InlineData("(Acez-Yuu) Jujutsu Kaisen - 07.srt")]
    [InlineData("呪術廻戦.S01E07.急襲.WEBRip.Amazon.ja-jp[sdh].srt")]
    [InlineData("呪術廻戦.S01E07.急襲.WEBRip.Netflix.ja[cc].srt")]
    public void Breakdown(string subtitle)
    {
        var parsed = ReleaseInfo.Parse(subtitle);
        var match = ReleaseMatcher.Compare(Video, subtitle, 7);

        output.WriteLine(
            $"{match.Score,3}  title={parsed.Title ?? "-"}  group={parsed.ReleaseGroup ?? "-"}  "
            + $"source={parsed.SourceFamily ?? "-"}  res={parsed.Resolution ?? "-"}  "
            + $"video={parsed.VideoTerm ?? "-"}  audio={parsed.AudioTerm ?? "-"}  ep={parsed.EpisodeNumber?.ToString() ?? "-"}");
        output.WriteLine($"     notes: {match.Notes}");

        Assert.True(match.Score >= 0);
    }

    [Fact]
    public void TheTwoWaysOfWritingAFrameSizeCompareEqual()
    {
        // Release names split about evenly between the two spellings, and treating them as
        // different cost the disc-sourced candidate the resolution match it had earned - against a
        // stream rip that happened to write it the other way.
        var disc = ReleaseMatcher.Compare(
            Video, "[IrizaRaws] Jujutsu Kaisen - 07 (BDRip 1920x1080 x264 10bit FLAC).srt", 7);
        var web = ReleaseMatcher.Compare(Video, "[Erai-raws] Jujutsu Kaisen - 07 [1080p].srt", 7);

        Assert.Equal("1080p", ReleaseInfo.NormalizeResolution("1920x1080"));
        Assert.True(disc.Score > web.Score, $"disc {disc.Score} vs web {web.Score}");
    }

    [Theory]
    [InlineData("呪術廻戦.S01E07.急襲.WEBRip.Amazon.ja-jp[sdh].srt")]
    [InlineData("呪術廻戦.S01E07.急襲.WEBRip.Netflix.ja[cc].srt")]
    [InlineData("Show - 07 [forced].srt")]
    public void AnAccessibilityTagIsNotAReleaseGroup(string fileName)
    {
        // It sits where a group tag sits and parses as one. Harmless for scoring, since it simply
        // fails to match - but the series preference learns from release groups, and would have
        // concluded that a show is best served by the group "sdh".
        Assert.Null(ReleaseInfo.Parse(fileName).ReleaseGroup);
    }

    [Fact]
    public void TheTitleIsNotComparedAtAll()
    {
        // Worth pinning because it is surprising, and because it is the answer to "why do the
        // Japanese-titled files score lower": they do not, on account of their titles. They score
        // lower for naming no resolution and coming from a different source.
        var english = ReleaseMatcher.Compare(Video, "Jujutsu Kaisen - 07 [1080p].srt", 7);
        var japanese = ReleaseMatcher.Compare(Video, "呪術廻戦 - 07 [1080p].srt", 7);

        Assert.Equal(english.Score, japanese.Score);
    }

    [Fact]
    public void TheLocalFileItself()
    {
        var parsed = ReleaseInfo.Parse(Video);
        output.WriteLine(
            $"video: title={parsed.Title}  source={parsed.SourceFamily}  res={parsed.Resolution}  "
            + $"video={parsed.VideoTerm}  audio={parsed.AudioTerm}  ep={parsed.EpisodeNumber}");

        Assert.NotNull(parsed.Title);
    }
}
