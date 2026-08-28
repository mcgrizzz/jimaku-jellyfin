using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
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
public sealed class SidecarWriter(
    ILibraryManager libraryManager,
    ILibraryMonitor libraryMonitor,
    IProviderManager providerManager,
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

        await providerManager.RefreshSingleItem(item, options, cancellationToken).ConfigureAwait(false);
    }
}
