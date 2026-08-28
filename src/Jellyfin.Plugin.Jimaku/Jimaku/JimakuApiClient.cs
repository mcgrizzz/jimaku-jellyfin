using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Jimaku.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Jimaku;

/// <summary>
/// Client for the jimaku.cc API.
/// </summary>
/// <remarks>
/// Two details of this API are easy to get wrong and both fail in confusing ways. The
/// <c>Authorization</c> header carries the raw API key with no <c>Bearer</c> prefix, because the
/// server looks the header value up verbatim. And every endpoint returns a bare JSON array with no
/// envelope and no pagination.
/// </remarks>
public sealed class JimakuApiClient : IDisposable
{
    /// <summary>The API base address.</summary>
    public const string BaseUrl = "https://jimaku.cc/api";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<JimakuApiClient> _logger;
    private readonly RateLimiter _rateLimiter;

    /// <summary>Initializes a new instance of the <see cref="JimakuApiClient"/> class.</summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="rateLimiter">Shared rate limiter, or null to create a private one.</param>
    public JimakuApiClient(HttpClient httpClient, ILogger<JimakuApiClient> logger, RateLimiter? rateLimiter = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _rateLimiter = rateLimiter ?? new RateLimiter();
    }

    /// <summary>
    /// Searches entries by AniList ID.
    /// </summary>
    /// <param name="aniListId">The AniList ID.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching entries, best first.</returns>
    public async Task<IReadOnlyList<JimakuEntry>> SearchByAniListIdAsync(
        int aniListId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (aniListId < 0)
        {
            return Array.Empty<JimakuEntry>();
        }

        var id = aniListId.ToString(CultureInfo.InvariantCulture);
        return await SearchBothFlagsAsync($"/entries/search?anilist_id={id}", apiKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Searches entries by TMDB ID.
    /// </summary>
    /// <param name="tmdbId">The TMDB ID, numeric portion only.</param>
    /// <param name="isMovie">Whether the item is a movie rather than a series.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching entries, best first.</returns>
    public Task<IReadOnlyList<JimakuEntry>> SearchByTmdbIdAsync(
        string tmdbId,
        bool isMovie,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tmdbId);

        var prefix = isMovie ? "movie" : "tv";
        return SearchBothFlagsAsync(
            $"/entries/search?tmdb_id={Uri.EscapeDataString(prefix + ":" + tmdbId)}",
            apiKey,
            cancellationToken);
    }

    /// <summary>
    /// Searches entries by name.
    /// </summary>
    /// <param name="query">The search text.</param>
    /// <param name="anime">
    /// Whether to restrict the search to anime. Defaults to true server-side, so live action must
    /// explicitly opt out or nothing will ever match.
    /// </param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching entries, best first.</returns>
    public Task<IReadOnlyList<JimakuEntry>> SearchByNameAsync(
        string query,
        bool anime,
        string apiKey,
        CancellationToken cancellationToken) =>
        SearchAsync(
            $"/entries/search?query={Uri.EscapeDataString(query)}&anime={(anime ? "true" : "false")}",
            apiKey,
            cancellationToken);

    /// <summary>
    /// Lists the files attached to an entry.
    /// </summary>
    /// <param name="entryId">The entry ID.</param>
    /// <param name="episode">
    /// Episode number to filter by, or null for everything. Jimaku parses filenames with anitomy to
    /// apply this, and consults an anime-relations table keyed by the entry's AniList ID, which is
    /// how absolute versus per-season numbering gets reconciled without the caller doing anything.
    /// Note that files whose episode number cannot be parsed are dropped entirely when this is set.
    /// </param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The files, ordered by name.</returns>
    public async Task<IReadOnlyList<JimakuFile>> GetFilesAsync(
        long entryId,
        int? episode,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var path = $"/entries/{entryId.ToString(CultureInfo.InvariantCulture)}/files";
        if (episode is >= 0)
        {
            path += $"?episode={episode.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return await GetAsync<JimakuFile>(path, apiKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks that an API key is accepted, for the "test key" button on the settings page.
    /// </summary>
    /// <param name="apiKey">The API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the key is valid.</returns>
    public async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            // Any authenticated endpoint will do; a search for a well-known ID is the cheapest.
            await SearchByAniListIdAsync(1, apiKey, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (JimakuApiException ex) when (ex.IsAuthenticationFailure)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads a subtitle file.
    /// </summary>
    /// <param name="url">The absolute download URL from a <see cref="JimakuFile"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file bytes.</returns>
    public async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // Downloads sit outside the API rate limiter server-side, so they do not consume a permit.
        using var response = await _httpClient
            .GetAsync(new Uri(url), cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new JimakuApiException(
                $"Downloading '{url}' failed with {(int)response.StatusCode} {response.ReasonPhrase}.",
                response.StatusCode,
                0);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<JimakuEntry>> SearchAsync(
        string path,
        string apiKey,
        CancellationToken cancellationToken) =>
        await GetAsync<JimakuEntry>(path, apiKey, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs a search against anime entries and, if nothing matches, against non-anime ones.
    /// </summary>
    /// <remarks>
    /// The <c>anime</c> parameter defaults to <see langword="true"/> server-side and is applied
    /// before the ID match, so an entry that is not flagged as anime cannot be found even by an
    /// exact AniList or TMDB ID. Live-action Japanese drama is the obvious casualty. The second
    /// request only happens when the first returns nothing, so the common path still costs one
    /// call against a 25-per-minute budget.
    /// </remarks>
    private async Task<IReadOnlyList<JimakuEntry>> SearchBothFlagsAsync(
        string path,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var entries = await GetAsync<JimakuEntry>($"{path}&anime=true", apiKey, cancellationToken)
            .ConfigureAwait(false);

        if (entries.Count > 0)
        {
            return entries;
        }

        _logger.LogDebug("No anime entries for {Path}; retrying as non-anime.", path);
        return await GetAsync<JimakuEntry>($"{path}&anime=false", apiKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> GetAsync<T>(
        string path,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        await _rateLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUrl + path));

        // The raw key, deliberately not "Bearer {key}": the server compares the header value
        // verbatim against stored keys, so a scheme prefix produces a 401.
        request.Headers.TryAddWithoutValidation("Authorization", apiKey);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = ReadRetryAfter(response);
            _logger.LogWarning("Jimaku rate limited the request; holding off for {Seconds:0.0}s.", retryAfter.TotalSeconds);
            _rateLimiter.ApplyServerBackoff(retryAfter);
            throw new JimakuApiException("Jimaku rate limit exceeded.", response.StatusCode, 8);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            throw new JimakuApiException(
                $"Jimaku returned {(int)response.StatusCode}: {error?.Error ?? response.ReasonPhrase}",
                response.StatusCode,
                error?.Code ?? 0);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var result = await JsonSerializer
            .DeserializeAsync<List<T>>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return result ?? (IReadOnlyList<T>)Array.Empty<T>();
    }

    private static TimeSpan ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-reset-after", out var values))
        {
            foreach (var value in values)
            {
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 300));
                }
            }
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;
        return retryAfter ?? TimeSpan.FromSeconds(60);
    }

    private static async Task<JimakuError?> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<JimakuError>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _rateLimiter.Dispose();
}
