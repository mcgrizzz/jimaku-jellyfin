using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Jimaku;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Jimaku;

/// <summary>
/// Records requests and replays canned responses. Responses are built per call, because the client
/// disposes each one after reading it.
/// </summary>
internal sealed class StubHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _index;

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var factory = responses[Math.Min(_index, responses.Length - 1)];
        _index++;
        return Task.FromResult(factory());
    }

    public static Func<HttpResponseMessage> Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

    public static Func<HttpResponseMessage> Bytes(byte[] body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
}

public class JimakuApiClientTests
{
    private const string EntriesJson = """
    [
      {
        "id": 42,
        "name": "Sousou no Frieren",
        "japanese_name": "葬送のフリーレン",
        "english_name": "Frieren: Beyond Journey's End",
        "anilist_id": 154587,
        "tmdb_id": "tv:209867",
        "last_modified": "2024-03-22T10:11:12Z",
        "flags": { "adult": false, "anime": true, "external": false, "movie": false, "unverified": false }
      }
    ]
    """;

    private const string FilesJson = """
    [
      {
        "url": "https://jimaku.cc/entry/42/download/ep12.ass",
        "name": "[SubsPlease] Sousou no Frieren - 12.ass",
        "size": 48213,
        "last_modified": "2024-03-22T10:11:12Z"
      }
    ]
    """;

    private static JimakuApiClient Create(StubHandler handler) =>
        new(new HttpClient(handler), NullLogger<JimakuApiClient>.Instance);

    [Fact]
    public async Task Search_SendsTheRawKeyWithNoBearerPrefix()
    {
        // Jimaku compares the Authorization header verbatim against stored keys, so a "Bearer "
        // prefix produces a 401. This is the single easiest way to get this API wrong.
        var handler = new StubHandler(StubHandler.Json(EntriesJson));

        await Create(handler).SearchByAniListIdAsync(154587, "secret-key", CancellationToken.None);

        var header = handler.Requests[0].Headers.GetValues("Authorization").Single();
        Assert.Equal("secret-key", header);
        Assert.DoesNotContain("Bearer", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_ParsesTheBareJsonArray()
    {
        // The API returns a naked array, with no envelope and no pagination.
        var entries = await Create(new StubHandler(StubHandler.Json(EntriesJson)))
            .SearchByAniListIdAsync(154587, "k", CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(42, entry.Id);
        Assert.Equal("Sousou no Frieren", entry.Name);
        Assert.Equal("葬送のフリーレン", entry.JapaneseName);
        Assert.Equal(154587, entry.AniListId);
        Assert.Equal("tv:209867", entry.TmdbId);
        Assert.True(entry.Flags.Anime);
        Assert.False(entry.Flags.Unverified);
    }

    [Fact]
    public async Task Search_BuildsTheExpectedQueryStrings()
    {
        var handler = new StubHandler(StubHandler.Json("[]"));
        var client = Create(handler);

        await client.SearchByAniListIdAsync(154587, "k", CancellationToken.None);
        await client.SearchByTmdbIdAsync("209867", false, "k", CancellationToken.None);
        await client.SearchByNameAsync("Frieren 2", false, "k", CancellationToken.None);
        await client.GetFilesAsync(42, 12, "k", CancellationToken.None);

        var urls = handler.Requests.Select(r => r.RequestUri!.AbsoluteUri).ToList();

        Assert.Equal("https://jimaku.cc/api/entries/search?anilist_id=154587", urls[0]);
        Assert.Equal("https://jimaku.cc/api/entries/search?tmdb_id=tv%3A209867", urls[1]);
        Assert.Equal("https://jimaku.cc/api/entries/search?query=Frieren%202&anime=false", urls[2]);
        Assert.Equal("https://jimaku.cc/api/entries/42/files?episode=12", urls[3]);
    }

    [Fact]
    public async Task GetFiles_ParsesTheFileEntries()
    {
        var files = await Create(new StubHandler(StubHandler.Json(FilesJson)))
            .GetFilesAsync(42, 12, "k", CancellationToken.None);

        var file = Assert.Single(files);
        Assert.Equal("[SubsPlease] Sousou no Frieren - 12.ass", file.Name);
        Assert.Equal(48213, file.Size);
        Assert.StartsWith("https://jimaku.cc/entry/", file.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_Unauthorized_ReportsAnAuthenticationFailure()
    {
        var handler = new StubHandler(
            StubHandler.Json("""{"error":"unauthorized","code":7}""", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<JimakuApiException>(() =>
            Create(handler).SearchByAniListIdAsync(1, "bad", CancellationToken.None));

        Assert.True(ex.IsAuthenticationFailure);
        Assert.Equal(7, ex.ErrorCode);
        Assert.Contains("unauthorized", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_RateLimited_ReportsRateLimiting()
    {
        Func<HttpResponseMessage> response = () =>
        {
            var message = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":"rate limited","code":8}"""),
            };

            message.Headers.TryAddWithoutValidation("x-ratelimit-reset-after", "3.5");
            return message;
        };

        var ex = await Assert.ThrowsAsync<JimakuApiException>(() =>
            Create(new StubHandler(response)).SearchByAniListIdAsync(1, "k", CancellationToken.None));

        Assert.True(ex.IsRateLimited);
    }

    [Fact]
    public async Task ValidateApiKey_ReturnsFalseOnRejectionRatherThanThrowing()
    {
        var handler = new StubHandler(
            StubHandler.Json("""{"error":"unauthorized","code":7}""", HttpStatusCode.Unauthorized));

        Assert.False(await Create(handler).ValidateApiKeyAsync("bad", CancellationToken.None));
    }

    [Fact]
    public async Task Download_DoesNotRequireAnApiKey()
    {
        // The download route has no auth extractor server-side and sits outside the rate limiter.
        var handler = new StubHandler(StubHandler.Bytes([1, 2, 3, 4]));

        var bytes = await Create(handler)
            .DownloadAsync("https://jimaku.cc/entry/42/download/ep12.ass", CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], bytes);
        Assert.False(handler.Requests[0].Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Search_WithNoApiKey_FailsBeforeSendingAnything()
    {
        var handler = new StubHandler(StubHandler.Json("[]"));

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            Create(handler).SearchByAniListIdAsync(1, string.Empty, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }
}

public class RateLimiterTests
{
    [Fact]
    public async Task Acquire_AllowsUpToTheBudgetWithoutDelay()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var limiter = new RateLimiter(25, TimeSpan.FromSeconds(60), time);

        for (var i = 0; i < 25; i++)
        {
            await limiter.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Acquire_HoldsTheCallerOnceTheBudgetIsSpent()
    {
        // Jimaku's limit is per-IP across the whole API, so a library sweep must throttle itself
        // rather than discover the limit by being refused.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var limiter = new RateLimiter(2, TimeSpan.FromSeconds(60), time);

        await limiter.AcquireAsync(CancellationToken.None);
        await limiter.AcquireAsync(CancellationToken.None);

        var third = limiter.AcquireAsync(CancellationToken.None);
        Assert.False(third.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(61));
        await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ApplyServerBackoff_HoldsEveryCallerUntilItLifts()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var limiter = new RateLimiter(25, TimeSpan.FromSeconds(60), time);

        limiter.ApplyServerBackoff(TimeSpan.FromSeconds(30));

        var pending = limiter.AcquireAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(31));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
