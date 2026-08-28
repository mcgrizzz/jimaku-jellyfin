using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// Derives a timing reference from the local media.
/// </summary>
public interface IReferenceTrackProvider
{
    /// <summary>
    /// Attempts to build a reference track.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reference, or null when this provider cannot supply one.</returns>
    Task<ReferenceTrack?> TryGetAsync(BaseItem item, CancellationToken cancellationToken);
}
