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
            var usable = candidates.Where(c => c.IsUsable).ToList();

            if (usable.Count == 0)
            {
                return [];
            }

            // First in the list, and the only entry most people should need. Core preserves the
            // order a provider returns, so this stays at the top; picking it runs the same
            // selection the plugin's own page runs, which measures every candidate against the
            // episode rather than asking the user to guess from filenames.
            var results = new List<RemoteSubtitleInfo>(usable.Count + 1)
            {
                new()
                {
                    Id = SubtitleId.EncodeAuto(request.MediaPath),
                    Name = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Best match - checked against this episode ({usable.Count} candidates)"),
                    ProviderName = Name,
                    Format = "ass",
                    ThreeLetterISOLanguageName = "jpn",
                    Comment = "Downloads whichever of the files below actually lines up with your copy, correcting its timing if it needs it.",
                },
            };

            results.AddRange(usable
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
                }));

            return results;
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
        var (entryId, fileName, url, itemPath, auto) = SubtitleId.Decode(id);

        if (libraryManager.FindByPath(itemPath, false) is not Episode episode)
        {
            throw new InvalidOperationException("The episode this subtitle belongs to could not be resolved.");
        }

        var result = await SyncService.SyncEpisodeAsync(
            episode,
            new SyncOptions
            {
                AllowPiecewise = true,

                // The automatic entry forces nothing: leaving these unset is what makes the
                // pipeline measure every candidate and rank them, rather than verifying one.
                ForcedFile = auto ? null : new JimakuFile { Name = fileName, Url = url },
                ForcedEntryId = auto ? 0 : entryId,

                // Core's SubtitleManager saves whatever comes back and refreshes the item, so
                // writing a sidecar here too would leave two copies of the same subtitle.
                WriteSidecar = false,

                // Someone tapped download in a client and is now looking at a dialog that will
                // tell them nothing. This is what lets the plugin speak for itself afterwards.
                Interactive = true,

                // A refusal on a named file cannot be explained: the dialog reports a failed
                // download with no reason, no numbers and no way to override, so writing what was
                // asked for - with the measured correction if one was found - is more useful than
                // declining. It applies only to a named file. On the automatic entry it would make
                // every candidate acceptable and rank them on nothing, which is the opposite of
                // what picking "best match" asks for; there, declining is the whole point.
                ApplyEvenIfUnverified = !auto
                    && (Plugin.Instance?.Configuration.NativePickerAppliesUnverified ?? true),
                UseMeasuredTransform = !auto,
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
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(entryId, fileName, url, itemPath, false),
            Options);

        return Base64Url.EncodeToString(payload);
    }

    /// <summary>
    /// Builds the identifier for the entry that stands for "choose for me".
    /// </summary>
    /// <param name="itemPath">The episode's media path, which is all that is needed.</param>
    /// <returns>The identifier.</returns>
    public static string EncodeAuto(string itemPath)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(0, string.Empty, string.Empty, itemPath, true),
            Options);

        return Base64Url.EncodeToString(payload);
    }

    public static (long EntryId, string FileName, string Url, string ItemPath, bool Auto) Decode(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var payload = JsonSerializer.Deserialize<Payload>(Base64Url.DecodeFromChars(id), Options)
            ?? throw new InvalidOperationException("The subtitle identifier could not be decoded.");

        return (payload.EntryId, payload.FileName, payload.Url, payload.ItemPath, payload.Auto);
    }

    private sealed record Payload(long EntryId, string FileName, string Url, string ItemPath, bool Auto);
}
