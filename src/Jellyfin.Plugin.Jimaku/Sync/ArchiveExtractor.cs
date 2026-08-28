using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// Pulls a single episode's subtitle out of a ZIP archive.
/// </summary>
/// <remarks>
/// Season packs are common on Jimaku. Only ZIP is handled: RAR and 7z would each need a third-party
/// dependency, and those files are filtered out earlier with a reason the user can see rather than
/// failing here.
/// </remarks>
public static class ArchiveExtractor
{
    private static readonly string[] SubtitleExtensions = [".ass", ".ssa", ".srt"];

    /// <summary>
    /// Finds the entry for one episode inside an archive.
    /// </summary>
    /// <param name="archiveBytes">The archive contents.</param>
    /// <param name="episodeNumber">The episode to look for, if known.</param>
    /// <param name="fileName">Receives the name of the entry that was extracted.</param>
    /// <returns>The subtitle bytes, or null when nothing suitable was found.</returns>
    public static byte[]? TryExtract(byte[] archiveBytes, int? episodeNumber, out string fileName)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        fileName = string.Empty;

        try
        {
            using var stream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entries = archive.Entries
                .Where(e => e.Length > 0)
                .Where(e => SubtitleExtensions.Contains(Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (entries.Count == 0)
            {
                return null;
            }

            var chosen = entries.Count == 1
                ? entries[0]
                : SelectByEpisode(entries, episodeNumber);

            if (chosen is null)
            {
                return null;
            }

            fileName = chosen.Name;

            using var entryStream = chosen.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static ZipArchiveEntry? SelectByEpisode(List<ZipArchiveEntry> entries, int? episodeNumber)
    {
        if (!episodeNumber.HasValue)
        {
            return null;
        }

        // Reuse the same filename parser the candidate matcher uses, rather than inventing a second
        // set of episode-number heuristics that could disagree with it.
        foreach (var entry in entries)
        {
            var info = Matching.ReleaseInfo.Parse(entry.Name);
            if (info.EpisodeNumber == episodeNumber.Value)
            {
                return entry;
            }
        }

        return null;
    }
}
