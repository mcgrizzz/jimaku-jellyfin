using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Produces the best timing reference available for a media item, and remembers it.
/// </summary>
/// <remarks>
/// Building a reference is by far the most expensive step: every embedded text track is extracted
/// and parsed, then compared against every other to find the one they agree on. On a release
/// carrying eleven subtitle tracks that is dozens of cross-correlations, and it was being repeated
/// in full for each operation on the same episode - once to list candidates and again to apply one,
/// which is what made the subtitle dialog sit for several seconds before responding. The reference
/// depends only on the media file, so it is cached until that file changes.
/// </remarks>
public sealed class ReferenceTrackResolver(
    EmbeddedSubtitleReferenceProvider embedded,
    AudioActivityReferenceProvider audio,
    ILogger<ReferenceTrackResolver> logger)
{
    /// <summary>How many episodes to remember references for.</summary>
    private const int MaxEntries = 16;

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    private sealed record CacheEntry(
        string Path,
        long Stamp,
        ReferenceTrack? Track,
        ReferenceReport Report,
        DateTimeOffset CachedAt);

    /// <summary>
    /// Resolves a reference, preferring an embedded subtitle track and falling back to audio.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="allowAudioFallback">Whether audio analysis may be used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reference, or null when none could be built.</returns>
    public async Task<ReferenceTrack?> ResolveAsync(
        BaseItem item,
        bool allowAudioFallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var stamp = MediaStamp(item);

        if (_cache.TryGetValue(item.Id, out var cached) &&
            string.Equals(cached.Path, item.Path, StringComparison.Ordinal) &&
            cached.Stamp == stamp)
        {
            logger.LogDebug("Reusing the cached timing reference for {Path}.", item.Path);
            return cached.Track;
        }

        var report = new ReferenceReport();
        var reference = await BuildAsync(item, allowAudioFallback, report, cancellationToken).ConfigureAwait(false);

        // A failure is worth caching too: without that, an episode with no usable reference pays
        // the full cost again on every candidate listing.
        Store(item.Id, new CacheEntry(item.Path ?? string.Empty, stamp, reference, report, DateTimeOffset.UtcNow));
        return reference;
    }

    /// <summary>
    /// Builds - or reuses - the account of how an item's reference was arrived at.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ResolveAsync"/> so the question "what did it compare against, and
    /// why that" can be asked directly rather than inferred from a decline message.
    /// </remarks>
    /// <param name="item">The media item.</param>
    /// <param name="allowAudioFallback">Whether audio analysis may be used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account.</returns>
    public async Task<ReferenceReport> ExplainAsync(
        BaseItem item,
        bool allowAudioFallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var stamp = MediaStamp(item);
        if (_cache.TryGetValue(item.Id, out var cached) &&
            string.Equals(cached.Path, item.Path, StringComparison.Ordinal) &&
            cached.Stamp == stamp)
        {
            return cached.Report;
        }

        await ResolveAsync(item, allowAudioFallback, cancellationToken).ConfigureAwait(false);

        return _cache.TryGetValue(item.Id, out var built) ? built.Report : new ReferenceReport();
    }

    /// <summary>Returns the cached account for an item, without building one.</summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns>The account, or null when nothing is cached.</returns>
    public ReferenceReport? PeekReport(Guid itemId) =>
        _cache.TryGetValue(itemId, out var cached) ? cached.Report : null;

    /// <summary>Forgets any cached reference for an item.</summary>
    /// <param name="itemId">The item ID.</param>
    public void Invalidate(Guid itemId) => _cache.TryRemove(itemId, out _);

    private async Task<ReferenceTrack?> BuildAsync(
        BaseItem item,
        bool allowAudioFallback,
        ReferenceReport report,
        CancellationToken cancellationToken)
    {
        var reference = await embedded.TryGetAsync(item, report, cancellationToken).ConfigureAwait(false);
        if (reference is not null)
        {
            return reference;
        }

        if (!allowAudioFallback)
        {
            logger.LogDebug(
                "{Path} has no embedded subtitle track and the audio fallback is disabled.",
                item.Path);

            report.Note = "No embedded subtitle track could be used, and the audio fallback is turned off.";
            return null;
        }

        logger.LogDebug("Falling back to audio analysis for {Path}.", item.Path);
        return await audio.TryGetAsync(item, report, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Identifies the media file's current state, so a replaced or re-encoded file is not matched
    /// against a stale reference.
    /// </summary>
    private static long MediaStamp(BaseItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                return 0;
            }

            var info = new FileInfo(item.Path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void Store(Guid itemId, CacheEntry entry)
    {
        _cache[itemId] = entry;

        if (_cache.Count <= MaxEntries)
        {
            return;
        }

        // Cheap eviction: drop the oldest few rather than tracking access order.
        foreach (var stale in _cache.OrderBy(e => e.Value.CachedAt).Take(_cache.Count - MaxEntries).ToList())
        {
            _cache.TryRemove(stale.Key, out _);
        }
    }
}
