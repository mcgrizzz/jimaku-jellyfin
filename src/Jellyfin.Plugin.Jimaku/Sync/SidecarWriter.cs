using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Timing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// Writes a corrected subtitle next to the media as an external sidecar, then makes Jellyfin notice.
/// </summary>
/// <remarks>
/// A sidecar rather than a remux: the media file is never rewritten, so nothing can be corrupted,
/// the operation is instant regardless of file size, and removing the subtitle is a matter of
/// deleting one small file.
/// </remarks>
/// <remarks>
/// <para>
/// <see cref="IProviderManager"/> is resolved when it is needed rather than injected, and that is
/// load-bearing. Jellyfin builds <c>ProviderManager</c> -&gt; <c>SubtitleManager</c> -&gt; every
/// registered <see cref="MediaBrowser.Controller.Subtitles.ISubtitleProvider"/>, and this plugin
/// registers one. Taking <see cref="IProviderManager"/> as a constructor parameter therefore closes
/// a dependency cycle that the container detects at startup, and the server refuses to boot.
/// </para>
/// </remarks>
public sealed class SidecarWriter(
    ILibraryManager libraryManager,
    ILibraryMonitor libraryMonitor,
    IServiceProvider serviceProvider,
    IFileSystem fileSystem,
    ILogger<SidecarWriter> logger)
{
    /// <summary>
    /// Writes the subtitle and refreshes the item so the new track appears to clients.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="content">The subtitle text.</param>
    /// <param name="extension">The file extension, without a leading dot.</param>
    /// <param name="languageTag">The language tag to embed in the filename.</param>
    /// <param name="overwrite">Whether an existing file at the target path may be replaced.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path written.</returns>
    public async Task<string> WriteAsync(
        BaseItem item,
        string content,
        string extension,
        string languageTag,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(content);

        var path = ResolvePath(item, extension, languageTag, overwrite);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Tell the watcher first, so writing the file does not race a library scan of its own.
        libraryMonitor.ReportFileSystemChangeBeginning(path);
        try
        {
            // UTF-8 without a byte-order mark: every player and Jellyfin's own parsers read it, and
            // a stray BOM shows up as a leading glyph in some renderers.
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            libraryMonitor.ReportFileSystemChangeComplete(path, false);
        }

        logger.LogInformation("Wrote {Path}", path);

        await RefreshAsync(item, cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Builds the sidecar path for an item.
    /// </summary>
    /// <remarks>
    /// The naming mirrors Jellyfin's own <c>SubtitleManager</c>, and it has to: the external-file
    /// resolver only considers files whose name begins with the video's filename followed by a dot,
    /// and it reads tokens right to left taking the first language it recognises. So the language
    /// must be the last token before the extension.
    /// </remarks>
    /// <param name="item">The media item.</param>
    /// <param name="extension">The file extension, without a leading dot.</param>
    /// <param name="languageTag">The language tag.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <returns>The path to write to.</returns>
    public string ResolvePath(BaseItem item, string extension, string languageTag, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(item);

        var options = libraryManager.GetLibraryOptions(item);
        var folder = options is not null && options.SaveSubtitlesWithMedia
            ? item.ContainingFolderPath
            : item.GetInternalMetadataPath();

        return SidecarNaming.Resolve(folder, item.Path, languageTag, extension, overwrite, File.Exists);
    }


    /// <summary>
    /// Lists the subtitle sidecars for an item that this plugin could have written.
    /// </summary>
    /// <remarks>
    /// Used to tell "the user deleted what we attached" from "we never attached anything". The
    /// match is by naming convention rather than by remembered path, because the native subtitle
    /// flow has core write the file and never tells the plugin where it landed.
    /// </remarks>
    /// <param name="item">The media item.</param>
    /// <param name="languageTag">The language tag the sidecar would carry.</param>
    /// <returns>The matching paths, which may be empty.</returns>
    public IReadOnlyList<string> FindExisting(BaseItem item, string languageTag)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrEmpty(item.Path))
        {
            return [];
        }

        var baseName = Path.GetFileNameWithoutExtension(item.Path);
        var found = new List<string>();

        foreach (var folder in CandidateFolders(item))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(folder, baseName + ".*"))
                {
                    if (SidecarNaming.LooksLikeOurs(path, baseName, languageTag))
                    {
                        found.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not list sidecars in {Folder}.", folder);
            }
        }

        return found;
    }

    /// <summary>
    /// Decides whether a subtitle file carries this plugin's provenance stamp.
    /// </summary>
    /// <remarks>
    /// The native subtitle flow has core write the file and never says where, so a path is not
    /// always available to match against. The stamp is: it is written by this plugin and nothing
    /// else, which is what makes it safe to remove a file without a recorded path - a subtitle the
    /// user placed by hand has no stamp and is left alone.
    /// </remarks>
    /// <param name="path">The file to test.</param>
    /// <returns><see langword="true"/> when the file was written by this plugin.</returns>
    public bool WasWrittenHere(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            // The stamp lives in the header, so there is no need to read a whole script.
            var head = new char[4096];
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var read = reader.ReadBlock(head, 0, head.Length);
            return SubtitleProvenance.Read(new string(head, 0, read)) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not read {Path} to check for a provenance stamp.", path);
            return false;
        }
    }

    /// <summary>
    /// Deletes a sidecar this plugin previously wrote.
    /// </summary>
    /// <remarks>
    /// Only ever called with a path the plugin recorded writing itself. Without this, replacing a
    /// subtitle left the old one behind under a <c>.1.</c> counter, so an episode accumulated files
    /// and the player could pick any of them.
    /// </remarks>
    /// <param name="path">The path to remove.</param>
    /// <returns><see langword="true"/> when a file was removed.</returns>
    public bool TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        libraryMonitor.ReportFileSystemChangeBeginning(path);
        try
        {
            File.Delete(path);
            logger.LogInformation("Removed the superseded sidecar {Path}", path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the superseded sidecar {Path}.", path);
            return false;
        }
        finally
        {
            libraryMonitor.ReportFileSystemChangeComplete(path, false);
        }
    }

    private static IEnumerable<string> CandidateFolders(BaseItem item)
    {
        // Either location is possible depending on the library's SaveSubtitlesWithMedia setting,
        // and that setting can change between one sync and the next.
        var containing = item.ContainingFolderPath;
        if (!string.IsNullOrEmpty(containing))
        {
            yield return containing;
        }

        var metadata = item.GetInternalMetadataPath();
        if (!string.IsNullOrEmpty(metadata) && !string.Equals(metadata, containing, StringComparison.Ordinal))
        {
            yield return metadata;
        }
    }

    /// <summary>
    /// Re-probes the item so the new sidecar becomes a visible subtitle track.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the refresh.</returns>
    public async Task RefreshAsync(BaseItem item, CancellationToken cancellationToken)
    {
        // A fresh DirectoryService per item is essential, not incidental. Jellyfin resolves external
        // subtitle files through the directory service passed in here, with its cache left intact,
        // so a service reused across a library sweep would still hold the pre-write listing and the
        // file just written would be invisible.
        var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.None,
            ReplaceAllMetadata = false,
            ReplaceAllImages = false,
            EnableRemoteContentProbe = false,

            // User-initiated, which bypasses the "refreshed recently" throttle.
            IsAutomated = false,
            ForceSave = true,
        };

        // Deferred deliberately; see the note on the class about the startup dependency cycle.
        var providerManager = serviceProvider.GetRequiredService<IProviderManager>();
        await providerManager.RefreshSingleItem(item, options, cancellationToken).ConfigureAwait(false);
    }
}
