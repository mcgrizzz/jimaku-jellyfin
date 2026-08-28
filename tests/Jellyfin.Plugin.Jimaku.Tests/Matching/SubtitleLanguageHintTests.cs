using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// Chinese fansub groups publish a bilingual file and a Japanese-only file side by side. The
/// bilingual one styles Chinese as the prominent line, which is the wrong result here, so the
/// Japanese-only sibling has to win.
/// </summary>
public class SubtitleLanguageHintTests
{
    [Theory]
    // The real pair that caused this: bilingual won on an alphabetical tie-break.
    [InlineData("[Nekomoe kissaten&VCB-Studio] Mushoku Tensei ~Isekai Ittara Honki Dasu~ [01][Ma10p_1080p][x265_flac][CHS, JPN].ass", SubtitleLanguages.Multilingual)]
    [InlineData("[Nekomoe kissaten&VCB-Studio] Mushoku Tensei ~Isekai Ittara Honki Dasu~ [01][Ma10p_1080p][x265_flac][JPN].ass", SubtitleLanguages.JapaneseOnly)]
    [InlineData("[SubsPlease] Mushoku Tensei - 01v2 (1080p) [F76E4E71].ja.srt", SubtitleLanguages.JapaneseOnly)]
    [InlineData("無職転生.S01E01.WEBRip.Amazon.ja-jp[sdh].srt", SubtitleLanguages.JapaneseOnly)]
    [InlineData("Show - 01 [CHT&JPN].ass", SubtitleLanguages.Multilingual)]
    [InlineData("Show - 01 [中日双语].ass", SubtitleLanguages.Multilingual)]
    [InlineData("Show - 01 [简日].ass", SubtitleLanguages.Multilingual)]
    [InlineData("Show - 01 [CHS].ass", SubtitleLanguages.NoJapanese)]
    [InlineData("Show - 01 [繁体].ass", SubtitleLanguages.NoJapanese)]
    [InlineData("[AnimeOut] Show - 01 BD Remux [CE93C369][OZR].srt", SubtitleLanguages.Unknown)]
    public void Classify(string fileName, SubtitleLanguages expected)
    {
        Assert.Equal(expected, SubtitleLanguageHint.Classify(fileName));
    }

    [Theory]
    // Language codes must match as whole tokens. Substring matching finds "sc" in "x265_flac"
    // and "ja" in a title, and would misclassify half the library.
    [InlineData("[Group] Show ~Isekai Ittara Honki Dasu~ [01][Ma10p_1080p][x265_flac].ass")]
    [InlineData("[Group] Jashin-chan Dropkick - 01.ass")]
    [InlineData("[Group] Show - 01 [Hi10p][BD][1080p].ass")]
    public void Classify_DoesNotFindLanguagesInsideOtherWords(string fileName)
    {
        Assert.Equal(SubtitleLanguages.Unknown, SubtitleLanguageHint.Classify(fileName));
    }
}

/// <summary>
/// Language preference exists to demote bilingual releases, not to demote files that simply do not
/// advertise a language. Ranking "unknown" below "Japanese only" let a subtitle covering more of
/// the dialogue lose to a sparser one before coverage was even compared.
/// </summary>
public class LanguagePreferenceScopeTests
{
    [Theory]
    [InlineData(SubtitleLanguages.JapaneseOnly, SubtitleLanguages.Unknown, true)]
    [InlineData(SubtitleLanguages.Unknown, SubtitleLanguages.JapaneseOnly, true)]
    [InlineData(SubtitleLanguages.JapaneseOnly, SubtitleLanguages.Multilingual, false)]
    [InlineData(SubtitleLanguages.Unknown, SubtitleLanguages.Multilingual, false)]
    public void RankedLevel(SubtitleLanguages left, SubtitleLanguages right, bool expectedEqual)
    {
        // Mirrors JimakuSyncService.LanguageRank.
        static int Rank(SubtitleLanguages l) => l switch
        {
            SubtitleLanguages.JapaneseOnly => 0,
            SubtitleLanguages.Unknown => 0,
            SubtitleLanguages.Multilingual => 1,
            _ => 2,
        };

        Assert.Equal(expectedEqual, Rank(left) == Rank(right));
    }

    [Fact]
    public void TheRealPair_IsDecidedOnQualityNotOnLabelling()
    {
        // AnimeOut carries no language tag; the Nekomoe file says [JPN]. They must reach the
        // quality comparison level, or the better subtitle loses on a naming convention.
        var animeOut = SubtitleLanguageHint.Classify(
            "[AnimeOut] Mushoku Tensei Jobless Reincarnation - 01 BD Remux 720p FLAC AAC [Dual-Audio] [CE93C369][OZR][RapidBot].srt");
        var nekomoe = SubtitleLanguageHint.Classify(
            "[Nekomoe kissaten&VCB-Studio] Mushoku Tensei ~Isekai Ittara Honki Dasu~ [01][Ma10p_1080p][x265_flac][JPN].ass");

        Assert.Equal(SubtitleLanguages.Unknown, animeOut);
        Assert.Equal(SubtitleLanguages.JapaneseOnly, nekomoe);

        static int Rank(SubtitleLanguages l) => l switch
        {
            SubtitleLanguages.JapaneseOnly => 0,
            SubtitleLanguages.Unknown => 0,
            SubtitleLanguages.Multilingual => 1,
            _ => 2,
        };

        Assert.Equal(Rank(nekomoe), Rank(animeOut));
    }
}
