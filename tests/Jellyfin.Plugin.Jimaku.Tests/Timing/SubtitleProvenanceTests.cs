using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

/// <summary>
/// The comment that records where a subtitle came from.
/// </summary>
/// <remarks>
/// This exists because the sidecar's filename cannot carry it: Jellyfin's external-file resolver
/// dictates the name entirely, so two files from completely different Jimaku uploads are
/// indistinguishable on disk. The stamp is the only thing that survives the plugin's data folder
/// being lost - so it has to be both readable back and harmless to every consumer of the file.
/// </remarks>
public class SubtitleProvenanceTests
{
    private const string Script = """
        [Script Info]
        Title: Something
        ScriptType: v4.00+

        [V4+ Styles]
        Format: Name, Fontname, Fontsize
        Style: Default,Arial,48

        [Events]
        Format: Layer, Start, End, Style, Text
        Dialogue: 0,0:00:01.00,0:00:03.00,Default,こんにちは
        Dialogue: 0,0:00:04.50,0:00:06.25,Default,{\k30}おは{\k20}よう
        """;

    private static string Line() => SubtitleProvenance.BuildLine(
        "[AnimeOut] Mushoku Tensei - 09 [BD 1080p].ass",
        4321,
        new TimingTransform(1.0, 0.21),
        new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TheStampGoesInsideScriptInfo()
    {
        var stamped = SubtitleProvenance.Stamp(Script, SubtitleFormatKind.Ass, Line());
        var lines = stamped.Split('\n');

        Assert.Equal("[Script Info]", lines[0].Trim());
        Assert.StartsWith(SubtitleProvenance.Marker, lines[1].Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void WhatIsWrittenCanBeReadBack()
    {
        var stamped = SubtitleProvenance.Stamp(Script, SubtitleFormatKind.Ass, Line());
        var read = SubtitleProvenance.Read(stamped);

        Assert.NotNull(read);
        Assert.Contains("[AnimeOut] Mushoku Tensei - 09 [BD 1080p].ass", read, StringComparison.Ordinal);
        Assert.Contains("entry 4321", read, StringComparison.Ordinal);
        Assert.Contains("+0.210s", read, StringComparison.Ordinal);
    }

    [Fact]
    public void ReStampingReplacesRatherThanAccumulates()
    {
        var once = SubtitleProvenance.Stamp(Script, SubtitleFormatKind.Ass, Line());
        var twice = SubtitleProvenance.Stamp(
            once,
            SubtitleFormatKind.Ass,
            SubtitleProvenance.BuildLine("other.ass", 9, TimingTransform.Identity, DateTimeOffset.UtcNow));

        var marks = twice.Split('\n').Count(l => l.TrimStart().StartsWith(SubtitleProvenance.Marker, StringComparison.Ordinal));

        Assert.Equal(1, marks);
        Assert.Contains("other.ass", SubtitleProvenance.Read(twice)!, StringComparison.Ordinal);
    }

    [Fact]
    public void AStampedScriptStillParsesAndKeepsItsCues()
    {
        // The whole point is a file that is still an ordinary subtitle. If stamping shifted a cue
        // or upset the parser, it would trade a bookkeeping problem for a playback one.
        var original = SubtitleDocument.Parse(Encoding.UTF8.GetBytes(Script));
        var stamped = SubtitleDocument.Parse(
            Encoding.UTF8.GetBytes(SubtitleProvenance.Stamp(Script, SubtitleFormatKind.Ass, Line())));

        Assert.Equal(SubtitleFormatKind.Ass, stamped.Kind);
        Assert.Equal(original.TimedLines.Count, stamped.TimedLines.Count);

        for (var i = 0; i < original.TimedLines.Count; i++)
        {
            Assert.Equal(original.TimedLines[i].StartSeconds, stamped.TimedLines[i].StartSeconds);
            Assert.Equal(original.TimedLines[i].EndSeconds, stamped.TimedLines[i].EndSeconds);
        }
    }

    [Fact]
    public void SubRipIsLeftAlone()
    {
        // SRT has no comment syntax, so anything inserted would be rendered on screen as dialogue.
        const string Srt = "1\n00:00:01,000 --> 00:00:03,000\nこんにちは\n";

        Assert.Equal(Srt, SubtitleProvenance.Stamp(Srt, SubtitleFormatKind.Srt, Line()));
    }

    [Fact]
    public void WindowsLineEndingsAreNotMixed()
    {
        var crlf = Script.Replace("\n", "\r\n", StringComparison.Ordinal);
        var stamped = SubtitleProvenance.Stamp(crlf, SubtitleFormatKind.Ass, Line());

        Assert.DoesNotContain(stamped.Replace("\r\n", string.Empty, StringComparison.Ordinal), "\n", StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnstampedFileReadsAsUnstamped()
    {
        Assert.Null(SubtitleProvenance.Read(Script));
        Assert.Null(SubtitleProvenance.Read(string.Empty));
    }

    [Fact]
    public void AScriptWithNoHeaderStillGetsStamped()
    {
        var stamped = SubtitleProvenance.Stamp("[Events]\nDialogue: 0,0:00:01.00,0:00:02.00,D,hi", SubtitleFormatKind.Ass, Line());

        Assert.StartsWith(SubtitleProvenance.Marker, stamped, StringComparison.Ordinal);
        Assert.NotNull(SubtitleProvenance.Read(stamped));
    }
}
