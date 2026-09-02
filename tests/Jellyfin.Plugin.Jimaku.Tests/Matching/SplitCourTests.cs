using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

/// <summary>
/// One season in the library, two entries on Jimaku.
/// </summary>
/// <remarks>
/// A split cour airs as two runs, AniList gives each its own entry numbered from one, and TVDB -
/// which is what Jellyfin shows - numbers the season straight through. So a season of twenty-four
/// episodes is two entries of twelve, and episode nineteen lives in the second as its episode
/// seven. Asking the first entry for episode nineteen finds nothing, and nothing is what the user
/// saw.
///
/// The rows below are Kometa's actual mappings for Mushoku Tensei, TVDB 371310.
/// </remarks>
public class SplitCourTests
{
    private static readonly List<AnimeIdMapping> MushokuTensei =
    [
        new() { TvdbId = 371310, TvdbSeason = 1, TvdbEpisodeOffsetRaw = 0, AniListId = 108465 },
        new() { TvdbId = 371310, TvdbSeason = 1, TvdbEpisodeOffsetRaw = 11, AniListId = 127720 },
        new() { TvdbId = 371310, TvdbSeason = 2, TvdbEpisodeOffsetRaw = 0, AniListId = 146065 },
        new() { TvdbId = 371310, TvdbSeason = 2, TvdbEpisodeOffsetRaw = 12, AniListId = 166873 },
        new() { TvdbId = 371310, TvdbSeason = 3, TvdbEpisodeOffsetRaw = 0, AniListId = 178789 },
    ];

    [Theory]
    [InlineData(1, 1, 108465, 1)]
    [InlineData(1, 11, 108465, 11)]   // last episode of the first cour
    [InlineData(1, 12, 127720, 1)]    // first of the second, numbered from one again
    [InlineData(1, 23, 127720, 12)]
    [InlineData(2, 12, 146065, 12)]
    [InlineData(2, 13, 166873, 1)]
    [InlineData(2, 19, 166873, 7)]
    [InlineData(3, 4, 178789, 4)]
    public void TheEpisodeIsFoundInTheCourThatHoldsIt(
        int season,
        int episode,
        int expectedAniList,
        int expectedEpisode)
    {
        var best = AnimeIdResolver.SelectAllFromTvdbMappings(MushokuTensei, season, episode).First();

        Assert.Equal(expectedAniList, best.AniListId);
        Assert.Equal(expectedEpisode, best.EpisodeNumber);
    }

    [Fact]
    public void TheOtherCourIsOfferedAsAFallback()
    {
        // The boundary between two cours is not always where the mapping says - a recap counted by
        // one source and not the other moves it - so the second entry is tried when the first
        // returns nothing rather than the episode being declared missing.
        var all = AnimeIdResolver.SelectAllFromTvdbMappings(MushokuTensei, 2, 19);

        Assert.Equal(2, all.Count);
        Assert.Equal(166873, all[0].AniListId);
        Assert.Equal(146065, all[1].AniListId);
        Assert.Equal(19, all[1].EpisodeNumber);
    }

    [Fact]
    public void AnEpisodeBeforeEveryOffsetOnlyMatchesTheFirstCour()
    {
        var all = AnimeIdResolver.SelectAllFromTvdbMappings(MushokuTensei, 2, 3);

        Assert.Single(all);
        Assert.Equal(146065, all[0].AniListId);
    }

    [Fact]
    public void SeasonsDoNotBleedIntoEachOther()
    {
        var all = AnimeIdResolver.SelectAllFromTvdbMappings(MushokuTensei, 3, 4);

        Assert.Single(all);
        Assert.Equal(178789, all[0].AniListId);
    }

    [Fact]
    public void AbsoluteNumberingStillShortCircuits()
    {
        // A long-running show numbered straight through has one entry and no offsets to apply.
        List<AnimeIdMapping> absolute =
        [
            new() { TvdbId = 1, TvdbSeason = -1, TvdbEpisodeOffsetRaw = 0, AniListId = 999 },
            new() { TvdbId = 1, TvdbSeason = 1, TvdbEpisodeOffsetRaw = 0, AniListId = 111 },
        ];

        var all = AnimeIdResolver.SelectAllFromTvdbMappings(absolute, 1, 500);

        Assert.Single(all);
        Assert.Equal(999, all[0].AniListId);
        Assert.Equal(500, all[0].EpisodeNumber);
    }
}
