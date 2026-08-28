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
                },
                cancellationToken).ConfigureAwait(false);

            return Ok(ToDto(result));
        }
        catch (JimakuApiException ex)
        {
            logger.LogWarning(ex, "Syncing {Name} failed.", episode.Name);
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
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
            },
            cancellationToken).ConfigureAwait(false);

        return Ok(ToDto(result));
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

    private static CandidateDto ToDto(SubtitleCandidate candidate) => new()
    {
        EntryId = candidate.EntryId,
        EntryName = candidate.EntryName,
        FileName = candidate.File.Name,
        Url = candidate.File.Url,
        Size = candidate.File.Size,
        NameScore = candidate.NameMatch.Score,
        NameNotes = candidate.NameMatch.Notes,
        Usable = candidate.IsUsable,
        RejectedBecause = candidate.NameMatch.EpisodeMismatch
            ? candidate.NameMatch.Notes
            : new Matching.FilteredCandidate(candidate.File, candidate.Rejection).Explain(),
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
