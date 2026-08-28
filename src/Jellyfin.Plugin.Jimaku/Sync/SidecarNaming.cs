using System;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// Builds external subtitle filenames that Jellyfin will actually recognise.
/// </summary>
/// <remarks>
/// <para>
/// The rules are strict and fail silently when broken, so they are worth stating. Jellyfin's
/// external-file resolver only considers files whose name-without-extension begins with the video's
/// name-without-extension, followed by a dot. A dot is the only delimiter it accepts, so
/// <c>Show S01E01 - ja.srt</c> is not recognised at all.
/// </para>
/// <para>
/// Tokens after the prefix are then read <em>right to left</em>, and the first one that parses as a
/// language wins. So the language must be the last token before the extension: in
/// <c>Show.ja.fr.srt</c> the track would be French, with "ja" becoming part of the title.
/// </para>
/// </remarks>
public static class SidecarNaming
{
    /// <summary>
    /// Builds the filename for a subtitle sidecar.
    /// </summary>
    /// <param name="videoPath">Path to the media file.</param>
    /// <param name="languageTag">The language tag, which becomes the final token.</param>
    /// <param name="extension">The extension, without a leading dot.</param>
    /// <returns>The filename, without a directory.</returns>
    public static string BuildFileName(string videoPath, string languageTag, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageTag);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return $"{baseName}.{languageTag.ToLowerInvariant()}.{extension.TrimStart('.').ToLowerInvariant()}";
    }

    /// <summary>
    /// Resolves the full path to write to, avoiding an existing file unless told to overwrite.
    /// </summary>
    /// <param name="folder">The directory to write into.</param>
    /// <param name="videoPath">Path to the media file.</param>
    /// <param name="languageTag">The language tag.</param>
    /// <param name="extension">The extension, without a leading dot.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="exists">Existence test, injected so the rule can be tested without a disk.</param>
    /// <returns>The path to write to.</returns>
    public static string Resolve(
        string folder,
        string videoPath,
        string languageTag,
        string extension,
        bool overwrite,
        Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var path = Path.Combine(folder, BuildFileName(videoPath, languageTag, extension));
        if (overwrite || !exists(path))
        {
            return path;
        }

        // Match core's de-duplication rather than silently clobbering a subtitle the user may have
        // placed or corrected themselves. The counter sits before the language so that the language
        // stays the final token and the file is still recognised.
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        var suffix = $"{languageTag.ToLowerInvariant()}.{extension.TrimStart('.').ToLowerInvariant()}";

        for (var counter = 1; counter < 100; counter++)
        {
            var candidate = Path.Combine(
                folder,
                string.Create(CultureInfo.InvariantCulture, $"{baseName}.{counter}.{suffix}"));

            if (!exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }
}
