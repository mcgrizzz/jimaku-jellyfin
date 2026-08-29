using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Sync;
using Jellyfin.Plugin.Jimaku.Tests.Timing;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// Runs a real ASS file through the whole timing path: break it in a known way, let the plugin
/// detect and undo the damage, then check both that the timings came back and that nothing else in
/// the file moved.
/// </summary>
public class EndToEndTimingTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1500;

    /// <summary>Builds a realistic ASS file: styled header, karaoke, signs, comments, and dialogue.</summary>
    private static string BuildAss(CueTrack track)
    {
        var builder = new StringBuilder();
        builder.Append(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.ass")));

        // Everything above the fixture's own events stays; append many more dialogue lines so the
        // aligner has a realistic amount of material.
        var i = 0;
        foreach (var cue in track.Cues)
        {
            var start = SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, cue.StartSeconds);
            var end = SubtitleRewriter.FormatTime(SubtitleFormatKind.Ass, cue.EndSeconds);
            var style = i % 7 == 0 ? "Sign" : "Default";
            var text = i % 11 == 0
                ? "{\\pos(960,140)\\fad(150,150)}標識テキスト"
                : "セリフ行 " + i.ToString(CultureInfo.InvariantCulture);

            builder.Append(CultureInfo.InvariantCulture, $"Dialogue: 0,{start},{end},{style},,0,0,0,,{text}\n");
            i++;
        }

        return builder.ToString();
    }

    /// <summary>Applies a linear time map to an ASS file, standing in for a mistimed release.</summary>
    private static string Distort(string ass, double scale, double offset)
    {
        var document = SubtitleDocument.Parse(ass);
        return SubtitleRewriter.Apply(document, new TimingTransform(scale, offset)).Text;
    }

    private static IReadOnlyList<string> NonTimingLines(string ass) =>
        ass.Split('\n')
           .Select(line => System.Text.RegularExpressions.Regex.Replace(
               line,
               @"(?<=^(Dialogue|Comment): [^,]*,)[^,]+,[^,]+",
               "<TIME>"))
           .ToList();

    [Theory]
    [InlineData(1.0, 2.5)]
    [InlineData(1.0, -7.25)]
    [InlineData(25.0 / 24.0, 0.0)]
    [InlineData(25.0 / 23.976, 3.0)]
    [InlineData(1001.0 / 1000.0, -1.5)]
    public void FullPipeline_RecoversTheDistortionAndPreservesEverythingElse(double scale, double offset)
    {
        var truth = SyntheticTrack.Episode(seed: 5, cueCount: 300);
        var original = BuildAss(truth);
        var distorted = Distort(original, scale, offset);

        var referenceCues = SubtitleDocument.Parse(original).ToCueTrack();
        var reference = new ReferenceTrack(
            ActivitySignal.FromCues(referenceCues, EpisodeSeconds),
            "embedded subtitles",
            referenceCues);

        var alignment = new SubtitleAligner(new PluginConfiguration()).Align(
            reference,
            SubtitleDocument.Parse(distorted),
            allowPiecewise: false,
            expectDifferentCut: false);

        output.WriteLine($"{alignment.Verdict}: {alignment.Transform.Describe()} r={alignment.Correlation:0.000} ratio={alignment.PeakRatio:0.00}");

        Assert.True(alignment.IsAcceptable, alignment.Reason);

        // The correction must invert the distortion: t -> (t * scale + offset) undone.
        Assert.Equal(1.0 / scale, alignment.Transform.Scale, 4);

        var corrected = SubtitleRewriter.Apply(
            SubtitleDocument.Parse(distorted),
            alignment.Transform).Text;

        // Timings come back within a bin.
        var expected = SubtitleDocument.Parse(original).ToCueTrack();
        var actual = SubtitleDocument.Parse(corrected).ToCueTrack();
        Assert.Equal(expected.Count, actual.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.InRange(actual.Cues[i].StartSeconds - expected.Cues[i].StartSeconds, -0.05, 0.05);
        }

        // Nothing outside the timing fields changed: same line count, and every line identical once
        // the two timecodes are masked out.
        var before = NonTimingLines(original);
        var after = NonTimingLines(corrected);
        Assert.Equal(before.Count, after.Count);

        if (Math.Abs(scale - 1.0) < 1e-9)
        {
            // A pure shift must leave inline tags completely untouched, karaoke included.
            Assert.Equal(before, after);
        }
        else
        {
            // A rate change legitimately rewrites time-valued inline tags, so compare only the
            // lines that carry none.
            for (var i = 0; i < before.Count; i++)
            {
                if (!before[i].Contains('\\', StringComparison.Ordinal))
                {
                    Assert.Equal(before[i], after[i]);
                }
            }

            Assert.Contains("[Aegisub Project Garbage]", corrected, StringComparison.Ordinal);
            Assert.Contains("Style: OP-Romaji,Comic Sans MS,64", corrected, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FullPipeline_ShiftOnly_LeavesTheFileByteIdenticalApartFromTimecodes()
    {
        var truth = SyntheticTrack.Episode(seed: 9, cueCount: 200);
        var original = BuildAss(truth);
        var distorted = Distort(original, 1.0, 6.0);

        var referenceCues = SubtitleDocument.Parse(original).ToCueTrack();
        var reference = new ReferenceTrack(
            ActivitySignal.FromCues(referenceCues, EpisodeSeconds),
            "embedded subtitles",
            referenceCues);

        var alignment = new SubtitleAligner(new PluginConfiguration()).Align(
            reference, SubtitleDocument.Parse(distorted), false, false);

        Assert.Equal(SyncVerdict.ConstantOffset, alignment.Verdict);

        var corrected = SubtitleRewriter.Apply(SubtitleDocument.Parse(distorted), alignment.Transform).Text;

        Assert.Equal(original.Length, corrected.Length);
        Assert.Equal(original, corrected);
    }
}
