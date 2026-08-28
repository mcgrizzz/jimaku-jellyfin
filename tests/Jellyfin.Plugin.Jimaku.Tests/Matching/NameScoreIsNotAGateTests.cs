using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// Records why the filename score orders candidates but must never exclude them.
/// </summary>
/// <remarks>
/// Real case from a user's library. The local file is a Bilibili WebRip; Jimaku offered seven
/// subtitles for the episode. The two that scored highest (35) were Netflix and Amazon rips, and
/// both failed timing verification. The one the user confirmed actually matches the audio scored
/// 10, because it is a Blu-ray remux from a different group and shares almost no naming with the
/// local file. A minimum-score gate of 20 discarded it before it was ever downloaded.
/// </remarks>
public class NameScoreIsNotAGateTests(ITestOutputHelper output)
{
    private const string LocalFile =
        "[Feibanyama] Mushoku Tensei Jobless Reincarnation S01E01 [BILIBILI WebRip 2160p HEVC OPUS Multi-Subs].mkv";

    public static TheoryData<string> JimakuCandidates =>
    [
        "無職転生.～異世界行ったら本気だす～.S01E01.無職転生.WEBRip.Netflix.ja[cc].srt",
        "無職転生.～異世界行ったら本気だす～.S01E01.第1話.無職転生.WEBRip.Amazon.ja-jp[sdh].srt",
        "[AnimeOut] Mushoku Tensei Jobless Reincarnation - 01 BD Remux 720p FLAC AAC [Dual-Audio] [CE93C369][OZR][RapidBot].srt",
        "[Nekomoe kissaten&VCB-Studio] Mushoku Tensei ~Isekai Ittara Honki Dasu~ [01][Ma10p_1080p][x265_flac][JPN].ass",
        "[Netflix] Mushoku Tensei Jobless Reincarnation 01 (non SDH).srt",
        "[SubsPlease] Mushoku Tensei - 01v2 (1080p) [F76E4E71].ja.srt",
    ];

    [Theory]
    [MemberData(nameof(JimakuCandidates))]
    public void EveryRealCandidate_IsUsableRegardlessOfItsScore(string candidate)
    {
        var match = ReleaseMatcher.Compare(LocalFile, candidate, 1);

        output.WriteLine($"{match.Score,3}  {match.Notes}   {candidate}");

        // None of these name the wrong episode, so none may be rejected outright. Whether they are
        // correct is a question for the timing check, not the filename.
        Assert.False(match.EpisodeMismatch);
    }

    [Fact]
    public void TheCorrectCandidate_ScoresBelowTheOldGate()
    {
        // The subtitle the user confirmed matches, against the two that scored highest and failed.
        var correct = ReleaseMatcher.Compare(
            LocalFile,
            "[AnimeOut] Mushoku Tensei Jobless Reincarnation - 01 BD Remux 720p FLAC AAC [Dual-Audio] [CE93C369][OZR][RapidBot].srt",
            1);

        var highestScoring = ReleaseMatcher.Compare(
            LocalFile,
            "無職転生.～異世界行ったら本気だす～.S01E01.無職転生.WEBRip.Netflix.ja[cc].srt",
            1);

        output.WriteLine($"correct candidate scores {correct.Score}; best-named scores {highestScoring.Score}");

        // This is the whole point: the filename ranks the right answer below the wrong one, so any
        // threshold that excludes on score can discard the correct subtitle.
        Assert.True(
            correct.Score < highestScoring.Score,
            "the premise of this test no longer holds; revisit whether scoring changed");

        Assert.True(correct.Score < 20, $"correct candidate scored {correct.Score}, above the old gate of 20");
    }

    [Fact]
    public void SourceMismatch_IsFlaggedRatherThanUsedToReject()
    {
        // A Blu-ray subtitle on a web video is a hint to expect a different cut, not grounds to
        // discard the file.
        var match = ReleaseMatcher.Compare(
            LocalFile,
            "[AnimeOut] Mushoku Tensei Jobless Reincarnation - 01 BD Remux 720p FLAC AAC [Dual-Audio] [CE93C369][OZR][RapidBot].srt",
            1);

        Assert.True(match.SourceMismatch);
        Assert.False(match.EpisodeMismatch);
    }
}
