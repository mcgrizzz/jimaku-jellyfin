using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Produces the best timing reference available for a media item.
/// </summary>
public sealed class ReferenceTrackResolver(
    EmbeddedSubtitleReferenceProvider embedded,
    AudioActivityReferenceProvider audio,
    ILogger<ReferenceTrackResolver> logger)
{
    /// <summary>
    /// Resolves a reference, preferring an embedded subtitle track and falling back to audio.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="allowAudioFallback">Whether audio analysis may be used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reference, or null when none could be built.</returns>
    public async Task<ReferenceTrack?> ResolveAsync(
        BaseItem item,
        bool allowAudioFallback,
        CancellationToken cancellationToken)
    {
        var reference = await embedded.TryGetAsync(item, cancellationToken).ConfigureAwait(false);
        if (reference is not null)
        {
            return reference;
        }

        if (!allowAudioFallback)
        {
            logger.LogDebug(
                "{Path} has no embedded subtitle track and the audio fallback is disabled.",
                item.Path);
            return null;
        }

        logger.LogDebug("Falling back to audio analysis for {Path}.", item.Path);
        return await audio.TryGetAsync(item, cancellationToken).ConfigureAwait(false);
    }
}
