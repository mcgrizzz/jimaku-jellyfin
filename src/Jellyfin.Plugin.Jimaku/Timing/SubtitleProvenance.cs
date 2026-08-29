using System;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// Writes where a subtitle came from into the subtitle itself.
/// </summary>
/// <remarks>
/// <para>
/// A sidecar's filename is dictated by Jellyfin's external-file resolver: it must begin with the
/// video's name and end with the language tag, which leaves nowhere to record that the file
/// actually came from, say, <c>[AnimeOut] Mushoku Tensei - 09 [BD 1080p].ass</c>. So two files that
/// came from entirely different Jimaku uploads are indistinguishable on disk.
/// </para>
/// <para>
/// The plugin's own history answers that, but only for as long as its data folder survives. A line
/// inside the file survives backups, moves, a plugin reinstall, and being opened in Aegisub - and
/// costs one comment.
/// </para>
/// </remarks>
public static class SubtitleProvenance
{
    /// <summary>The prefix identifying the plugin's own comment line.</summary>
    public const string Marker = "; Jimaku:";

    /// <summary>
    /// Builds the provenance line for a file.
    /// </summary>
    /// <param name="fileName">The Jimaku file name.</param>
    /// <param name="entryId">The Jimaku entry ID.</param>
    /// <param name="transform">The correction that was applied.</param>
    /// <param name="stampedUtc">When it was written.</param>
    /// <returns>A single comment line, without a line ending.</returns>
    public static string BuildLine(
        string fileName,
        long entryId,
        TimingTransform transform,
        DateTimeOffset stampedUtc) => string.Create(
            CultureInfo.InvariantCulture,
            $"{Marker} {fileName} | entry {entryId} | {transform.Describe()} | {stampedUtc:yyyy-MM-dd}");

    /// <summary>
    /// Reads the provenance line back out of a subtitle, if it has one.
    /// </summary>
    /// <param name="text">The subtitle text.</param>
    /// <returns>The line's contents after the marker, or null when the file was not written here.</returns>
    public static string? Read(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (var line in text.Split('\n').Take(40))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(Marker, StringComparison.Ordinal))
            {
                return trimmed[Marker.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Adds - or replaces - the provenance line in an ASS script.
    /// </summary>
    /// <remarks>
    /// SubRip has no comment syntax, so SRT output is returned untouched rather than having
    /// something invented for it that players would render as dialogue.
    /// </remarks>
    /// <param name="text">The subtitle text.</param>
    /// <param name="kind">The format.</param>
    /// <param name="line">The line to write, from <see cref="BuildLine"/>.</param>
    /// <returns>The stamped text.</returns>
    public static string Stamp(string text, SubtitleFormatKind kind, string line)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(line);

        if (kind != SubtitleFormatKind.Ass)
        {
            return text;
        }

        // Match the file's own line endings; mixing them upsets some of the stricter parsers.
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').ToList();

        // Re-stamping must replace, not accumulate. A file that has been corrected twice should
        // still say where it came from once.
        lines.RemoveAll(l => l.TrimStart().StartsWith(Marker, StringComparison.Ordinal));

        var header = lines.FindIndex(l =>
            l.Trim().StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase));

        if (header >= 0)
        {
            lines.Insert(header + 1, line);
        }
        else
        {
            // No section header to hang it off. A leading comment is still legal ASS, and any
            // parser that reaches the dialogue at all will skip it.
            lines.Insert(0, line);
        }

        return string.Join(newline, lines.Select(l => l.TrimEnd('\r')));
    }
}
