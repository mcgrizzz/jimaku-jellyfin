using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Api.Dtos;
using Jellyfin.Plugin.Jimaku.Jimaku;
using Jellyfin.Plugin.Jimaku.Jimaku.Models;
using Jellyfin.Plugin.Jimaku.Sync;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Api;

/// <summary>
/// On-demand endpoints backing the plugin's settings page.
/// </summary>
/// <remarks>
/// Controllers in a plugin assembly need no registration: the server scans loaded plugin assemblies
/// for <see cref="ControllerBase"/> types. Routes are prefixed with the assembly name, following the
/// convention the first-party plugins use, so they cannot collide with core routes.
/// </remarks>
[ApiController]
[Produces(MediaTypeNames.Application.Json)]
[Authorize(Policy = Policies.SubtitleManagement)]
public class JimakuController(
    JimakuSyncService syncService,
    JimakuApiClient apiClient,
    ILibraryManager libraryManager,
    ILogger<JimakuController> logger) : ControllerBase
{
    /// <summary>
    /// Lists the Jimaku files available for an episode, without downloading or analysing any.
    /// </summary>
    /// <param name="itemId">The episode's item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates, best filename match first.</returns>
    [HttpGet("Jellyfin.Plugin.Jimaku/Episodes/{itemId}/Candidates")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<CandidateDto>>> GetCandidates(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        if (libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return NotFound("No episode with that ID.");
        }

        try
        {
            var candidates = await syncService.FindCandidatesAsync(episode, cancellationToken).ConfigureAwait(false);
            return Ok(candidates.Select(ToDto).ToList());
        }
        catch (JimakuApiException ex)
        {
            logger.LogWarning(ex, "Listing Jimaku candidates for {Name} failed.", episode.Name);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected failure listing candidates for {Name}.", episode.Name);
            return StatusCode(StatusCodes.Status500InternalServerError, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs the full pipeline for one episode: pick the best candidate, verify its timing, correct
    /// it if needed, and attach it.
    /// </summary>
    /// <param name="itemId">The episode's item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened, including why nothing was written when that is the outcome.</returns>
    [HttpPost("Jellyfin.Plugin.Jimaku/Episodes/{itemId}/Auto")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SyncResultDto>> Auto(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        if (libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return NotFound("No episode with that ID.");
        }

        var configuration = Plugin.Instance?.Configuration;

        try
        {
            var result = await syncService.SyncEpisodeAsync(
                episode,
                new SyncOptions
                {
                    AllowPiecewise = configuration?.AllowPiecewiseOnDemand ?? true,
                    AllowAudioFallback = configuration?.EnableAudioFallback ?? true,
                    Interactive = true,
                },
                cancellationToken).ConfigureAwait(false);

            return Ok(ToDto(result));
        }
        catch (JimakuApiException ex)
        {
            logger.LogWarning(ex, "Syncing {Name} failed.", episode.Name);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (Exception ex)
        {
            // The settings page shows this text, so a readable sentence beats a stack trace.
            logger.LogError(ex, "Unexpected failure syncing {Name}.", episode.Name);
            return StatusCode(StatusCodes.Status500InternalServerError, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a specific candidate the user picked, still verifying its timing unless they have
    /// explicitly asked to bypass that.
    /// </summary>
    /// <param name="itemId">The episode's item ID.</param>
    /// <param name="body">The candidate to apply.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Jellyfin.Plugin.Jimaku/Episodes/{itemId}/Apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SyncResultDto>> Apply(
        [FromRoute] Guid itemId,
        [FromBody] ApplyRequest body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(body.Url))
        {
            return BadRequest("A download URL is required.");
        }

        if (libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return NotFound("No episode with that ID.");
        }

        var configuration = Plugin.Instance?.Configuration;

        var result = await syncService.SyncEpisodeAsync(
            episode,
            new SyncOptions
            {
                AllowPiecewise = configuration?.AllowPiecewiseOnDemand ?? true,
                AllowAudioFallback = configuration?.EnableAudioFallback ?? true,
                ForcedEntryId = body.EntryId,
                ForcedFile = new JimakuFile { Name = body.FileName, Url = body.Url },
                ApplyEvenIfUnverified = body.ApplyEvenIfUnverified,
                Interactive = true,
            },
            cancellationToken).ConfigureAwait(false);

        return Ok(ToDto(result));
    }

    /// <summary>
    /// Reports what the plugin has attached to an episode, and what it has already tried.
    /// </summary>
    /// <remarks>
    /// The sidecar's filename is derived from the media file, so nothing on disk records which
    /// Jimaku upload it came from. This does.
    /// </remarks>
    /// <param name="itemId">The episode's item ID.</param>
    /// <returns>The recorded history.</returns>
    [HttpGet("Jellyfin.Plugin.Jimaku/Episodes/{itemId}/History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<EpisodeHistoryDto> GetHistory([FromRoute] Guid itemId)
    {
        if (libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return NotFound("That item is not an episode.");
        }

        return Ok(BuildHistory(episode));
    }

    /// <summary>
    /// Throws away the subtitle currently attached to an episode.
    /// </summary>
    /// <remarks>
    /// Removes the file, records the rejection so automatic selection stops offering it, and takes
    /// back the credit it gave the series' release-group preference.
    /// </remarks>
    /// <param name="itemId">The episode's item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated history.</returns>
    [HttpPost("Jellyfin.Plugin.Jimaku/Episodes/{itemId}/Reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EpisodeHistoryDto>> Reject(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        if (libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return NotFound("That item is not an episode.");
        }

        var rejected = await syncService.RejectCurrentAsync(episode, cancellationToken).ConfigureAwait(false);
        if (rejected is null)
        {
            return NotFound("This plugin has not attached a subtitle to that episode.");
        }

        return Ok(BuildHistory(episode));
    }

    /// <summary>
    /// Puts previously rejected files back on the table for an episode.
    /// </summary>
    /// <param name="itemId">The episode's item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated history.</returns>
    [HttpPost("Jellyfin.Plugin.Jimaku/Episodes/{itemId}/ClearRejections")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EpisodeHistoryDto>> ClearRejections(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        if (libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return NotFound("That item is not an episode.");
        }

        await syncService.ClearRejectionsAsync(episode, cancellationToken).ConfigureAwait(false);
        return Ok(BuildHistory(episode));
    }

    /// <summary>
    /// Checks that a Jimaku API key is accepted.
    /// </summary>
    /// <param name="body">The key to test, or empty to test the saved one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the key works.</returns>
    [HttpPost("Jellyfin.Plugin.Jimaku/ValidateApiKey")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<bool>> ValidateApiKey(
        [FromBody] ValidateApiKeyRequest body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        var key = string.IsNullOrWhiteSpace(body.ApiKey)
            ? Plugin.Instance?.Configuration.ApiKey ?? string.Empty
            : body.ApiKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            return Ok(false);
        }

        try
        {
            return Ok(await apiClient.ValidateApiKeyAsync(key, cancellationToken).ConfigureAwait(false));
        }
        catch (JimakuApiException ex)
        {
            logger.LogDebug(ex, "Validating the Jimaku API key failed.");
            return Ok(false);
        }
    }

    private EpisodeHistoryDto BuildHistory(Episode episode)
    {
        var entry = syncService.GetHistory(episode);
        var languageTag = Plugin.Instance?.Configuration.LanguageTag ?? "jpn";

        // Both halves matter and they can disagree: the record says what the plugin believes is
        // attached, the disk says what actually is. A file removed outside the plugin shows up as
        // exactly that difference.
        var onDisk = syncService.FindSidecars(episode, languageTag);

        if (entry is null)
        {
            return new EpisodeHistoryDto { SidecarsOnDisk = onDisk };
        }

        var attempts = entry.Attempts
            .Select(ToDto)
            .Reverse()
            .ToList();

        return new EpisodeHistoryDto
        {
            Current = attempts.Find(a => string.Equals(a.Status, nameof(AttemptStatus.Applied), StringComparison.Ordinal)),
            Attempts = attempts,
            RejectedFileNames = entry.RejectedFileNames,
            SidecarsOnDisk = onDisk,
        };
    }

    private static AttemptDto ToDto(SyncAttempt attempt) => new()
    {
        AttemptedUtc = attempt.AttemptedUtc,
        Status = attempt.Status.ToString(),
        Verdict = attempt.Verdict.ToString(),
        FileName = attempt.FileName,
        ReleaseGroup = attempt.ReleaseGroup,
        EntryId = attempt.EntryId,
        SidecarPath = attempt.SidecarPath,
        Correction = new Timing.TimingTransform(attempt.Scale, attempt.OffsetSeconds).Describe(),
        Correlation = attempt.Correlation,
        Reason = attempt.Reason,
    };

    private static CandidateDto ToDto(SubtitleCandidate candidate) => new()
    {
        EntryId = candidate.EntryId,
        EntryName = candidate.EntryName,
        EntryNotes = candidate.EntryNotes,
        EntryUnverified = candidate.EntryUnverified,
        FileName = candidate.File.Name,
        Url = candidate.File.Url,
        Size = candidate.File.Size,
        NameScore = candidate.NameMatch.Score,
        NameNotes = candidate.NameMatch.Notes,
        Usable = candidate.IsUsable,
        PreviouslyRejected = candidate.PreviouslyRejected,
        ReleaseGroup = candidate.ReleaseGroup ?? string.Empty,
        RejectedBecause = candidate.NameMatch.EpisodeMismatch
            ? candidate.NameMatch.Notes
            : new Matching.FilteredCandidate(candidate.File, candidate.Rejection).Explain(),

        // Populated only for candidates that were actually downloaded and measured.
        Verdict = candidate.Alignment?.Verdict.ToString(),
        Correlation = candidate.Alignment?.Correlation,
        PeakRatio = candidate.Alignment?.PeakRatio,
        Correction = candidate.Alignment?.Transform.Describe(),
        TimingNotes = candidate.Alignment?.Reason,
        ReferenceBiasSeconds = candidate.Alignment?.ReferenceBiasSeconds,
        Coverage = candidate.Alignment?.Coverage,
        OnScreenRatio = candidate.Alignment?.OnScreenRatio,
    };

    private static SyncResultDto ToDto(SyncResult result) => new()
    {
        Applied = result.Applied,
        Verdict = result.Verdict.ToString(),
        Message = result.Message,
        FileName = result.FileName,
        SidecarPath = result.SidecarPath,
        ReferenceSource = result.ReferenceSource,
        Correction = result.Transform.Describe(),
        OffsetSeconds = result.Transform.OffsetSeconds,
        Scale = result.Transform.Scale,
        Correlation = result.Correlation,
        PeakRatio = result.PeakRatio,
        Candidates = result.Candidates.Select(ToDto).ToList(),
    };
}
