using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Jimaku.Sync;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// Jellyfin's external-subtitle detection has strict, silent rules. A file that breaks them is
/// simply never picked up, with no error anywhere, so they are pinned down here.
/// </summary>
public class SidecarNamingTests
{
    private const string Video = "/media/Anime/Frieren/Season 1/[SubsPlease] Frieren - 12 (1080p).mkv";

    [Theory]
    [InlineData("jpn", "ass", "[SubsPlease] Frieren - 12 (1080p).jpn.ass")]
    [InlineData("ja", "srt", "[SubsPlease] Frieren - 12 (1080p).ja.srt")]
    [InlineData("JPN", "ASS", "[SubsPlease] Frieren - 12 (1080p).jpn.ass")]
    public void BuildFileName_PutsTheLanguageLastAndLowercasesIt(string tag, string extension, string expected)
    {
        // The parser reads tokens right to left and takes the first language it recognises, so the
        // language has to be the final token before the extension.
        Assert.Equal(expected, SidecarNaming.BuildFileName(Video, tag, extension));
    }

    [Fact]
    public void BuildFileName_StartsWithTheVideoFilenameFollowedByADot()
    {
        // The resolver only considers files whose name begins with the video's name, and a dot is
        // the only delimiter it accepts.
        var name = SidecarNaming.BuildFileName(Video, "jpn", "ass");
        var prefix = Path.GetFileNameWithoutExtension(Video);

        Assert.StartsWith(prefix, name, StringComparison.Ordinal);
        Assert.Equal('.', name[prefix.Length]);
    }

    [Fact]
    public void Resolve_WhenNothingExists_UsesThePlainName()
    {
        var path = SidecarNaming.Resolve("/media", Video, "jpn", "ass", overwrite: false, _ => false);
        Assert.EndsWith(".jpn.ass", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WhenAFileExists_NumbersItRatherThanClobberingIt()
    {
        // Never silently replace a subtitle the user may have placed or corrected themselves.
        var existing = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.Combine("/media", "[SubsPlease] Frieren - 12 (1080p).jpn.ass"),
        };

        var path = SidecarNaming.Resolve("/media", Video, "jpn", "ass", overwrite: false, existing.Contains);

        Assert.EndsWith("[SubsPlease] Frieren - 12 (1080p).1.jpn.ass", path, StringComparison.Ordinal);

        // The language must still be the last token, or the numbered file is not detected at all.
        Assert.EndsWith(".jpn.ass", path, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WithOverwrite_ReusesTheExistingPath()
    {
        var path = SidecarNaming.Resolve("/media", Video, "jpn", "ass", overwrite: true, _ => true);
        Assert.EndsWith("[SubsPlease] Frieren - 12 (1080p).jpn.ass", path, StringComparison.Ordinal);
    }
}
