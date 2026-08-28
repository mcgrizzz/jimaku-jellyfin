using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Timing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// What the plugin last did with one episode.
/// </summary>
public sealed class SyncHistoryEntry
{
    /// <summary>Gets or sets when the attempt was made.</summary>
    public DateTimeOffset AttemptedUtc { get; set; }

    /// <summary>Gets or sets the verdict reached.</summary>
    public SyncVerdict Verdict { get; set; }

    /// <summary>Gets or sets the Jimaku entry the file came from.</summary>
    public long EntryId { get; set; }

    /// <summary>Gets or sets the chosen file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the offset applied, in seconds.</summary>
    public double OffsetSeconds { get; set; }

    /// <summary>Gets or sets the time scale applied.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Gets or sets the correlation achieved.</summary>
    public double Correlation { get; set; }

    /// <summary>Gets or sets the peak-to-second-peak ratio achieved.</summary>
    public double PeakRatio { get; set; }

    /// <summary>Gets or sets the sidecar that was written, if any.</summary>
    public string SidecarPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the explanation, which matters most when the verdict was a decline.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Records per-episode outcomes on disk.
/// </summary>
/// <remarks>
/// This is what lets the scheduled sweep be cheap on its second and subsequent runs: episodes that
/// already succeeded are skipped outright, and episodes that were declined are not retried until
/// enough time has passed that Jimaku might plausibly have new uploads. Without it, every sweep
/// would re-download and re-analyse the same failures forever, against a 25-request-per-minute API.
/// </remarks>
public sealed class SyncHistoryStore(ILogger<SyncHistoryStore> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<Guid, SyncHistoryEntry> _cache = new();

    /// <summary>Gets or sets the directory history is stored in.</summary>
    public string Directory { get; set; } = Path.GetTempPath();

    /// <summary>Reads the recorded outcome for an item.</summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns>The entry, or null when the item has never been attempted.</returns>
    public SyncHistoryEntry? Get(Guid itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var path = PathFor(itemId);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<SyncHistoryEntry>(File.ReadAllText(path), SerializerOptions);
            if (entry is not null)
            {
                _cache[itemId] = entry;
            }

            return entry;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogDebug(ex, "Could not read sync history for {ItemId}.", itemId);
            return null;
        }
    }

    /// <summary>Records an outcome.</summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="entry">The outcome.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task SetAsync(Guid itemId, SyncHistoryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _cache[itemId] = entry;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await File.WriteAllTextAsync(
                PathFor(itemId),
                JsonSerializer.Serialize(entry, SerializerOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // Losing history costs a redundant retry later; it must never fail the operation.
            logger.LogDebug(ex, "Could not write sync history for {ItemId}.", itemId);
        }
    }

    /// <summary>
    /// Decides whether the scheduled sweep should skip an item.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="retryDeclinedAfterDays">
    /// How long a decline stands before it is worth trying again. Jimaku gains uploads over time,
    /// so a decline is a statement about today, not forever.
    /// </param>
    /// <param name="reason">Receives the reason for skipping.</param>
    /// <returns><see langword="true"/> when the item should be skipped.</returns>
    public bool ShouldSkip(Guid itemId, int retryDeclinedAfterDays, out string reason)
    {
        reason = string.Empty;
        var entry = Get(itemId);
        if (entry is null)
        {
            return false;
        }

        if (entry.Verdict is SyncVerdict.Exact or SyncVerdict.ConstantOffset
            or SyncVerdict.FramerateDrift or SyncVerdict.PiecewiseCut)
        {
            // Only skip if what was written is still there; the user may have deleted it.
            if (!string.IsNullOrEmpty(entry.SidecarPath) && File.Exists(entry.SidecarPath))
            {
                reason = "already has a subtitle written by this plugin";
                return true;
            }

            return false;
        }

        var age = DateTimeOffset.UtcNow - entry.AttemptedUtc;
        if (age < TimeSpan.FromDays(retryDeclinedAfterDays))
        {
            var days = Math.Max(0, retryDeclinedAfterDays - (int)age.TotalDays);
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"declined {(int)age.TotalDays}d ago, retrying in {days}d");
            return true;
        }

        return false;
    }

    private string PathFor(Guid itemId) => Path.Combine(Directory, itemId.ToString("N") + ".json");
}
