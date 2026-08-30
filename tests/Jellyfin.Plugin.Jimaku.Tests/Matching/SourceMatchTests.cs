using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// Telling a source that agrees from a source that was never stated.
/// </summary>
/// <remarks>
/// These collapse into one another if only the mismatch is recorded, and the difference decides
/// which subtitle gets written. A filename that says nothing about its origin produces the same
/// "not mismatched" as one naming the right origin - so a preference built on the absence of a
/// mismatch promoted an untagged fansub over the disc release it was competing against, on a disc
/// video.
/// </remarks>
public class SourceMatchTests
{
    private const string BluRayVideo =
        "Jujutsu.Kaisen.S01E07.1080p.Blu-Ray.10-Bit.Dual-Audio.LPCM.x265-iAHD.mkv";

    [Fact]
    public void ADiscSubtitleOnADiscVideoMatches()
    {
        var match = ReleaseMatcher.Compare(
            BluRayVideo,
            "[IrizaRaws] Jujutsu Kaisen - 07 (BDRip 1920x1080 x264 10bit FLAC).srt",
            7);

        Assert.True(match.SourceMatch);
        Assert.False(match.SourceMismatch);
    }

    [Fact]
    public void AWebSubtitleOnADiscVideoMismatches()
    {
        var match = ReleaseMatcher.Compare(
            BluRayVideo,
            "呪術廻戦.S01E07.急襲.WEBRip.Amazon.ja-jp[sdh].srt",
            7);

        Assert.True(match.SourceMismatch);
        Assert.False(match.SourceMatch);
    }

    [Fact]
    public void ASubtitleNamingNoSourceIsNeitherAMatchNorAMismatch()
    {
        // The case that caused the wrong pick. Silence is not agreement.
        var match = ReleaseMatcher.Compare(BluRayVideo, "(Acez-Yuu) Jujutsu Kaisen - 07.srt", 7);

        Assert.False(match.SourceMatch);
        Assert.False(match.SourceMismatch);
    }

    [Fact]
    public void AVideoNamingNoSourceCannotConfirmAnything()
    {
        var match = ReleaseMatcher.Compare(
            "Jujutsu Kaisen - 07.mkv",
            "[IrizaRaws] Jujutsu Kaisen - 07 (BDRip 1920x1080 x264 10bit FLAC).srt",
            7);

        Assert.False(match.SourceMatch);
        Assert.False(match.SourceMismatch);
    }
}
