using System.IO;
using System.IO.Compression;
using System.Text;
using Jellyfin.Plugin.Jimaku.Sync;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

public class ArchiveExtractorTests
{
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    [Fact]
    public void TryExtract_SeasonPack_PicksTheRequestedEpisode()
    {
        var zip = Zip(
            ("[Group] Show - 01.ass", "episode one"),
            ("[Group] Show - 02.ass", "episode two"),
            ("[Group] Show - 03.ass", "episode three"));

        var bytes = ArchiveExtractor.TryExtract(zip, 2, out var name);

        Assert.NotNull(bytes);
        Assert.Equal("[Group] Show - 02.ass", name);
        Assert.Equal("episode two", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void TryExtract_SingleEntry_TakesItWithoutNeedingAnEpisodeNumber()
    {
        var bytes = ArchiveExtractor.TryExtract(Zip(("subs.ass", "only one")), null, out var name);

        Assert.NotNull(bytes);
        Assert.Equal("subs.ass", name);
    }

    [Fact]
    public void TryExtract_NoMatchingEpisode_ReturnsNothing()
    {
        var zip = Zip(("[Group] Show - 01.ass", "one"), ("[Group] Show - 02.ass", "two"));
        Assert.Null(ArchiveExtractor.TryExtract(zip, 9, out _));
    }

    [Fact]
    public void TryExtract_IgnoresNonSubtitleEntries()
    {
        var zip = Zip(("readme.txt", "notes"), ("fonts.ttf", "binary"));
        Assert.Null(ArchiveExtractor.TryExtract(zip, 1, out _));
    }

    [Fact]
    public void TryExtract_NotAnArchive_ReturnsNothingRatherThanThrowing()
    {
        Assert.Null(ArchiveExtractor.TryExtract(Encoding.UTF8.GetBytes("not a zip at all"), 1, out _));
    }
}
