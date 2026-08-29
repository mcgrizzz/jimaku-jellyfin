using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Media;
using Jellyfin.Plugin.Jimaku.Sync;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// What stops the differing-cut aligner fitting sections to noise.
/// </summary>
/// <remarks>
/// The splitter gets one free offset per section, so it raises correlation almost by construction.
/// Refereeing it on correlation alone therefore fails in the one direction that matters: a subtitle
/// whose global fit was completely non-unique - a flat surface, the signature of the wrong file -
/// came back as a confident two-section match and was written. Coverage is the check correlation
/// cannot provide, because extra freedom does not put dialogue in the right place.
/// </remarks>
public class PiecewiseCredibilityTests(ITestOutputHelper output)
{
    private const double EpisodeSeconds = 1420;

    private static string Srt(int index, double start, double end) =>
        $"{index}\n{Stamp(start)} --> {Stamp(end)}\nline {index}\n";

    private static string Stamp(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\,fff");

    private static SubtitleDocument Document(IEnumerable<Cue> cues) =>
        SubtitleDocument.Parse(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            cues.Select((c, i) => Srt(i + 1, c.StartSeconds, c.EndSeconds)))));

    private static ReferenceTrack Reference(CueTrack cues) =>
        new(ActivitySignal.FromCues(cues, EpisodeSeconds), "test", cues);

    private static List<Cue> Dialogue(int seed, int count)
    {
        var random = new Random(seed);
        var cues = new List<Cue>(count);
        var t = 20.0;

        for (var i = 0; i < count; i++)
        {
            t += 2.0 + (random.NextDouble() * 8.0);
            cues.Add(new Cue(t, t + 1.0 + (random.NextDouble() * 2.0)));
        }

        return cues;
    }

    [Fact]
    public void AnUnrelatedSubtitleIsNotRescuedBySplittingIt()
    {
        // Two independently generated dialogue patterns: nothing in common but their statistics.
        // This is the case that reached the user - a flat correlation surface, then a confident
        // "matched a different cut, 2 sections" that was worse than doing nothing.
        var reference = new CueTrack(Dialogue(1, 130));
        var candidate = Document(Dialogue(9999, 130));

        var result = new SubtitleAligner(new PluginConfiguration())
            .Align(Reference(reference), candidate, allowPiecewise: true, expectDifferentCut: false);

        output.WriteLine($"{result.Verdict}: {result.Reason}");

        Assert.NotEqual(SyncVerdict.PiecewiseCut, result.Verdict);
        Assert.False(result.IsAcceptable);
    }

    [Fact]
    public void AGenuineDifferingCutStillPasses()
    {
        // The guard has to keep letting the real thing through, or it has just re-broken what it
        // was added to protect.
        var dialogue = Dialogue(7, 130);
        var reference = new List<Cue>();

        for (var i = 0; i < dialogue.Count; i++)
        {
            var shift = i >= dialogue.Count / 2 ? 9.5 : 0.0;
            reference.Add(new Cue(dialogue[i].StartSeconds + shift, dialogue[i].EndSeconds + shift));
        }

        var result = new SubtitleAligner(new PluginConfiguration())
            .Align(Reference(new CueTrack(reference)), Document(dialogue), allowPiecewise: true, expectDifferentCut: false);

        output.WriteLine($"{result.Verdict}: {result.Reason}");

        Assert.Equal(SyncVerdict.PiecewiseCut, result.Verdict);
        Assert.True(result.Coverage > 0.9, $"covered {result.Coverage:P0}");
    }

    [Fact]
    public void PiecewiseIsNotAttemptedAgainstVoiceActivity()
    {
        // Maximum freedom on the weakest evidence. Voice activity carries no cue structure, so
        // there is nothing to check the result against - and it will always find something.
        var dialogue = Dialogue(3, 130);
        var track = new CueTrack(dialogue);
        var vad = new ReferenceTrack(ActivitySignal.FromCues(track, EpisodeSeconds), "voice activity");

        var result = new SubtitleAligner(new PluginConfiguration())
            .Align(vad, Document(Dialogue(4242, 130)), allowPiecewise: true, expectDifferentCut: false);

        Assert.NotEqual(SyncVerdict.PiecewiseCut, result.Verdict);
    }

    [Fact]
    public void TheDeclineSaysTheSectionsWereFittedToNoise()
    {
        var reference = new CueTrack(Dialogue(11, 130));
        var candidate = Document(Dialogue(5150, 130));

        var configuration = new PluginConfiguration { MinPiecewiseCoverage = 0.99 };
        var result = new SubtitleAligner(configuration)
            .Align(Reference(reference), candidate, allowPiecewise: true, expectDifferentCut: false);

        output.WriteLine(result.Reason);

        Assert.Equal(SyncVerdict.Declined, result.Verdict);
    }
}
