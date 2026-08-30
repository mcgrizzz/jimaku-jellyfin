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
    SweepRunner sweepRunner,
    SeriesProfileStore profiles,
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
                UseMeasuredTransform = body.UseMeasuredTransform,
                ManualOffsetSeconds = body.ManualOffsetSeconds,
                ManualEndOffsetSeconds = body.ManualEndOffsetSeconds,
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
    /// Reports what an episode's subtitles are compared against, and why that was chosen.
    /// </summary>
    /// <remarks>
    /// The reference is the usual explanation for a run of declines, and it was previously
    /// invisible: a decline named the method but not the track, so there was no way to tell an
    /// episode with no readable subtitle tracks from one where a signs track had been picked.
    /// </remarks>
    /// <param name="itemId">The episode's item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account.</returns>
    [HttpGet("Jellyfin.Plugin.Jimaku/Episodes/{itemId}/Reference")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReferenceReportDto>> GetReference(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        if (libraryManager.GetItemById(itemId) is not Episode episode)
        {
            return NotFound("That item is not an episode.");
        }

        var report = await syncService.ExplainReferenceAsync(episode, cancellationToken).ConfigureAwait(false);

        return Ok(new ReferenceReportDto
        {
            Chosen = report.Chosen,
            FromSubtitles = report.FromSubtitles,
            Note = report.Explain() is { Length: > 0 } explanation ? explanation : report.Note,
            AudioTrack = report.AudioTrack,
            Detector = report.Detector,
            Cues = report.Cues,
            MedianCueSeconds = report.MedianCueSeconds,
            DutyCycle = report.DutyCycle,
            SampleCues = report.SampleCues,
            Streams = report.Streams.Select(s => new ReferenceStreamDto
            {
                Index = s.Index,
                Codec = s.Codec,
                Language = s.Language,
                Title = s.Title,
                IsForced = s.IsForced,
                IsText = s.IsText,
                CueCount = s.CueCount,
                Used = s.Used,
                Status = s.Status,
            }).ToList(),
        });
    }

    /// <summary>
    /// Reports which release group a series has settled on, and how much stands behind it.
    /// </summary>
    /// <param name="seriesId">The series' item ID.</param>
    /// <returns>The learned preference.</returns>
    [HttpGet("Jellyfin.Plugin.Jimaku/Series/{seriesId}/Preference")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SeriesPreferenceDto> GetSeriesPreference([FromRoute] Guid seriesId)
    {
        var required = Plugin.Instance?.Configuration.SeriesPreferenceMinConfirmations ?? 2;
        var profile = profiles.Get(seriesId);

        return Ok(new SeriesPreferenceDto
        {
            ReleaseGroup = profile?.PreferredReleaseGroup ?? string.Empty,
            Confirmations = profile?.Confirmations ?? 0,
            Required = required,
            InUse = profile is not null && profile.Confirmations >= required && profile.PreferredReleaseGroup.Length > 0,
            UpdatedUtc = profile?.UpdatedUtc,
        });
    }

    /// <summary>
    /// Forgets what a series has learned about release groups.
    /// </summary>
    /// <param name="seriesId">The series' item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The preference after resetting.</returns>
    [HttpPost("Jellyfin.Plugin.Jimaku/Series/{seriesId}/ResetPreference")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SeriesPreferenceDto>> ResetSeriesPreference(
        [FromRoute] Guid seriesId,
        CancellationToken cancellationToken)
    {
        await profiles.ResetPreferenceAsync(seriesId, cancellationToken).ConfigureAwait(false);
        return GetSeriesPreference(seriesId);
    }

    /// <summary>
    /// Sweeps a chosen series, season or set of episodes.
    /// </summary>
    /// <remarks>
    /// The scheduled task can only be pointed at whole libraries, which is the wrong granularity
    /// for "I just added a season and want subtitles for it". This runs the same pipeline over
    /// whatever you name, in the background, reporting into the same live status.
    /// </remarks>
    /// <param name="body">What to sweep.</param>
    /// <returns>The status of the run that was started.</returns>
    [HttpPost("Jellyfin.Plugin.Jimaku/Sweep")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<SweepStatusDto> StartSweep([FromBody] SweepRequest body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            return BadRequest("No Jimaku API key is configured.");
        }

        if (sweepRunner.Progress.IsRunning)
        {
            // Jimaku's budget is per-IP and shared, so a second sweep would only take turns waiting
            // on the same limiter while making the progress reporting incoherent.
            return Conflict("A sweep is already running.");
        }

        var label = "the selected episodes";
        var ancestors = new List<Guid>();

        if (body.ParentId is { } parentId && parentId != Guid.Empty)
        {
            var parent = libraryManager.GetItemById(parentId);
            if (parent is null)
            {
                return NotFound("That series or season could not be found.");
            }

            ancestors.Add(parentId);
            label = parent.Name ?? "a series";
        }

        var scope = new SweepScope
        {
            AncestorIds = ancestors,
            EpisodeIds = body.EpisodeIds,
            OnlyMissingSubtitles = body.OnlyMissingSubtitles,
            RespectHistory = body.RespectHistory,
            Label = label,
        };

        var options = new SyncOptions
        {
            AllowPiecewise = configuration.AllowPiecewiseOnDemand,
            AllowAudioFallback = configuration.EnableAudioFallback,

            // A bulk run is not a per-episode decision by the user, so it neither notifies as one
            // nor teaches the series preference anything.
            Interactive = false,
        };

        // Detached deliberately: the request's cancellation token dies with the response, and this
        // run outlives it. Cancellation goes through the progress object instead.
        var cancellation = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await sweepRunner.RunAsync(scope, options, null, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The on-demand sweep failed.");
            }
            finally
            {
                cancellation.Dispose();
            }
        });

        return Ok(ToDto(sweepRunner.Progress));
    }

    /// <summary>
    /// Reports what the running sweep is doing.
    /// </summary>
    /// <returns>The live status.</returns>
    [HttpGet("Jellyfin.Plugin.Jimaku/Sweep/Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SweepStatusDto> SweepStatus() => Ok(ToDto(sweepRunner.Progress));

    /// <summary>
    /// Asks the running sweep to stop.
    /// </summary>
    /// <returns>The status after asking.</returns>
    [HttpPost("Jellyfin.Plugin.Jimaku/Sweep/Cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<SweepStatusDto> CancelSweep()
    {
        sweepRunner.Progress.Cancel();
        return Ok(ToDto(sweepRunner.Progress));
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

    private static SweepStatusDto ToDto(SweepProgress progress) => new()
    {
        IsRunning = progress.IsRunning,
        Scope = progress.Scope,
        CurrentEpisode = progress.CurrentEpisode,
        Completed = progress.Completed,
        Total = progress.Total,
        Applied = progress.Applied,
        Declined = progress.Declined,
        Skipped = progress.Skipped,
        Conclusion = progress.Conclusion,
        StartedUtc = progress.StartedUtc,
        Outcomes = progress.Outcomes.Select(o => new SweepOutcomeDto
        {
            EpisodeId = o.EpisodeId,
            Name = o.Name,
            Applied = o.Applied,
            Verdict = o.Verdict,
            FileName = o.FileName,
            Message = o.Message,
        }).ToList(),
    };

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
