using System.Collections.Generic;
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
        TvdbEpisodeOffset = offset,
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
