using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// Pins down the parts of Anitomy's classification the matcher depends on. These are not obvious
/// from its documentation and getting them wrong fails silently.
/// </summary>
public class ReleaseSourceTests
{
    [Theory]
    [InlineData("[Group] Some Show - 03 [BD 1080p].mkv", "BD")]
    [InlineData("[Group] Some Show - 03 [TV 720p].mkv", "TV")]
    [InlineData("[Group] Some Show - 03 [WEB 720p].mkv", "WEB")]
    [InlineData("[Erai-raws] Show - 03 [1080p][HEVC][Web-DL].mkv", "WEB")]
    [InlineData("[Group] Some Show - 03 [DVDRip].mkv", "DVD")]
    public void Parse_RecognisesTheSourceFamily(string fileName, string expected)
    {
        // Anitomy files "TV" under anime type rather than source; the parser folds it back in,
        // because broadcast-versus-disc is the comparison that predicts a differing cut.
        Assert.Equal(expected, ReleaseInfo.Parse(fileName).SourceFamily);
    }

    [Fact]
    public void Compare_WebAndWebDl_AreTheSameOrigin()
    {
        var match = ReleaseMatcher.Compare(
            "[GroupA] Show - 03 [WEB 1080p].mkv",
            "[GroupB] Show - 03 [Web-DL 1080p].ass",
            3);

        Assert.False(match.SourceMismatch);
        Assert.Contains("same source", match.Notes, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_NoSourceInformation_DoesNotClaimAMismatch()
    {
        var match = ReleaseMatcher.Compare("Show - 03.mkv", "Show - 03.ass", 3);
        Assert.False(match.SourceMismatch);
    }
}
