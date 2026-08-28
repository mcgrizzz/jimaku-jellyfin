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
    /// Resolves the lookup for an episode.
    /// </summary>
    /// <param name="episode">The episode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The lookup.</returns>
    public async Task<AnimeLookup> ResolveAsync(Episode episode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);

        var series = episode.Series;
        var episodeNumber = episode.IndexNumber;
        var seasonNumber = episode.ParentIndexNumber;

        // Best case: a metadata plugin already recorded the AniList ID on the series.
        if (TryGetInt(series, "AniList", out var aniListId))
        {
            return new AnimeLookup(aniListId, null, null, episodeNumber, "AniList ID from series metadata");
        }

        if (TryGetInt(series, "AniDB", out var aniDbId))
        {
            var mapping = await mappings.GetByAniDbIdAsync(aniDbId, cancellationToken).ConfigureAwait(false);
            if (mapping?.AniListId is { } fromAniDb)
            {
                return new AnimeLookup(fromAniDb, null, null, episodeNumber, "AniList ID mapped from AniDB");
            }
        }

        if (TryGetInt(series, MetadataProvider.Tvdb.ToString(), out var tvdbId) && episodeNumber.HasValue)
        {
            var resolved = await ResolveFromTvdbAsync(tvdbId, seasonNumber, episodeNumber.Value, cancellationToken)
                .ConfigureAwait(false);

            if (resolved.HasValue)
            {
                return resolved.Value;
            }
        }

        if (series is not null &&
            series.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId) &&
            !string.IsNullOrWhiteSpace(tmdbId))
        {
            return new AnimeLookup(null, tmdbId, null, episodeNumber, "TMDB ID from series metadata");
        }

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

    private async Task<AnimeLookup?> ResolveFromTvdbAsync(
        int tvdbId,
        int? seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken)
    {
        var rows = await mappings.GetByTvdbIdAsync(tvdbId, cancellationToken).ConfigureAwait(false);
        var lookup = SelectFromTvdbMappings(rows, seasonNumber, episodeNumber);

        if (lookup.HasValue && lookup.Value.EpisodeNumber != episodeNumber)
        {
            logger.LogDebug(
                "TVDB {TvdbId} S{Season}E{Episode} maps to AniList {AniListId} episode {Adjusted}.",
                tvdbId,
                seasonNumber,
                episodeNumber,
                lookup.Value.AniListId,
                lookup.Value.EpisodeNumber);
        }

        return lookup;
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
        if (rows.Count == 0)
        {
            return null;
        }

        var absolute = rows.FirstOrDefault(r => r.TvdbSeason == -1 && r.AniListId.HasValue);
        if (absolute?.AniListId is { } absoluteId)
        {
            return new AnimeLookup(
                absoluteId,
                null,
                null,
                episodeNumber,
                "AniList ID mapped from TVDB (absolute numbering)");
        }

        var forSeason = rows
            .Where(r => r.AniListId.HasValue)
            .Where(r => !seasonNumber.HasValue || r.TvdbSeason == seasonNumber.Value)
            .Where(r => r.TvdbEpisodeOffset < episodeNumber)
            .OrderByDescending(r => r.TvdbEpisodeOffset)
            .FirstOrDefault();

        if (forSeason?.AniListId is not { } seasonId)
        {
            return null;
        }

        return new AnimeLookup(
            seasonId,
            null,
            null,
            episodeNumber - forSeason.TvdbEpisodeOffset,
            forSeason.TvdbEpisodeOffset == 0
                ? "AniList ID mapped from TVDB"
                : string.Create(CultureInfo.InvariantCulture, $"AniList ID mapped from TVDB (episode offset {forSeason.TvdbEpisodeOffset})"));
    }

    private static bool TryGetInt(Series? item, string key, out int value)
    {
        value = 0;
        return item is not null
               && item.ProviderIds.TryGetValue(key, out var raw)
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
