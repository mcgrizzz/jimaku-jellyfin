using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>
/// How to look an episode up on Jimaku.
/// </summary>
/// <param name="AniListId">The AniList ID, when one could be determined.</param>
/// <param name="TmdbId">The numeric TMDB ID, as a fallback route.</param>
/// <param name="Query">A name to search by, as a last resort.</param>
/// <param name="EpisodeNumber">
/// The episode number to request, already converted into the numbering the AniList entry uses.
/// </param>
/// <param name="Description">How the lookup was derived, for logging and the UI.</param>
public readonly record struct AnimeLookup(
    int? AniListId,
    string? TmdbId,
    string? Query,
    int? EpisodeNumber,
    string Description)
{
    /// <summary>Gets a value indicating whether there is anything to search with.</summary>
    public bool IsUsable => AniListId.HasValue || TmdbId is not null || !string.IsNullOrWhiteSpace(Query);
}

/// <summary>
/// Works out which Jimaku entry an episode corresponds to.
/// </summary>
/// <remarks>
/// Jimaku indexes by AniList ID, but Jellyfin's subtitle plumbing does not carry one: provider IDs
/// live on the <c>Series</c>, not the <c>Episode</c>, and most anime libraries are scraped by TVDB
/// or TMDB in any case. So this walks from whatever the library happens to know towards an AniList
/// ID, and falls back to a name search when it cannot get there.
/// </remarks>
public sealed class AnimeIdResolver(KometaMappingCache mappings, ILogger<AnimeIdResolver> logger)
{
    /// <summary>
    /// How many ways of finding one episode are worth trying. A split cour needs two; more than a
    /// handful means the identifiers disagree so badly that spending requests on all of them is
    /// worse than reporting the failure.
    /// </summary>
    private const int MaxLookups = 4;

    /// <summary>
    /// Resolves the lookup for an episode.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lookup.</returns>
    public async Task<AnimeLookup> ResolveAsync(Episode episode, CancellationToken cancellationToken)
    {
        var all = await ResolveAllAsync(episode, cancellationToken).ConfigureAwait(false);
        return all.Count > 0 ? all[0] : new AnimeLookup(null, null, null, null, "no usable identifier");
    }

    /// <summary>
    /// Resolves every way this episode might be found on Jimaku, most likely first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// More than one, because a single season is regularly two AniList entries. A split cour airs
    /// as two runs of twelve, AniList gives each its own entry numbered from one, and TVDB - which
    /// is what Jellyfin shows - numbers the season straight through to twenty-four. Episode
    /// nineteen therefore lives in the second entry as its episode seven, and asking the first
    /// entry for episode nineteen finds nothing at all.
    /// </para>
    /// <para>
    /// An AniList ID recorded on the series cannot express this: it names one entry, so it is right
    /// for the first cour and wrong for every episode after it. Returning the alternatives and
    /// trying each is cheap - Jimaku filters by episode server-side, so the entry that does not
    /// contain it simply returns nothing - and it removes the need to be right first time.
    /// </para>
    /// </remarks>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lookups to try, in order.</returns>
    public async Task<IReadOnlyList<AnimeLookup>> ResolveAllAsync(
        Episode episode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var series = episode.Series;
        var episodeNumber = episode.IndexNumber;
        var seasonNumber = episode.ParentIndexNumber;
        var lookups = new List<AnimeLookup>();

        // The mapping table leads, because it is the only source that knows a season can be two
        // entries. A series-level AniList ID is kept as an alternative rather than a first choice:
        // it is exact for a single-cour show and silently wrong past the first cour of a split one.
        if (TryGetInt(series, MetadataProvider.Tvdb.ToString(), out var tvdbId) && episodeNumber.HasValue)
        {
            var rows = await mappings.GetByTvdbIdAsync(tvdbId, cancellationToken).ConfigureAwait(false);

            foreach (var candidate in SelectAllFromTvdbMappings(rows, seasonNumber, episodeNumber.Value))
            {
                Add(candidate);
            }
        }

        if (TryGetInt(series, "AniList", out var aniListId))
        {
            Add(new AnimeLookup(aniListId, null, null, episodeNumber, "AniList ID from series metadata"));
        }

        if (TryGetInt(series, "AniDB", out var aniDbId))
        {
            var mapping = await mappings.GetByAniDbIdAsync(aniDbId, cancellationToken).ConfigureAwait(false);
            if (mapping?.AniListId is { } fromAniDb)
            {
                Add(new AnimeLookup(fromAniDb, null, null, episodeNumber, "AniList ID mapped from AniDB"));
            }
        }

        if (lookups.Count > 0)
        {
            if (lookups.Count > 1)
            {
                logger.LogDebug(
                    "{Name} S{Season}E{Episode} could be any of {Count} entries: {Lookups}.",
                    episode.SeriesName,
                    seasonNumber,
                    episodeNumber,
                    lookups.Count,
                    string.Join(", ", lookups.Select(l => string.Create(
                        CultureInfo.InvariantCulture,
                        $"AniList {l.AniListId} episode {l.EpisodeNumber}"))));
            }

            return lookups;
        }

        if (series is not null &&
            series.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId) &&
            !string.IsNullOrWhiteSpace(tmdbId))
        {
            return [new AnimeLookup(null, tmdbId, null, episodeNumber, "TMDB ID from series metadata")];
        }

        return [NameLookup(series, episode, seasonNumber, episodeNumber)];

        void Add(AnimeLookup lookup)
        {
            if (lookups.Count < MaxLookups
                && !lookups.Any(l => l.AniListId == lookup.AniListId && l.EpisodeNumber == lookup.EpisodeNumber))
            {
                lookups.Add(lookup);
            }
        }
    }

    private static AnimeLookup NameLookup(Series? series, Episode episode, int? seasonNumber, int? episodeNumber)
    {
        var name = series?.Name ?? episode.SeriesName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            // Jimaku appends the season number to entry names for later seasons, so including it
            // meaningfully improves the fuzzy match.
            var query = seasonNumber is > 1
                ? string.Create(CultureInfo.InvariantCulture, $"{name} {seasonNumber}")
                : name;

            return new AnimeLookup(null, null, query, episodeNumber, "name search (no usable provider ID)");
        }

        return new AnimeLookup(null, null, null, episodeNumber, "no usable identifier");
    }

    /// <summary>
    /// Chooses the mapping row that covers a given TVDB season and episode, and converts the
    /// episode number into the numbering the matching AniList entry uses.
    /// </summary>
    /// <remarks>
    /// Two conventions have to be handled. A season of <c>-1</c> means the series is numbered
    /// absolutely, as long-running shows are. Otherwise a single TVDB season is frequently split
    /// across several AniList entries, one per cour, and <c>tvdb_epoffset</c> records how many
    /// episodes precede each one; subtracting it is what turns a TVDB episode number into that
    /// entry's own. Skipping that subtraction fetches subtitles for the wrong episode while looking
    /// entirely successful, which is why it is pulled out here and tested directly.
    /// </remarks>
    /// <param name="rows">Candidate mappings for the TVDB series.</param>
    /// <param name="seasonNumber">The TVDB season number, if known.</param>
    /// <param name="episodeNumber">The TVDB episode number.</param>
    /// <returns>The lookup, or null when no row applies.</returns>
    internal static AnimeLookup? SelectFromTvdbMappings(
        IReadOnlyList<AnimeIdMapping> rows,
        int? seasonNumber,
        int episodeNumber)
    {
        var all = SelectAllFromTvdbMappings(rows, seasonNumber, episodeNumber);
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// Lists every mapping row that could account for a TVDB season and episode, best first.
    /// </summary>
    /// <remarks>
    /// The row whose offset the episode falls past is the intended one and comes first. The others
    /// follow because the boundary between two cours is not always where the mapping says: a recap
    /// episode counted by one source and not the other moves it by one, and an episode on the wrong
    /// side of it is invisible if only a single row is ever tried. Asking a second entry costs one
    /// request and cannot produce a wrong answer, since an entry that does not hold the episode
    /// returns nothing.
    /// </remarks>
    /// <param name="rows">Candidate mappings for the TVDB series.</param>
    /// <param name="seasonNumber">The TVDB season number, if known.</param>
    /// <param name="episodeNumber">The TVDB episode number.</param>
    /// <returns>The lookups, most likely first.</returns>
    internal static IReadOnlyList<AnimeLookup> SelectAllFromTvdbMappings(
        IReadOnlyList<AnimeIdMapping> rows,
        int? seasonNumber,
        int episodeNumber)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            return [];
        }

        var absolute = rows.FirstOrDefault(r => r.TvdbSeason == -1 && r.AniListId.HasValue);
        if (absolute?.AniListId is { } absoluteId)
        {
            return
            [
                new AnimeLookup(
                    absoluteId,
                    null,
                    null,
                    episodeNumber,
                    "AniList ID mapped from TVDB (absolute numbering)")
            ];
        }

        return rows
            .Where(r => r.AniListId.HasValue)
            .Where(r => !seasonNumber.HasValue || r.TvdbSeason == seasonNumber.Value)

            // An offset at or past the episode would map it to zero or below, which is no episode.
            .Where(r => r.TvdbEpisodeOffset < episodeNumber)
            .OrderByDescending(r => r.TvdbEpisodeOffset)
            .Select(r => new AnimeLookup(
                r.AniListId!.Value,
                null,
                null,
                episodeNumber - r.TvdbEpisodeOffset,
                r.TvdbEpisodeOffset == 0
                    ? "AniList ID mapped from TVDB"
                    : string.Create(CultureInfo.InvariantCulture, $"AniList ID mapped from TVDB (episode offset {r.TvdbEpisodeOffset})")))
            .ToList();
    }

    private static bool TryGetInt(Series? item, string key, out int value)
    {
        value = 0;
        return item is not null
               && item.ProviderIds.TryGetValue(key, out var raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
