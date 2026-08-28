using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// Covers the TVDB to AniList conversion, where getting the episode number wrong fetches subtitles
/// for the wrong episode while appearing to succeed.
/// </summary>
public class AnimeIdMappingTests
{
    private static AnimeIdMapping Row(int? season, int offset, int aniListId, int tvdbId = 100) => new()
    {
        TvdbId = tvdbId,
        TvdbSeason = season,
        TvdbEpisodeOffsetRaw = offset,
        AniListId = aniListId,
    };

    [Fact]
    public void Select_SimpleSeason_MapsStraightThrough()
    {
        var lookup = AnimeIdResolver.SelectFromTvdbMappings([Row(1, 0, 154587)], 1, 12);

        Assert.NotNull(lookup);
        Assert.Equal(154587, lookup!.Value.AniListId);
        Assert.Equal(12, lookup.Value.EpisodeNumber);
    }

    [Fact]
    public void Select_SeasonSplitAcrossTwoCours_SubtractsTheEpisodeOffset()
    {
        // TVDB numbers a two-cour season 1-24, but AniList lists it as two entries each starting at
        // episode 1. Without subtracting the offset, episode 15 would request episode 15 of the
        // second cour, which is a different episode entirely.
        List<AnimeIdMapping> rows = [Row(1, 0, 1000), Row(1, 12, 2000)];

        var first = AnimeIdResolver.SelectFromTvdbMappings(rows, 1, 5);
        Assert.Equal(1000, first!.Value.AniListId);
        Assert.Equal(5, first.Value.EpisodeNumber);

        var second = AnimeIdResolver.SelectFromTvdbMappings(rows, 1, 15);
        Assert.Equal(2000, second!.Value.AniListId);
        Assert.Equal(3, second.Value.EpisodeNumber);
    }

    [Fact]
    public void Select_AbsoluteNumbering_KeepsTheEpisodeNumberAsIs()
    {
        // A season of -1 marks a series numbered absolutely, as long-running shows are.
        var lookup = AnimeIdResolver.SelectFromTvdbMappings([Row(-1, 0, 21)], 1, 1050);

        Assert.Equal(21, lookup!.Value.AniListId);
        Assert.Equal(1050, lookup.Value.EpisodeNumber);
        Assert.Contains("absolute", lookup.Value.Description, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Select_PrefersTheCorrectSeason()
    {
        List<AnimeIdMapping> rows = [Row(1, 0, 1000), Row(2, 0, 2000), Row(3, 0, 3000)];

        Assert.Equal(2000, AnimeIdResolver.SelectFromTvdbMappings(rows, 2, 4)!.Value.AniListId);
    }

    [Fact]
    public void Select_NoRowForTheSeason_ReturnsNothing()
    {
        Assert.Null(AnimeIdResolver.SelectFromTvdbMappings([Row(1, 0, 1000)], 4, 1));
    }

    [Fact]
    public void Select_EmptyTable_ReturnsNothing()
    {
        Assert.Null(AnimeIdResolver.SelectFromTvdbMappings([], 1, 1));
    }
}

/// <summary>
/// The Kometa anime ID table is community-maintained and not schema validated. A strict
/// deserializer once threw on a single oddly typed field and lost all 16,000 mappings, surfacing
/// to the user as a failed request.
/// </summary>
public class KometaMappingParsingTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new();

    private static System.Collections.Generic.Dictionary<string, AnimeIdMapping>? Parse(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, AnimeIdMapping>>(json, Options);

    [Fact]
    public void Parse_EntryWithCommaSeparatedMalId_DoesNotThrow()
    {
        // Verbatim from the live table, entry 6367 - the one that broke the plugin in 1.0.2.0.
        const string Json = """
        {
          "1":    {"tvdb_id": 72025, "tvdb_season": 1, "tvdb_epoffset": 0, "mal_id": 290, "anilist_id": 290},
          "6367": {"tvdb_id": 79414, "tvdb_season": 1, "tvdb_epoffset": 0, "anilist_id": 4382, "mal_id": "849,4382"}
        }
        """;

        var parsed = Parse(Json);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        Assert.Equal(290, parsed["1"].AniListId);
        Assert.Equal(4382, parsed["6367"].AniListId);
        Assert.Equal(79414, parsed["6367"].TvdbId);
    }

    [Theory]
    [InlineData("""{"a":{"anilist_id": 123}}""", 123)]
    [InlineData("""{"a":{"anilist_id": "123"}}""", 123)]
    [InlineData("""{"a":{"anilist_id": "123,456"}}""", 123)]
    [InlineData("""{"a":{"anilist_id": null}}""", null)]
    [InlineData("""{"a":{"anilist_id": ""}}""", null)]
    [InlineData("""{"a":{"anilist_id": "not a number"}}""", null)]
    [InlineData("""{"a":{}}""", null)]
    public void Parse_ToleratesEveryShapeTheFieldHasTakenOrCouldTake(string json, int? expected)
    {
        Assert.Equal(expected, Parse(json)!["a"].AniListId);
    }

    [Fact]
    public void Parse_MissingEpisodeOffset_DefaultsToZero()
    {
        Assert.Equal(0, Parse("""{"a":{"anilist_id": 1}}""")!["a"].TvdbEpisodeOffset);
    }

    [Fact]
    public void Parse_UnexpectedFieldTypes_AreIgnoredRatherThanFatal()
    {
        // An object or array where a number was expected must not take the whole table down.
        var parsed = Parse("""{"a":{"anilist_id": {"nested": 1}, "tvdb_id": [1,2]}}""");

        Assert.NotNull(parsed);
        Assert.Null(parsed!["a"].AniListId);
        Assert.Null(parsed["a"].TvdbId);
    }
}

/// <summary>
/// Parses the live Kometa table when a local copy is available. Gated on an environment variable
/// so CI without network still passes, but it is the only check that covers the whole real file
/// rather than the handful of shapes I thought to write down.
/// </summary>
[Trait("Category", "Live")]
public class KometaLiveTableTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Parse_TheEntireLiveTable_Succeeds()
    {
        var path = Environment.GetEnvironmentVariable("KOMETA_ANIME_IDS");
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            output.WriteLine("Set KOMETA_ANIME_IDS to a copy of anime_ids.json to run this.");
            return;
        }

        var parsed = System.Text.Json.JsonSerializer
            .Deserialize<System.Collections.Generic.Dictionary<string, AnimeIdMapping>>(
                System.IO.File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions());

        Assert.NotNull(parsed);
        var withAniList = parsed!.Values.Count(v => v.AniListId.HasValue);
        var withTvdb = parsed.Values.Count(v => v.TvdbId.HasValue);

        output.WriteLine($"parsed {parsed.Count} entries; {withAniList} with an AniList ID, {withTvdb} with a TVDB ID");

        Assert.True(parsed.Count > 10000, $"only {parsed.Count} entries parsed");
        Assert.True(withAniList > 10000, $"only {withAniList} AniList IDs");
    }
}
