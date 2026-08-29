using System;
using System.Collections.Generic;
using System.Text;
using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Sync;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// The piecewise aligner's gate, which decided whether a differently-cut subtitle got a chance.
/// </summary>
/// <remarks>
/// The attempt used to be gated on the global correlation clearing half the accept floor. That is
/// backwards: a subtitle cut differently from the local release has no single offset that works, so
/// its global correlation is low by construction - which is precisely the case the piecewise
/// aligner was written to rescue. The guard belongs on the split's own result, and already was
/// there.
/// </remarks>
public class PiecewiseGateTests
{
    private static CueTrack Cues(params (double Start, double End)[] cues) =>
        new(Array.ConvertAll(cues, c => new Cue(c.Start, c.End)));

    /// <summary>
    /// Builds a dialogue-like sequence of cues: irregularly spaced, with varying durations.
    /// </summary>
    /// <remarks>
    /// Evenly spaced cues are the wrong fixture and quietly invalidate the test. A perfectly
    /// periodic signal correlates just as well at every multiple of its period, so uniqueness -
    /// the metric that catches a wrong episode - collapses and everything is declined regardless
    /// of what the aligner did. Real dialogue is irregular, and that irregularity is exactly what
    /// makes an offset identifiable.
    /// </remarks>
    private static List<(double Start, double Duration)> Dialogue(int count)
    {
        var random = new Random(20260829);
        var cues = new List<(double, double)>(count);
        var t = 10.0;

        for (var i = 0; i < count; i++)
        {
            t += 1.5 + (random.NextDouble() * 9.0);
            cues.Add((t, 1.0 + (random.NextDouble() * 2.5)));
        }

        return cues;
    }

    /// <summary>Builds a reference whose second half is shifted, as a differing cut produces.</summary>
    private static (ReferenceTrack Reference, SubtitleDocument Candidate) DifferingCut()
    {
        var dialogue = Dialogue(70);
        var referenceCues = new List<Cue>();
        var lines = new List<string>();

        for (var i = 0; i < dialogue.Count; i++)
        {
            var (start, duration) = dialogue[i];

            // The media carries a 12s insert halfway through, so the back half of the reference
            // sits later than the subtitle. No single offset explains both halves.
            var mediaStart = start + (i >= dialogue.Count / 2 ? 12.0 : 0.0);

            referenceCues.Add(new Cue(mediaStart, mediaStart + duration));
            lines.Add(Srt(i + 1, start, start + duration));
        }

        var track = new CueTrack(referenceCues);
        var signal = ActivitySignal.FromCues(track, track.LastEndSeconds + 60);

        return (new ReferenceTrack(signal, "test", track),
                SubtitleDocument.Parse(Encoding.UTF8.GetBytes(string.Join("\n", lines))));
    }

    private static string Srt(int index, double start, double end) =>
        $"{index}\n{Stamp(start)} --> {Stamp(end)}\nline {index}\n";

    private static string Stamp(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\,fff");

    [Fact]
    public void ADifferingCutIsMatchedRatherThanDeclined()
    {
        var (reference, candidate) = DifferingCut();
        var aligner = new SubtitleAligner(new PluginConfiguration());

        var result = aligner.Align(reference, candidate, allowPiecewise: true, expectDifferentCut: false);

        Assert.Equal(SyncVerdict.PiecewiseCut, result.Verdict);
        Assert.InRange(result.Blocks.Count, 2, 4);
    }

    [Fact]
    public void PiecewiseStillHasToBeAskedFor()
    {
        var (reference, candidate) = DifferingCut();
        var aligner = new SubtitleAligner(new PluginConfiguration());

        var result = aligner.Align(reference, candidate, allowPiecewise: false, expectDifferentCut: false);

        Assert.NotEqual(SyncVerdict.PiecewiseCut, result.Verdict);
    }

    [Fact]
    public void AWellAlignedSubtitleIsNotSplitSpuriously()
    {
        // The freedom that makes the splitter useful is also what makes it dangerous, so it must
        // not fire on a file a single offset already explains.
        var dialogue = Dialogue(70);
        var cues = new List<Cue>();
        var lines = new List<string>();

        for (var i = 0; i < dialogue.Count; i++)
        {
            var (start, duration) = dialogue[i];
            cues.Add(new Cue(start + 2.0, start + 2.0 + duration));
            lines.Add(Srt(i + 1, start, start + duration));
        }

        var track = new CueTrack(cues);
        var reference = new ReferenceTrack(
            ActivitySignal.FromCues(track, track.LastEndSeconds + 60), "test", track);
        var candidate = SubtitleDocument.Parse(Encoding.UTF8.GetBytes(string.Join("\n", lines)));

        var result = new SubtitleAligner(new PluginConfiguration())
            .Align(reference, candidate, allowPiecewise: true, expectDifferentCut: false);

        Assert.Equal(SyncVerdict.ConstantOffset, result.Verdict);
        Assert.Equal(2.0, result.Transform.OffsetSeconds, 1);
    }
}
