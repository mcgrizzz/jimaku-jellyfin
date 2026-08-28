using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jimaku.Jimaku.Models;

/// <summary>
/// Boolean markers on a Jimaku entry.
/// </summary>
public class EntryFlags
{
    /// <summary>Gets or sets a value indicating whether the entry is adult content.</summary>
    [JsonPropertyName("adult")]
    public bool Adult { get; set; }

    /// <summary>Gets or sets a value indicating whether the entry is anime.</summary>
    [JsonPropertyName("anime")]
    public bool Anime { get; set; }

    /// <summary>Gets or sets a value indicating whether the entry is externally sourced.</summary>
    [JsonPropertyName("external")]
    public bool External { get; set; }

    /// <summary>Gets or sets a value indicating whether the entry is a movie rather than a series.</summary>
    [JsonPropertyName("movie")]
    public bool Movie { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the entry is unverified. Worth surfacing to the user,
    /// though it says nothing about whether any particular file is correctly timed.
    /// </summary>
    [JsonPropertyName("unverified")]
    public bool Unverified { get; set; }
}

/// <summary>
/// A Jimaku entry: one work, holding subtitle files for its episodes.
/// </summary>
public class JimakuEntry
{
    /// <summary>Gets or sets the entry ID.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Gets or sets the romaji name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Japanese name.</summary>
    [JsonPropertyName("japanese_name")]
    public string? JapaneseName { get; set; }

    /// <summary>Gets or sets the English name.</summary>
    [JsonPropertyName("english_name")]
    public string? EnglishName { get; set; }

    /// <summary>Gets or sets the AniList ID.</summary>
    [JsonPropertyName("anilist_id")]
    public int? AniListId { get; set; }

    /// <summary>Gets or sets the TMDB ID, formatted as <c>tv:12345</c> or <c>movie:12345</c>.</summary>
    [JsonPropertyName("tmdb_id")]
    public string? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets editor notes about the entry. Often says which release the subtitles were
    /// timed for, which is exactly what matters when choosing between candidates.
    /// </summary>
    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>Gets or sets the timestamp of the newest uploaded file.</summary>
    [JsonPropertyName("last_modified")]
    public DateTimeOffset LastModified { get; set; }

    /// <summary>Gets or sets the entry flags.</summary>
    [JsonPropertyName("flags")]
    public EntryFlags Flags { get; set; } = new EntryFlags();
}

/// <summary>
/// One downloadable file attached to a Jimaku entry.
/// </summary>
public class JimakuFile
{
    /// <summary>
    /// Gets or sets the absolute download URL. The download route requires no authentication and is
    /// outside the API rate limiter, so it is a plain GET.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the file name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Gets or sets the last modification time.</summary>
    [JsonPropertyName("last_modified")]
    public DateTimeOffset LastModified { get; set; }
}

/// <summary>
/// The error body Jimaku returns on a failed request.
/// </summary>
public class JimakuError
{
    /// <summary>Gets or sets the error message.</summary>
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the error code. 4 is no permissions, 6 not found, 7 unauthorized, 8 rate limited.
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }
}
