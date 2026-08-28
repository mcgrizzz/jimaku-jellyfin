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
