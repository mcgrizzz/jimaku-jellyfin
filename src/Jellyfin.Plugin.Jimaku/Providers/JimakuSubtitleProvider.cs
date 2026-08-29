using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Jimaku.Models;
using Jellyfin.Plugin.Jimaku.Sync;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Jimaku.Providers;

/// <summary>
/// Exposes Jimaku through Jellyfin's built-in subtitle search.
/// </summary>
/// <remarks>
/// <para>
/// This is what gives the plugin a per-episode action in every client rather than only in its own
/// settings page, which matters for the mobile clients this exists to serve.
/// </para>
/// <para>
/// The split between the two methods is deliberate. <see cref="Search"/> only queries Jimaku and
/// scores filenames, so the picker appears promptly. All the expensive work - downloading,
/// extracting a reference from the media, correlating, correcting - happens in
/// <see cref="GetSubtitles"/>, on the one file the user actually chose.
/// </para>
/// </remarks>
public class JimakuSubtitleProvider(
    IServiceProvider serviceProvider,
    ILibraryManager libraryManager,
    ILogger<JimakuSubtitleProvider> logger) : ISubtitleProvider
{
    // Every ISubtitleProvider is constructed while the container is still building
    // IProviderManager, so anything pulled in here is pulled in mid-graph. Resolving the sync
    // service on use keeps this constructor free of transitive dependencies and makes it
    // impossible for a future dependency of the pipeline to deadlock server startup.
    private JimakuSyncService SyncService => serviceProvider.GetRequiredService<JimakuSyncService>();

    /// <inheritdoc />
    public string Name => "Jimaku";

    /// <inheritdoc />
    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Episode];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(
        SubtitleSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsJapanese(request.Language) && !IsJapanese(request.TwoLetterISOLanguageName))
        {
            return [];
        }

        // Jellyfin's subtitle search request carries the episode's own provider IDs, and anime IDs
        // live on the series, so the item has to be recovered from its path to reach the parent.
        if (libraryManager.FindByPath(request.MediaPath, false) is not Episode episode)
        {
            logger.LogDebug("Could not resolve an episode for {Path}.", request.MediaPath);
            return [];
        }

        try
        {
            var candidates = await SyncService.FindCandidatesAsync(episode, cancellationToken).ConfigureAwait(false);

            return candidates
                .Where(c => c.IsUsable)
                .Select(c => new RemoteSubtitleInfo
                {
                    Id = SubtitleId.Encode(c.EntryId, c.File.Name, c.File.Url, request.MediaPath),
                    Name = Describe(c),
                    ProviderName = Name,
                    Format = Path.GetExtension(c.File.Name).TrimStart('.').ToLowerInvariant(),
                    ThreeLetterISOLanguageName = "jpn",
                    IsHashMatch = c.NameMatch.IsExactRelease,
                    DateCreated = c.File.LastModified.DateTime,

                    // Machine transcriptions are filtered out before this point, so anything
                    // reaching the picker is human-authored.
                    AiTranslated = false,
                    MachineTranslated = false,
                })
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Searching Jimaku for {Path} failed.", request.MediaPath);
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var (entryId, fileName, url, itemPath) = SubtitleId.Decode(id);

        if (libraryManager.FindByPath(itemPath, false) is not Episode episode)
        {
            throw new InvalidOperationException("The episode this subtitle belongs to could not be resolved.");
        }

        var result = await SyncService.SyncEpisodeAsync(
            episode,
            new SyncOptions
            {
                AllowPiecewise = true,
                ForcedFile = new JimakuFile { Name = fileName, Url = url },
                ForcedEntryId = entryId,

                // Core's SubtitleManager saves whatever comes back and refreshes the item, so
                // writing a sidecar here too would leave two copies of the same subtitle.
                WriteSidecar = false,

                // Someone tapped download in a client and is now looking at a dialog that will
                // tell them nothing. This is what lets the plugin speak for itself afterwards.
                Interactive = true,
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Applied || result.Content is null)
        {
            // Surfacing the reason matters: this is the message the user sees when the plugin
            // refuses to attach something it could not verify.
            throw new InvalidOperationException(result.Message);
        }

        return new SubtitleResponse
        {
            Format = result.Extension ?? "ass",
            Language = "jpn",
            Stream = new MemoryStream(Encoding.UTF8.GetBytes(result.Content)),
        };
    }

    private static bool IsJapanese(string? language) =>
        language is not null &&
        (language.Equals("jpn", StringComparison.OrdinalIgnoreCase) ||
         language.Equals("ja", StringComparison.OrdinalIgnoreCase) ||
         language.Equals("japanese", StringComparison.OrdinalIgnoreCase));

    private static string Describe(SubtitleCandidate candidate)
    {
        var notes = candidate.NameMatch.IsExactRelease
            ? "exact release match"
            : string.Create(CultureInfo.InvariantCulture, $"match {candidate.NameMatch.Score}/100 - {candidate.NameMatch.Notes}");

        return $"{candidate.File.Name}  ({notes})";
    }
}

/// <summary>
/// Packs the information needed to fetch a candidate into a subtitle ID.
/// </summary>
/// <remarks>
/// Base64Url rather than plain Base64: Jellyfin round-trips these IDs through a query string, and
/// the Emby plugin this replaces had to strip injected spaces from its padded UTF-16 Base64 to cope.
/// </remarks>
internal static class SubtitleId
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Encode(long entryId, string fileName, string url, string itemPath)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Payload(entryId, fileName, url, itemPath), Options);
        return Base64Url.EncodeToString(payload);
    }

    public static (long EntryId, string FileName, string Url, string ItemPath) Decode(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(id), Options)
            ?? throw new InvalidOperationException("The subtitle identifier could not be decoded.");

        return (payload.EntryId, payload.FileName, payload.Url, payload.ItemPath);
    }

    private sealed record Payload(long EntryId, string FileName, string Url, string ItemPath);
}
