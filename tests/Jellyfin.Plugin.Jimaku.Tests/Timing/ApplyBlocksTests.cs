using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

/// <summary>
/// Piecewise offsets are addressed by cue index against the filtered, time-sorted cue track, which
/// is not the same as raw line order. These tests pin that correspondence down.
/// </summary>
public class ApplyBlocksTests
{
    private static string Ass(params string[] events) =>
        "[Script Info]\nScriptType: v4.00+\n\n[Events]\n" +
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
        string.Concat(events.Select(e => e + "\n"));

    private static string Dialogue(double start, double end, string text = "line", string kind = "Dialogue") =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{kind}: 0,{SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, start)},{SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, end)},Default,,0,0,0,,{text}");

    private static IReadOnlyList<double> StartsOf(string ass) =>
        SubtitleDocument.Parse(ass).TimedLines.Select(t => t.StartSeconds).ToList();

    [Fact]
    public void ApplyBlocks_AssignsEachBlockToItsOwnCues()
    {
        var ass = Ass(
            Dialogue(10, 12),
            Dialogue(20, 22),
            Dialogue(30, 32),
            Dialogue(40, 42));

        var result = SubtitleRewriter.ApplyBlocks(
            SubtitleDocument.Parse(ass),
            [new SplitBlock(0, 1, 1.0), new SplitBlock(2, 3, 5.0)]);

        Assert.Equal([11, 21, 35, 45], StartsOf(result.Text));
    }

    [Fact]
    public void ApplyBlocks_WithEventsOutOfChronologicalOrder_StillMatchesCuesToBlocks()
    {
        // ASS does not require events to be stored in time order, and Aegisub happily saves them
        // unsorted. Block indices refer to the time-sorted track, so a naive walk of the file in
        // line order would give these lines each other's offsets.
        var ass = Ass(
            Dialogue(40, 42, "fourth"),
            Dialogue(10, 12, "first"),
            Dialogue(30, 32, "third"),
            Dialogue(20, 22, "second"));

        var result = SubtitleRewriter.ApplyBlocks(
            SubtitleDocument.Parse(ass),
            [new SplitBlock(0, 1, 1.0), new SplitBlock(2, 3, 5.0)]);

        // Line order is preserved; the offsets follow the cue's position in time, not in the file.
        Assert.Equal([45, 11, 35, 21], StartsOf(result.Text));
    }

    [Fact]
    public void ApplyBlocks_SkipsCommentsAndSignsWhenCountingCuesButStillRetimesThem()
    {
        // The aligner never sees comments or music-note-only lines, so they do not consume a cue
        // index. They must still be retimed, or they drift away from the dialogue around them.
        var ass = Ass(
            Dialogue(10, 12, "one"),
            Dialogue(15, 16, "notes", "Comment"),
            Dialogue(20, 22, "two"),
            Dialogue(25, 26, "♪"),
            Dialogue(30, 32, "three"),
            Dialogue(40, 42, "four"));

        var document = SubtitleDocument.Parse(ass);

        // Four real cues survive the filter, so blocks address 0..3.
        Assert.Equal(4, document.ToCueTrack().Count);
        Assert.Equal(6, document.TimedLines.Count);

        var result = SubtitleRewriter.ApplyBlocks(
            document,
            [new SplitBlock(0, 1, 1.0), new SplitBlock(2, 3, 5.0)]);

        Assert.Equal(
            [11, 16, 21, 26, 35, 45],
            StartsOf(result.Text));
    }

    [Fact]
    public void Project_MapsEachCueBackToItsLine()
    {
        var ass = Ass(
            Dialogue(40, 42, "fourth"),
            Dialogue(15, 16, "notes", "Comment"),
            Dialogue(10, 12, "first"),
            Dialogue(20, 22, "second"));

        var projection = SubtitleDocument.Parse(ass).Project();

        Assert.Equal(3, projection.Track.Count);
        Assert.Equal([10.0, 20.0, 40.0], projection.Track.Cues.Select(c => c.StartSeconds));

        // Cue 0 is the third timed line, cue 1 the fourth, cue 2 the first.
        Assert.Equal([2, 3, 0], projection.TimedLineIndices);
    }

    [Fact]
    public void ApplyBlocks_NoBlocks_LeavesTheFileUntouched()
    {
        var ass = Ass(Dialogue(10, 12));
        var result = SubtitleRewriter.ApplyBlocks(SubtitleDocument.Parse(ass), []);
        Assert.Equal(ass, result.Text);
    }
}
