using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Timing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// How one attempt ended up, once later events are taken into account.
/// </summary>
public enum AttemptStatus
{
    /// <summary>Nothing was written.</summary>
    Declined = 0,

    /// <summary>Written, and still the subtitle in use.</summary>
    Applied = 1,

    /// <summary>Written, then replaced by a later attempt.</summary>
    Superseded = 2,

    /// <summary>Written, then thrown away - either explicitly, or by deleting the file.</summary>
    Rejected = 3,
}

/// <summary>
/// One thing the plugin tried, kept even after it stopped being the answer.
/// </summary>
/// <remarks>
/// The sidecar's filename is derived from the media file, so the name of the Jimaku upload it came
/// from does not survive on disk. Without this record there is no way to answer "what is actually
/// attached to this episode", let alone "what did I already try and reject".
/// </remarks>
public sealed class SyncAttempt
{
    /// <summary>Gets or sets when the attempt was made.</summary>
    public DateTimeOffset AttemptedUtc { get; set; }

    /// <summary>Gets or sets the verdict reached.</summary>
    public SyncVerdict Verdict { get; set; }

    /// <summary>Gets or sets how the attempt ended up.</summary>
    public AttemptStatus Status { get; set; }

    /// <summary>Gets or sets the Jimaku entry the file came from.</summary>
    public long EntryId { get; set; }

    /// <summary>Gets or sets the Jimaku file name, which the sidecar's own name does not preserve.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the release group parsed from that name, if it had one.</summary>
    public string ReleaseGroup { get; set; } = string.Empty;

    /// <summary>Gets or sets the sidecar written, if any.</summary>
    public string SidecarPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the offset applied, in seconds.</summary>
    public double OffsetSeconds { get; set; }

    /// <summary>Gets or sets the time scale applied.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Gets or sets the correlation achieved.</summary>
    public double Correlation { get; set; }

    /// <summary>Gets or sets the explanation.</summary>
    public string Reason { get; set; } = string.Empty;
}

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

    /// <summary>
    /// Gets or sets everything that has been tried for this episode, oldest first.
    /// </summary>
    public List<SyncAttempt> Attempts { get; set; } = [];

    /// <summary>
    /// Gets or sets the Jimaku file names that were attached and then thrown away.
    /// </summary>
    /// <remarks>
    /// Automatic selection skips these. A file the user deleted is a judgement the plugin cannot
    /// make for itself - it has no way to hear that the translation reads badly, or that the timing
    /// drifts somewhere its sampling did not look - and re-picking it on the next run turns that
    /// judgement into a loop. An explicit pick still overrides this: choosing a file by hand is a
    /// newer statement than the old rejection.
    /// </remarks>
    public List<string> RejectedFileNames { get; set; } = [];
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
    private const int MaxAttempts = 20;

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

    /// <summary>
    /// Records an attempt, retiring whatever it replaces.
    /// </summary>
    /// <remarks>
    /// A newly applied subtitle supersedes the one before it; that older attempt is kept rather
    /// than overwritten, because "what did I already try" is exactly the question the sidecar's
    /// filename cannot answer.
    /// </remarks>
    /// <param name="itemId">The item ID.</param>
    /// <param name="attempt">What was tried.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task RecordAttemptAsync(
        Guid itemId,
        SyncAttempt attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var entry = Get(itemId) ?? new SyncHistoryEntry();

        if (attempt.Status == AttemptStatus.Applied)
        {
            foreach (var previous in entry.Attempts.Where(a => a.Status == AttemptStatus.Applied))
            {
                previous.Status = AttemptStatus.Superseded;
            }
        }

        entry.Attempts.Add(attempt);

        // Bounded: this is a record of what was tried, not an audit trail. Twenty is far more than
        // any episode will legitimately accumulate, and it keeps the file small enough to stay
        // cheap to read on every sync.
        while (entry.Attempts.Count > MaxAttempts)
        {
            entry.Attempts.RemoveAt(0);
        }

        entry.AttemptedUtc = attempt.AttemptedUtc;
        entry.Verdict = attempt.Verdict;
        entry.EntryId = attempt.EntryId;
        entry.FileName = attempt.FileName;
        entry.OffsetSeconds = attempt.OffsetSeconds;
        entry.Scale = attempt.Scale;
        entry.Correlation = attempt.Correlation;
        entry.SidecarPath = attempt.SidecarPath;
        entry.Reason = attempt.Reason;

        await SetAsync(itemId, entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the sidecars this plugin currently believes it has attached to an item.
    /// </summary>
    /// <remarks>
    /// Only ever paths the plugin recorded writing. Nothing else is a candidate for removal - a
    /// subtitle the user placed themselves is not ours to tidy away.
    /// </remarks>
    /// <param name="itemId">The item ID.</param>
    /// <returns>The paths, which may be empty.</returns>
    public IReadOnlyList<string> AppliedSidecarPaths(Guid itemId)
    {
        var entry = Get(itemId);
        if (entry is null)
        {
            return [];
        }

        return entry.Attempts
            .Where(a => a.Status == AttemptStatus.Applied && !string.IsNullOrEmpty(a.SidecarPath))
            .Select(a => a.SidecarPath)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Notices that an applied subtitle is no longer on disk, and records that as a rejection.
    /// </summary>
    /// <remarks>
    /// Deleting the file is how a person says "this one is wrong", and it is the only such signal
    /// available: nothing else tells the plugin that a subtitle it was pleased with reads badly.
    /// Treating it as data rather than as an absence is what stops the next run from cheerfully
    /// re-downloading the same file.
    /// </remarks>
    /// <param name="itemId">The item ID.</param>
    /// <param name="stillPresent">Whether the episode still has a subtitle this plugin could have written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attempt that was rejected, or null when there was nothing to reject.</returns>
    public async Task<SyncAttempt?> NoteDeletionAsync(
        Guid itemId,
        bool stillPresent,
        CancellationToken cancellationToken)
    {
        if (stillPresent)
        {
            return null;
        }

        var entry = Get(itemId);
        var applied = entry?.Attempts.LastOrDefault(a => a.Status == AttemptStatus.Applied);
        if (entry is null || applied is null)
        {
            return null;
        }

        applied.Status = AttemptStatus.Rejected;
        AddRejection(entry, applied.FileName);

        await SetAsync(itemId, entry, cancellationToken).ConfigureAwait(false);
        return applied;
    }

    /// <summary>
    /// Marks whatever is currently applied as rejected, at the user's word rather than by inference.
    /// </summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attempt that was rejected, or null when nothing was applied.</returns>
    public async Task<SyncAttempt?> RejectCurrentAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var entry = Get(itemId);
        var applied = entry?.Attempts.LastOrDefault(a => a.Status == AttemptStatus.Applied);
        if (entry is null || applied is null)
        {
            return null;
        }

        applied.Status = AttemptStatus.Rejected;
        AddRejection(entry, applied.FileName);

        await SetAsync(itemId, entry, cancellationToken).ConfigureAwait(false);
        return applied;
    }

    /// <summary>Lists the file names automatic selection should skip for an episode.</summary>
    /// <param name="itemId">The item ID.</param>
    /// <returns>The rejected file names, compared case-insensitively.</returns>
    public IReadOnlySet<string> RejectedFileNames(Guid itemId)
    {
        var entry = Get(itemId);
        return entry is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(entry.RejectedFileNames, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Forgets an episode's rejections, so everything is back on the table.</summary>
    /// <param name="itemId">The item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task ClearRejectionsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var entry = Get(itemId);
        if (entry is null || entry.RejectedFileNames.Count == 0)
        {
            return;
        }

        entry.RejectedFileNames.Clear();
        await SetAsync(itemId, entry, cancellationToken).ConfigureAwait(false);
    }

    private static void AddRejection(SyncHistoryEntry entry, string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return;
        }

        if (!entry.RejectedFileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            entry.RejectedFileNames.Add(fileName);
        }
    }

    private string PathFor(Guid itemId) => Path.Combine(Directory, itemId.ToString("N") + ".json");
}
