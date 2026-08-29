using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// One Jimaku entry, remembered so the next episode of the same series need not search again.
/// </summary>
public sealed class SeriesEntry
{
    /// <summary>Gets or sets the Jimaku entry ID.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the entry name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the uploader's notes.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the entry is flagged unverified.</summary>
    public bool Unverified { get; set; }
}

/// <summary>
/// What repeated successes have taught the plugin about one series.
/// </summary>
/// <remarks>
/// Release groups are consistent across a season: if eight episodes in a row were correctly served
/// by the same group's files, the ninth almost certainly is too. Nothing was carrying that
/// knowledge forward, so every episode was decided from scratch and near-ties were broken by
/// hundredths of a point of measured coverage - noise, in other words.
/// </remarks>
public sealed class SeriesProfile
{
    /// <summary>
    /// Gets or sets the rules the learned fields were gathered under.
    /// </summary>
    /// <remarks>
    /// Version 1 counted every applied subtitle, including the plugin's own automatic picks, which
    /// let a sweep confirm the preference that produced it. What that gathered is not weak evidence
    /// but circular evidence, so it is discarded on upgrade rather than carried forward.
    /// </remarks>
    public int Version { get; set; }

    /// <summary>Gets or sets the release group that has been working for this series.</summary>
    public string PreferredReleaseGroup { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how much evidence stands behind <see cref="PreferredReleaseGroup"/>. This is a
    /// majority vote, not a running total: a success for a different group spends a confirmation
    /// rather than adding one, so a preference that stops working is unseated rather than entrenched.
    /// </summary>
    public int Confirmations { get; set; }

    /// <summary>
    /// Gets or sets the Jimaku entry the last confirmed success came from. Used only when a
    /// candidate's filename names no release group at all, which is common enough to matter.
    /// </summary>
    public long PreferredEntryId { get; set; }

    /// <summary>Gets or sets when the profile last changed.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the lookup the cached entries were found with, so a series that gets re-scraped
    /// onto a different AniList ID does not silently keep the old entries.
    /// </summary>
    public string EntriesKey { get; set; } = string.Empty;

    /// <summary>Gets or sets when the entry list was fetched.</summary>
    public DateTimeOffset EntriesCachedUtc { get; set; }

    /// <summary>Gets or sets the cached entry list.</summary>
    public List<SeriesEntry> Entries { get; set; } = [];
}

/// <summary>
/// Per-series memory: which release group keeps working, and which Jimaku entries the series maps to.
/// </summary>
/// <remarks>
/// Two jobs, both about not repeating work across the episodes of one series. The entry cache is
/// the larger practical win: a 24-episode sweep previously issued 24 identical searches against an
/// API that allows 25 requests a minute, so half the sweep's entire budget was spent re-asking a
/// question whose answer had not changed.
/// </remarks>
public sealed class SeriesProfileStore(ILogger<SeriesProfileStore> logger)
{
    /// <summary>
    /// The point past which more agreement stops meaning anything. Without a cap a preference
    /// backed by fifty episodes would need fifty contrary results to shift, long after the group
    /// had stopped being the right answer.
    /// </summary>
    private const int MaxConfirmations = 10;

    /// <summary>
    /// The current learning rules. Profiles written under an older set are reset on read.
    /// </summary>
    private const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ConcurrentDictionary<Guid, SeriesProfile> _cache = new();

    /// <summary>Gets or sets the directory profiles are stored in.</summary>
    public string Directory { get; set; } = Path.GetTempPath();

    /// <summary>
    /// Folds one confirmed success into a profile.
    /// </summary>
    /// <remarks>
    /// A Boyer-Moore majority vote. A matching group adds a confirmation; a different one spends
    /// a confirmation; a group that reaches zero support is replaced. That gives the preference
    /// hysteresis - one unusual episode does not overturn a season's worth of agreement - while
    /// still letting it move when the series genuinely changes hands.
    /// </remarks>
    /// <param name="profile">The profile to update, in place.</param>
    /// <param name="releaseGroup">The release group of the file that worked, if the name gave one.</param>
    /// <param name="entryId">The Jimaku entry the file came from.</param>
    public static void RecordSuccess(SeriesProfile profile, string? releaseGroup, long entryId)
    {
        ArgumentNullException.ThrowIfNull(profile);

        profile.UpdatedUtc = DateTimeOffset.UtcNow;

        var group = releaseGroup?.Trim() ?? string.Empty;
        if (group.Length == 0)
        {
            // Nothing to learn about naming, but which entry the file came from is still a signal,
            // and it is the only one available for the untagged files this case describes.
            if (profile.Confirmations == 0)
            {
                profile.PreferredEntryId = entryId;
            }

            return;
        }

        if (string.Equals(group, profile.PreferredReleaseGroup, StringComparison.OrdinalIgnoreCase))
        {
            profile.Confirmations = Math.Min(profile.Confirmations + 1, MaxConfirmations);
            profile.PreferredEntryId = entryId;
        }
        else if (profile.Confirmations <= 1)
        {
            profile.PreferredReleaseGroup = group;
            profile.Confirmations = 1;
            profile.PreferredEntryId = entryId;
        }
        else
        {
            profile.Confirmations--;
        }
    }

    /// <summary>
    /// Folds a rejection into a profile: the user threw away a file this group produced.
    /// </summary>
    /// <remarks>
    /// The counterweight to <see cref="RecordSuccess"/>, and the reason it is needed: successes are
    /// judged by correlation, which cannot see that a translation reads badly or that the timing
    /// slips somewhere the sampling did not look. Only the person watching can see that, and
    /// discarding the file is how they say so. One rejection spends one confirmation - enough to
    /// move a preference that keeps disappointing, not enough for a single odd episode to undo a
    /// season of agreement.
    /// </remarks>
    /// <param name="profile">The profile to update, in place.</param>
    /// <param name="releaseGroup">The release group of the file that was thrown away.</param>
    public static void RecordRejection(SeriesProfile profile, string? releaseGroup)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var group = releaseGroup?.Trim() ?? string.Empty;
        if (group.Length == 0
            || !string.Equals(group, profile.PreferredReleaseGroup, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        profile.UpdatedUtc = DateTimeOffset.UtcNow;
        profile.Confirmations = Math.Max(0, profile.Confirmations - 1);

        if (profile.Confirmations == 0)
        {
            profile.PreferredReleaseGroup = string.Empty;
            profile.PreferredEntryId = 0;
        }
    }

    /// <summary>
    /// Decides whether a candidate is the one this series has been served by.
    /// </summary>
    /// <param name="profile">The profile, or null when the series has no history.</param>
    /// <param name="releaseGroup">The candidate's parsed release group.</param>
    /// <param name="entryId">The Jimaku entry the candidate came from.</param>
    /// <param name="minimumConfirmations">How much agreement is required before the preference is used at all.</param>
    /// <returns><see langword="true"/> when the candidate matches the established preference.</returns>
    public static bool IsPreferred(SeriesProfile? profile, string? releaseGroup, long entryId, int minimumConfirmations)
    {
        if (profile is null || profile.Confirmations < minimumConfirmations)
        {
            return false;
        }

        var group = releaseGroup?.Trim() ?? string.Empty;
        if (group.Length > 0)
        {
            return string.Equals(group, profile.PreferredReleaseGroup, StringComparison.OrdinalIgnoreCase);
        }

        // An untagged filename cannot be judged on its group, so fall back to the entry it came
        // from. This is weaker evidence, and deliberately only consulted when there is nothing else.
        return profile.PreferredEntryId != 0 && profile.PreferredEntryId == entryId;
    }

    /// <summary>
    /// Discards learning gathered under rules that allowed it to confirm itself.
    /// </summary>
    /// <remarks>
    /// The cached entry list is untouched - it is a fact about Jimaku, not a judgement - but the
    /// preference is dropped. A preference built partly from a sweep's own choices cannot be
    /// partially salvaged: there is no record of which confirmations were the user's, and leaving
    /// it in place would let a coin flip made during a bulk run keep outranking measurement.
    /// </remarks>
    private void Migrate(SeriesProfile profile, Guid seriesId)
    {
        if (profile.Version >= CurrentVersion)
        {
            return;
        }

        if (profile.Confirmations > 0 || profile.PreferredReleaseGroup.Length > 0)
        {
            logger.LogInformation(
                "Discarding the learned release-group preference for series {SeriesId}: it was gathered when the plugin's own automatic picks counted as confirmations. It will rebuild from subtitles you choose yourself.",
                seriesId);
        }

        profile.PreferredReleaseGroup = string.Empty;
        profile.PreferredEntryId = 0;
        profile.Confirmations = 0;
        profile.Version = CurrentVersion;
    }

    /// <summary>
    /// Forgets what a series has learned, leaving its cached Jimaku entries alone.
    /// </summary>
    /// <param name="seriesId">The series ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task ResetPreferenceAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        var profile = Get(seriesId);
        if (profile is null)
        {
            return;
        }

        profile.PreferredReleaseGroup = string.Empty;
        profile.PreferredEntryId = 0;
        profile.Confirmations = 0;
        profile.Version = CurrentVersion;

        await SaveAsync(seriesId, profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a series' profile.</summary>
    /// <param name="seriesId">The series ID.</param>
    /// <returns>The profile, or null when the series has none.</returns>
    public SeriesProfile? Get(Guid seriesId)
    {
        if (seriesId == Guid.Empty)
        {
            return null;
        }

        if (_cache.TryGetValue(seriesId, out var cached))
        {
            return cached;
        }

        var path = PathFor(seriesId);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var profile = JsonSerializer.Deserialize<SeriesProfile>(File.ReadAllText(path), SerializerOptions);
            if (profile is not null)
            {
                Migrate(profile, seriesId);
                _cache[seriesId] = profile;
            }

            return profile;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogDebug(ex, "Could not read the series profile for {SeriesId}.", seriesId);
            return null;
        }
    }

    /// <summary>Writes a series' profile.</summary>
    /// <param name="seriesId">The series ID.</param>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task SaveAsync(Guid seriesId, SeriesProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (seriesId == Guid.Empty)
        {
            return;
        }

        profile.Version = CurrentVersion;
        _cache[seriesId] = profile;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await File.WriteAllTextAsync(
                PathFor(seriesId),
                JsonSerializer.Serialize(profile, SerializerOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // A lost profile costs a little ranking quality on the next episode, nothing more.
            logger.LogDebug(ex, "Could not write the series profile for {SeriesId}.", seriesId);
        }
    }

    /// <summary>
    /// Returns the cached entry list for a series, when one is present and still fresh.
    /// </summary>
    /// <param name="seriesId">The series ID.</param>
    /// <param name="lookupKey">A fingerprint of how the series was identified.</param>
    /// <param name="ttlHours">How long a cached list stands. Zero disables the cache.</param>
    /// <returns>The entries, or null when the cache cannot be used.</returns>
    public IReadOnlyList<SeriesEntry>? GetEntries(Guid seriesId, string lookupKey, int ttlHours)
    {
        if (ttlHours <= 0)
        {
            return null;
        }

        var profile = Get(seriesId);
        if (profile is null || profile.Entries.Count == 0)
        {
            return null;
        }

        if (!string.Equals(profile.EntriesKey, lookupKey, StringComparison.Ordinal))
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - profile.EntriesCachedUtc > TimeSpan.FromHours(ttlHours))
        {
            return null;
        }

        return profile.Entries;
    }

    /// <summary>Caches the entry list a search returned.</summary>
    /// <param name="seriesId">The series ID.</param>
    /// <param name="lookupKey">A fingerprint of how the series was identified.</param>
    /// <param name="entries">The entries found.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task RememberEntriesAsync(
        Guid seriesId,
        string lookupKey,
        IEnumerable<SeriesEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (seriesId == Guid.Empty)
        {
            return;
        }

        var profile = Get(seriesId) ?? new SeriesProfile();
        profile.Entries = entries.ToList();
        profile.EntriesKey = lookupKey;
        profile.EntriesCachedUtc = DateTimeOffset.UtcNow;

        await SaveAsync(seriesId, profile, cancellationToken).ConfigureAwait(false);
    }

    private string PathFor(Guid seriesId) => Path.Combine(Directory, seriesId.ToString("N") + ".json");
}
