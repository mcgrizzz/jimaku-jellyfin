using System;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Timing;

public class SubtitleFormatTests
{
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData("sample.ass")]
    [InlineData("sample.srt")]
    public void Parse_ThenToText_IsByteExact(string fixture)
    {
        var original = ReadFixture(fixture);
        Assert.Equal(original, SubtitleDocument.Parse(original).ToText());
    }

    [Theory]
    [InlineData("sample.ass")]
    [InlineData("sample.srt")]
    public void ApplyIdentityTransform_ChangesNothing(string fixture)
    {
        var original = ReadFixture(fixture);
        var document = SubtitleDocument.Parse(original);
        Assert.Equal(original, SubtitleRewriter.Apply(document, TimingTransform.Identity).Text);
    }

    [Fact]
    public void Parse_Ass_ReadsEveryEventLineIncludingComments()
    {
        var document = SubtitleDocument.Parse(ReadFixture("sample.ass"));

        Assert.Equal(SubtitleFormatKind.Ass, document.Kind);
        Assert.Equal(9, document.TimedLines.Count);
        Assert.Single(document.TimedLines, t => t.IsComment);
        Assert.True(document.HasKaraoke);
    }

    [Fact]
    public void Parse_Ass_HandlesCommasInsideTheTextField()
    {
        var document = SubtitleDocument.Parse(ReadFixture("sample.ass"));
        var line = document.TimedLines[2];
        var raw = document.Lines[line.LineIndex];

        Assert.Equal(
            "It has a comma, and {\\i1}italics{\\i0} too.",
            raw.Substring(line.TextOffset, line.TextLength).TrimEnd('\r', '\n'));
        Assert.Equal(16.00, line.StartSeconds, 3);
        Assert.Equal(19.50, line.EndSeconds, 3);
    }

    [Fact]
    public void Shift_Ass_RewritesTimecodesAndLeavesEverythingElseAlone()
    {
        var original = ReadFixture("sample.ass");
        var document = SubtitleDocument.Parse(original);

        var result = SubtitleRewriter.Apply(document, new TimingTransform(1.0, 2.5));

        Assert.Equal(9, result.CuesRewritten);
        Assert.False(result.InlineTagsScaled);
        Assert.Contains("Dialogue: 0,0:00:14.84,0:00:18.17,Default,,0,0,0,,こんにちは、世界。", result.Text, StringComparison.Ordinal);

        // A pure shift must not touch karaoke: those durations are relative to the cue start.
        Assert.Contains("{\\k30}ka{\\k25}ze{\\k45}no{\\k20}u{\\k60}ta", result.Text, StringComparison.Ordinal);
        Assert.Contains("{\\move(100,200,800,200,0,1500)\\t(0,500,\\frz20)}", result.Text, StringComparison.Ordinal);

        // Styling, headers and project metadata pass through untouched.
        foreach (var section in new[] { "[Script Info]", "[Aegisub Project Garbage]", "[V4+ Styles]", "Style: Sign,", "YCbCr Matrix: TV.709" })
        {
            Assert.Contains(section, result.Text, StringComparison.Ordinal);
        }

        Assert.Equal(
            original.Split('\n').Length,
            result.Text.Split('\n').Length);
    }

    [Fact]
    public void Shift_Ass_RetimesCommentLinesToo()
    {
        var document = SubtitleDocument.Parse(ReadFixture("sample.ass"));
        var result = SubtitleRewriter.Apply(document, new TimingTransform(1.0, 2.5));

        Assert.Contains("Comment: 0,0:00:02.50,0:00:07.50,Default,,0,0,0,,translation notes go here, with a comma", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Shift_NegativeResult_IsClampedToZero()
    {
        var document = SubtitleDocument.Parse(ReadFixture("sample.ass"));
        var result = SubtitleRewriter.Apply(document, new TimingTransform(1.0, -20.0));

        Assert.True(result.ClampedToZero > 0);
        Assert.Contains("Dialogue: 0,0:00:00.00,0:00:00.00", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Scale_Ass_RescalesKaraokeAndAnimationTimings()
    {
        var document = SubtitleDocument.Parse(ReadFixture("sample.ass"));

        // 25/24 is a 4.1667% stretch, which is plainly visible in the karaoke values.
        var result = SubtitleRewriter.Apply(document, new TimingTransform(25.0 / 24.0, 0));

        Assert.True(result.InlineTagsScaled);
        Assert.Contains("{\\k31}ka{\\k26}ze{\\k47}no{\\k21}u{\\k63}ta", result.Text, StringComparison.Ordinal);
        Assert.Contains("\\fad(208,313)", result.Text, StringComparison.Ordinal);
        Assert.Contains("\\move(100,200,800,200,0,1563)", result.Text, StringComparison.Ordinal);
        Assert.Contains("\\t(0,521,\\frz20)", result.Text, StringComparison.Ordinal);

        // Spatial arguments must not move.
        Assert.Contains("\\pos(960,140)", result.Text, StringComparison.Ordinal);
        Assert.Contains("{\\p1}m 0 0 l 100 0 100 100 0 100{\\p0}", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ScaleCumulative_KeepsTheSumConsistentWithTheScaledTotal()
    {
        // Rounding each syllable on its own lets error accumulate until the karaoke no longer fills
        // the line. Scaling the running total and re-differencing cannot drift.
        int[] durations = [30, 25, 45, 20, 60, 17, 33, 11, 7, 23];
        const double Scale = 25.0 / 23.976;

        var scaled = AssTagScaler.ScaleCumulative(durations, Scale);

        var expectedTotal = (int)Math.Round(durations.Sum() * Scale, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedTotal, scaled.Sum());
        Assert.All(scaled, v => Assert.True(v > 0));
    }

    [Fact]
    public void Scale_Ass_DeclinePolicy_RefusesToTouchKaraokeFiles()
    {
        var document = SubtitleDocument.Parse(ReadFixture("sample.ass"));

        Assert.Throws<InvalidOperationException>(() =>
            SubtitleRewriter.Apply(document, new TimingTransform(25.0 / 24.0, 0), KaraokeScalePolicy.Decline));
    }

    [Fact]
    public void Shift_Srt_RewritesTimecodesAndKeepsTrailingCoordinates()
    {
        var document = SubtitleDocument.Parse(ReadFixture("sample.srt"));
        Assert.Equal(SubtitleFormatKind.Srt, document.Kind);
        Assert.Equal(4, document.TimedLines.Count);

        var result = SubtitleRewriter.Apply(document, new TimingTransform(1.0, 1.5));

        Assert.Contains("00:00:13,840 --> 00:00:17,170", result.Text, StringComparison.Ordinal);
        Assert.Contains("00:00:21,600 --> 00:00:26,400  X1:100 X2:800 Y1:900 Y2:1000", result.Text, StringComparison.Ordinal);
        Assert.Contains("01:00:01,490 --> 01:00:04,500", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToCueTrack_ExcludesCommentsAndMusicNoteOnlyLines()
    {
        var document = SubtitleDocument.Parse(ReadFixture("sample.ass"));
        var track = document.ToCueTrack();

        // Nine timed lines, minus one Comment and minus the bare music note.
        Assert.Equal(7, track.Count);
    }

    [Theory]
    [InlineData(0.0, "0:00:00.00")]
    [InlineData(1.0, "0:00:01.00")]
    [InlineData(61.239, "0:01:01.24")]
    [InlineData(3599.999, "1:00:00.00")]
    [InlineData(36000.5, "10:00:00.50")]
    [InlineData(-5.0, "0:00:00.00")]
    public void FormatTime_Ass_MatchesTheConventionalLayout(double seconds, string expected)
    {
        // Hours are not zero-padded and centiseconds carry into seconds, matching what Aegisub and
        // Subtitle Edit emit, so corrected files still round-trip through ordinary tooling.
        Assert.Equal(expected, SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, seconds));
    }

    [Theory]
    [InlineData(0.0, "00:00:00,000")]
    [InlineData(61.2394, "00:01:01,239")]
    [InlineData(3599.9999, "01:00:00,000")]
    public void FormatTime_Srt_MatchesTheConventionalLayout(double seconds, string expected)
    {
        Assert.Equal(expected, SubtitleRewriter.FormatTime(SubtitleFormatKind.Srt, seconds));
    }
}

public class EncodingDetectorTests
{
    private const string Japanese = "こんにちは、世界。ダイアログ行です。";

    [Fact]
    public void Decode_Utf8WithBom_StripsTheBom()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Japanese)).ToArray();
        Assert.Equal(Japanese, EncodingDetector.Decode(bytes, out var name));
        Assert.Equal("utf-8-bom", name);
    }

    [Fact]
    public void Decode_Utf8WithoutBom_IsDetected()
    {
        Assert.Equal(Japanese, EncodingDetector.Decode(Encoding.UTF8.GetBytes(Japanese), out var name));
        Assert.Equal("utf-8", name);
    }

    [Fact]
    public void Decode_ShiftJis_IsDetected()
    {
        // Jimaku hosts plenty of pre-UTF-8 files; getting this wrong yields mojibake that still
        // renders, so it is easy to ship without noticing.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(932).GetBytes(Japanese);

        Assert.Equal(Japanese, EncodingDetector.Decode(bytes, out var name));
        Assert.Equal("shift-jis", name);
    }

    [Fact]
    public void Decode_Utf16Le_IsDetected()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Japanese)).ToArray();
        Assert.Equal(Japanese, EncodingDetector.Decode(bytes, out var name));
        Assert.Equal("utf-16le", name);
    }

    [Fact]
    public void Parse_ShiftJisBytes_RoundTripsThroughTheDocument()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.ass"));
        var bytes = Encoding.GetEncoding(932).GetBytes(text);

        var document = SubtitleDocument.Parse(bytes);

        Assert.Equal("shift-jis", document.SourceEncoding);
        Assert.Equal(text, document.ToText());
    }
}
