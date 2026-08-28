using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>
/// One row of the Kometa anime ID table, keyed by AniDB ID.
/// </summary>
public sealed class AnimeIdMapping
{
    /// <summary>Gets or sets the AniDB ID this row is keyed by.</summary>
    public int AniDbId { get; set; }

    /// <summary>Gets or sets the TVDB series ID.</summary>
    [JsonPropertyName("tvdb_id")]
    public int? TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the TVDB season this row covers. <c>-1</c> means the series is numbered
    /// absolutely rather than by season, and <c>0</c> means specials.
    /// </summary>
    [JsonPropertyName("tvdb_season")]
    public int? TvdbSeason { get; set; }

    /// <summary>
    /// Gets or sets how many episodes of the TVDB season precede this entry.
    /// </summary>
    /// <remarks>
    /// A single TVDB season is often split across several AniList entries, one per cour. The offset
    /// is what converts a TVDB episode number into the number the AniList entry uses, and skipping
    /// that conversion is a subtle way to request the wrong episode's subtitles.
    /// </remarks>
    [JsonPropertyName("tvdb_epoffset")]
    public int TvdbEpisodeOffset { get; set; }

    /// <summary>Gets or sets the MyAnimeList ID.</summary>
    [JsonPropertyName("mal_id")]
    public int? MalId { get; set; }

    /// <summary>Gets or sets the AniList ID, which is what Jimaku indexes by.</summary>
    [JsonPropertyName("anilist_id")]
    public int? AniListId { get; set; }
}

/// <summary>
/// Downloads and caches the Kometa anime ID table, which maps TVDB and AniDB IDs to AniList IDs.
/// </summary>
/// <remarks>
/// Jimaku indexes by AniList ID, but most Jellyfin anime libraries are scraped by TVDB or TMDB, so
/// some translation is unavoidable. The table is about 1.6 MB and changes slowly, so it is cached
/// on disk. The Emby plugin this replaces re-downloaded it on every single search.
/// </remarks>
public sealed class KometaMappingCache(HttpClient httpClient, ILogger<KometaMappingCache> logger) : IDisposable
{
    private const string SourceUrl = "https://raw.githubusercontent.com/Kometa-Team/Anime-IDs/master/anime_ids.json";

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _lock = new(1, 1);

    private Dictionary<int, List<AnimeIdMapping>>? _byTvdb;
    private Dictionary<int, AnimeIdMapping>? _byAniDb;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>Gets or sets the directory the cached copy is kept in.</summary>
    public string CacheDirectory { get; set; } = Path.GetTempPath();

    /// <summary>
    /// Looks up every mapping for a TVDB series ID.
    /// </summary>
    /// <param name="tvdbId">The TVDB series ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mappings, which may be empty.</returns>
    public async Task<IReadOnlyList<AnimeIdMapping>> GetByTvdbIdAsync(int tvdbId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _byTvdb is not null && _byTvdb.TryGetValue(tvdbId, out var rows)
            ? rows
            : Array.Empty<AnimeIdMapping>();
    }

    /// <summary>
    /// Looks up a mapping by AniDB ID.
    /// </summary>
    /// <param name="aniDbId">The AniDB ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The mapping, or null.</returns>
    public async Task<AnimeIdMapping?> GetByAniDbIdAsync(int aniDbId, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _byAniDb is not null && _byAniDb.TryGetValue(aniDbId, out var row) ? row : null;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_byTvdb is not null && DateTimeOffset.UtcNow - _loadedAt < MaxAge)
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_byTvdb is not null && DateTimeOffset.UtcNow - _loadedAt < MaxAge)
            {
                return;
            }

            var path = Path.Combine(CacheDirectory, "kometa-anime-ids.json");
            var json = await ReadCachedAsync(path, cancellationToken).ConfigureAwait(false);

            if (json is null)
            {
                json = await DownloadAsync(path, cancellationToken).ConfigureAwait(false);
            }

            if (json is null)
            {
                // Keep whatever is already loaded rather than losing the ability to map IDs
                // because GitHub happened to be unreachable.
                _loadedAt = DateTimeOffset.UtcNow;
                return;
            }

            Parse(json);
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<string?> ReadCachedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var age = DateTimeOffset.UtcNow - new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (age > MaxAge)
            {
                return null;
            }

            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not read the cached anime ID table.");
            return null;
        }
    }

    private async Task<string?> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var json = await httpClient.GetStringAsync(new Uri(SourceUrl), cancellationToken).ConfigureAwait(false);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                logger.LogDebug(ex, "Could not cache the anime ID table to disk.");
            }

            return json;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Downloading the Kometa anime ID table failed.");

            // A stale cache still maps IDs correctly for everything that already existed.
            try
            {
                return File.Exists(path)
                    ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private void Parse(string json)
    {
        var byTvdb = new Dictionary<int, List<AnimeIdMapping>>();
        var byAniDb = new Dictionary<int, AnimeIdMapping>();

        var root = JsonSerializer.Deserialize<Dictionary<string, AnimeIdMapping>>(json);
        if (root is null)
        {
            return;
        }

        foreach (var (key, mapping) in root)
        {
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aniDbId))
            {
                continue;
            }

            mapping.AniDbId = aniDbId;
            byAniDb[aniDbId] = mapping;

            if (mapping.TvdbId is { } tvdbId)
            {
                if (!byTvdb.TryGetValue(tvdbId, out var list))
                {
                    list = [];
                    byTvdb[tvdbId] = list;
                }

                list.Add(mapping);
            }
        }

        _byTvdb = byTvdb;
        _byAniDb = byAniDb;

        logger.LogDebug(
            "Loaded {Count} anime ID mappings covering {Series} TVDB series.",
            byAniDb.Count,
            byTvdb.Count);
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();
}
