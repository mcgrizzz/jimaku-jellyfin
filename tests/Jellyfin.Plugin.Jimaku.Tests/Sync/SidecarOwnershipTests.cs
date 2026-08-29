using System;
using Jellyfin.Plugin.Jimaku.Sync;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// Which files on disk the plugin is willing to treat as its own.
/// </summary>
/// <remarks>
/// This gates deletion, so it errs towards leaving files alone. The naming rule mirrors Jellyfin's
/// external-file resolver, which reads tokens right to left and takes the first language it
/// recognises - so the language is always the final token, and anything else is somebody else's
/// file.
/// </remarks>
public class SidecarOwnershipTests
{
    private const string BaseName = "Mushoku Tensei S01E09 [BD 1080p]";

    [Theory]
    [InlineData("Mushoku Tensei S01E09 [BD 1080p].jpn.ass")]
    [InlineData("Mushoku Tensei S01E09 [BD 1080p].jpn.srt")]
    [InlineData("Mushoku Tensei S01E09 [BD 1080p].1.jpn.ass")]
    [InlineData("Mushoku Tensei S01E09 [BD 1080p].JPN.ASS")]
    public void OurOwnNamingIsRecognised(string fileName) =>
        Assert.True(SidecarNaming.LooksLikeOurs("/media/" + fileName, BaseName, "jpn"));

    [Theory]
    [InlineData("Mushoku Tensei S01E09 [BD 1080p].eng.ass")]     // a different language
    [InlineData("Mushoku Tensei S01E09 [BD 1080p].jpn.txt")]     // not a subtitle
    [InlineData("Mushoku Tensei S01E09 [BD 1080p].jpn.sdh.ass")] // language is not the last token
    [InlineData("Some Other Episode.jpn.ass")]                    // a different episode
    [InlineData("Mushoku Tensei S01E09 [BD 1080p].ass")]          // no language token at all
    public void EverythingElseIsLeftAlone(string fileName) =>
        Assert.False(SidecarNaming.LooksLikeOurs("/media/" + fileName, BaseName, "jpn"));
}
