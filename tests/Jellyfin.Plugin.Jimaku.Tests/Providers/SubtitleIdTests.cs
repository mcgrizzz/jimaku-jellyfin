using System;
using Jellyfin.Plugin.Jimaku.Providers;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Providers;

/// <summary>
/// The identifier a client hands back when a subtitle is downloaded.
/// </summary>
/// <remarks>
/// It has to survive a query-string round trip, which is why it is Base64Url rather than plain
/// Base64: the Emby plugin this replaces had to strip injected spaces out of its padded UTF-16
/// encoding. It also has to distinguish "this exact file" from "choose for me", because those two
/// requests take opposite paths - one verifies a named file and writes it regardless, the other
/// measures every candidate and declines if none convinces.
/// </remarks>
public class SubtitleIdTests
{
    [Fact]
    public void ANamedFileRoundTrips()
    {
        var id = SubtitleId.Encode(
            712,
            "[AnimeOut] Mushoku Tensei - 14 [BD Remux].srt",
            "https://jimaku.cc/entry/712/download/x.srt",
            "/media/Show S01E14.mkv");

        var (entryId, fileName, url, itemPath, auto) = SubtitleId.Decode(id);

        Assert.Equal(712, entryId);
        Assert.Equal("[AnimeOut] Mushoku Tensei - 14 [BD Remux].srt", fileName);
        Assert.Equal("https://jimaku.cc/entry/712/download/x.srt", url);
        Assert.Equal("/media/Show S01E14.mkv", itemPath);
        Assert.False(auto);
    }

    [Fact]
    public void TheAutomaticEntryCarriesOnlyThePathAndItsFlag()
    {
        var (entryId, fileName, url, itemPath, auto) =
            SubtitleId.Decode(SubtitleId.EncodeAuto("/media/Show S01E14.mkv"));

        Assert.True(auto);
        Assert.Equal("/media/Show S01E14.mkv", itemPath);
        Assert.Equal(0, entryId);
        Assert.Equal(string.Empty, fileName);
        Assert.Equal(string.Empty, url);
    }

    [Theory]
    [InlineData("無職転生.S01E14.只より高いものはない.WEBRip.Netflix.ja[cc].srt")]
    [InlineData("[Nekomoe kissaten&VCB-Studio] Show [14][Ma10p_1080p][x265_flac][CHS, JPN].ass")]
    [InlineData("file with spaces + plus & ampersand ?query #hash.srt")]
    public void AwkwardFileNamesSurviveTheRoundTrip(string fileName)
    {
        var id = SubtitleId.Encode(1, fileName, "https://example/x", "/media/a.mkv");

        // Base64Url, so nothing here needs escaping on the way through a query string.
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('=', id);
        Assert.Equal(fileName, SubtitleId.Decode(id).FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyIdentifierIsRejected(string id) =>
        Assert.ThrowsAny<ArgumentException>(() => SubtitleId.Decode(id));

    [Fact]
    public void RubbishIsRejectedRatherThanMisread() =>
        Assert.ThrowsAny<Exception>(() => SubtitleId.Decode("not-a-valid-identifier"));
}
