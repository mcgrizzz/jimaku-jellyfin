using System;
using System.IO;
using Jellyfin.Plugin.Jimaku.Matching;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// Language identification from content rather than filename, tested on excerpts taken from real
/// media: a Bilibili release whose "Chinese" track turned out to hold Chinese and Japanese
/// together, plus its English track.
/// </summary>
public class SubtitleScriptAnalyzerTests(ITestOutputHelper output)
{
    private static SubtitleDocument Load(string name) =>
        SubtitleDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "scripts", name)));

    [Theory]
    [InlineData("japanese.ass", SubtitleLanguages.JapaneseOnly)]
    [InlineData("chinese.ass", SubtitleLanguages.NoJapanese)]
    [InlineData("bilingual.ass", SubtitleLanguages.Multilingual)]
    [InlineData("english.ass", SubtitleLanguages.NoJapanese)]
    public void Classify_RealExcerpts(string fixture, SubtitleLanguages expected)
    {
        var document = Load(fixture);
        var profile = SubtitleScriptAnalyzer.Profile(document);

        output.WriteLine($"{fixture,-16} {SubtitleScriptAnalyzer.Describe(profile)}  ({profile.Letters} letters)");

        Assert.Equal(expected, SubtitleScriptAnalyzer.Classify(document));
    }

    [Fact]
    public void Profile_JapaneseProse_IsKanaDominant()
    {
        // Kana settle what Han cannot: both languages use Han, only Japanese uses kana.
        var profile = SubtitleScriptAnalyzer.Profile(Load("japanese.ass"));
        Assert.True(profile.Kana > 0.6, $"kana share was {profile.Kana:P1}");
    }

    [Fact]
    public void Profile_ChineseProse_HasNoKana()
    {
        var profile = SubtitleScriptAnalyzer.Profile(Load("chinese.ass"));
        Assert.True(profile.Kana < 0.02, $"kana share was {profile.Kana:P1}");
        Assert.True(profile.Han > 0.9);
    }

    [Fact]
    public void Profile_BilingualFile_LandsBetweenTheTwo()
    {
        // Roughly even kana and Han is the signature of two languages in one file, and it is what
        // a filename claiming a single language will not tell you.
        var profile = SubtitleScriptAnalyzer.Profile(Load("bilingual.ass"));

        output.WriteLine(SubtitleScriptAnalyzer.Describe(profile));

        Assert.InRange(profile.Kana, 0.20, SubtitleScriptAnalyzer.BilingualKanaCeiling);
        Assert.True(profile.Han > 0.30);
    }

    [Fact]
    public void Classify_ContentBeatsAMisleadingFilename()
    {
        // The case that caused a real misranking: a correct Japanese subtitle whose filename
        // carries no language tag at all.
        Assert.Equal(
            SubtitleLanguages.Unknown,
            SubtitleLanguageHint.Classify("[AnimeOut] Show - 01 BD Remux [CE93C369][OZR].srt"));

        Assert.Equal(SubtitleLanguages.JapaneseOnly, SubtitleScriptAnalyzer.Classify(Load("japanese.ass")));
    }

    [Fact]
    public void Classify_TooLittleText_DefersRatherThanGuessing()
    {
        var document = SubtitleDocument.Parse(
            "[Script Info]\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
            "Dialogue: 0,0:00:01.00,0:00:03.00,Default,,0,0,0,,はい\n");

        Assert.Equal(SubtitleLanguages.Unknown, SubtitleScriptAnalyzer.Classify(document));
    }
}
