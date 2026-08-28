using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Jimaku.Jimaku.Models;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>Why a candidate file was rejected before any timing work was done.</summary>
public enum RejectionReason
{
    /// <summary>Not rejected.</summary>
    None = 0,

    /// <summary>The extension is not a subtitle or supported archive format.</summary>
    UnsupportedExtension,

    /// <summary>A RAR or 7z archive, which cannot be opened without a third-party dependency.</summary>
    UnreadableArchive,

    /// <summary>An archive was offered alongside plain subtitle files, which are preferred.</summary>
    ArchiveNotNeeded,

    /// <summary>Archives are disabled in configuration.</summary>
    ArchivesDisabled,

    /// <summary>Machine-transcribed subtitles, which are rarely worth attaching.</summary>
    MachineTranslated,

    /// <summary>Too small to be a real subtitle file.</summary>
    TooSmall,
}

/// <summary>A Jimaku file with the outcome of filtering.</summary>
/// <param name="File">The file.</param>
/// <param name="Rejection">Why it was rejected, or <see cref="RejectionReason.None"/>.</param>
public readonly record struct FilteredCandidate(JimakuFile File, RejectionReason Rejection)
{
    /// <summary>Gets a value indicating whether the file survived filtering.</summary>
    public bool IsAccepted => Rejection == RejectionReason.None;

    /// <summary>Returns a short human-readable explanation of the rejection.</summary>
    /// <returns>The explanation, or an empty string when accepted.</returns>
    public string Explain() => Rejection switch
    {
        RejectionReason.None => string.Empty,
        RejectionReason.UnsupportedExtension => "not a subtitle file",
        RejectionReason.UnreadableArchive => "RAR and 7z archives are not supported",
        RejectionReason.ArchiveNotNeeded => "plain subtitle files were available instead",
        RejectionReason.ArchivesDisabled => "archive downloads are disabled",
        RejectionReason.MachineTranslated => "machine-generated subtitles",
        RejectionReason.TooSmall => "file is too small to be a real subtitle",
        _ => "rejected",
    };
}

/// <summary>
/// Discards Jimaku files that are not worth downloading, before any timing analysis happens.
/// </summary>
/// <remarks>
/// The Emby plugin this replaces did none of this: it returned every file the API offered, archives
/// and machine transcriptions included, and left the user to guess. The rules here follow Bazarr's
/// Jimaku provider, which has had far more exposure to what the site actually hosts.
/// </remarks>
public static partial class CandidateFilter
{
    private const long MinimumFileSize = 500;

    private static readonly HashSet<string> SubtitleExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ass", ".ssa", ".srt" };

    private static readonly HashSet<string> ReadableArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip" };

    private static readonly HashSet<string> UnreadableArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".rar", ".7z" };

    [GeneratedRegex(@"[\[\(]?(whisperai)[\]\)]?|[\[\(]whisper[\]\)]", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex MachineTranslatedPattern();

    /// <summary>Gets a value indicating whether a filename names a supported archive.</summary>
    /// <param name="name">The file name.</param>
    /// <returns><see langword="true"/> for an archive this plugin can open.</returns>
    public static bool IsReadableArchive(string name) =>
        ReadableArchiveExtensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// Filters a set of candidate files.
    /// </summary>
    /// <param name="files">The files offered by Jimaku.</param>
    /// <param name="allowArchives">Whether archives may be considered at all.</param>
    /// <returns>Every file, annotated with whether and why it was rejected.</returns>
    public static IReadOnlyList<FilteredCandidate> Filter(
        IReadOnlyList<JimakuFile> files,
        bool allowArchives)
    {
        ArgumentNullException.ThrowIfNull(files);

        // Plain subtitle files always beat archives, so establish whether any exist before deciding
        // what to do with the archives.
        var hasPlainSubtitle = false;
        foreach (var file in files)
        {
            if (SubtitleExtensions.Contains(Path.GetExtension(file.Name)) &&
                !MachineTranslatedPattern().IsMatch(file.Name) &&
                file.Size >= MinimumFileSize)
            {
                hasPlainSubtitle = true;
                break;
            }
        }

        var results = new List<FilteredCandidate>(files.Count);
        foreach (var file in files)
        {
            results.Add(new FilteredCandidate(file, Classify(file, allowArchives, hasPlainSubtitle)));
        }

        return results;
    }

    private static RejectionReason Classify(JimakuFile file, bool allowArchives, bool hasPlainSubtitle)
    {
        if (MachineTranslatedPattern().IsMatch(file.Name))
        {
            return RejectionReason.MachineTranslated;
        }

        var extension = Path.GetExtension(file.Name);

        if (UnreadableArchiveExtensions.Contains(extension))
        {
            return RejectionReason.UnreadableArchive;
        }

        if (ReadableArchiveExtensions.Contains(extension))
        {
            if (!allowArchives)
            {
                return RejectionReason.ArchivesDisabled;
            }

            return hasPlainSubtitle ? RejectionReason.ArchiveNotNeeded : RejectionReason.None;
        }

        if (!SubtitleExtensions.Contains(extension))
        {
            return RejectionReason.UnsupportedExtension;
        }

        // Anything this small is a stub, a placeholder, or a truncated upload.
        return file.Size < MinimumFileSize ? RejectionReason.TooSmall : RejectionReason.None;
    }
}
