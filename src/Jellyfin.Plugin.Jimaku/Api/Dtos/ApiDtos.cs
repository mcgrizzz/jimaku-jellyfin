using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Jimaku.Api.Dtos;

/// <summary>Request body for validating an API key.</summary>
public class ValidateApiKeyRequest
{
    /// <summary>Gets or sets the key to test. Empty means test the saved key.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>A candidate subtitle, as shown in the settings page.</summary>
public class CandidateDto
{
    /// <summary>Gets or sets the Jimaku entry ID.</summary>
    public long EntryId { get; set; }

    /// <summary>Gets or sets the entry name.</summary>
    public string EntryName { get; set; } = string.Empty;

    /// <summary>Gets or sets editor notes on the entry.</summary>
    public string EntryNotes { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the entry is flagged unverified.</summary>
    public bool EntryUnverified { get; set; }

    /// <summary>Gets or sets the file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the download URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the filename match score out of 100.</summary>
    public int NameScore { get; set; }

    /// <summary>Gets or sets an explanation of the filename match.</summary>
    public string NameNotes { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the file can be used.</summary>
    public bool Usable { get; set; }

    /// <summary>Gets or sets why the file was rejected, if it was.</summary>
    public string RejectedBecause { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this file was attached to this episode before and
    /// then thrown away. Automatic selection skips it; picking it explicitly still works.
    /// </summary>
    public bool PreviouslyRejected { get; set; }

    /// <summary>Gets or sets the release group parsed from the file name, if it named one.</summary>
    public string ReleaseGroup { get; set; } = string.Empty;

    /// <summary>Gets or sets the timing verdict, when this candidate was actually measured.</summary>
    public string? Verdict { get; set; }

    /// <summary>Gets or sets the measured correlation, when this candidate was measured.</summary>
    public double? Correlation { get; set; }

    /// <summary>Gets or sets the measured uniqueness, when this candidate was measured.</summary>
    public double? PeakRatio { get; set; }

    /// <summary>Gets or sets the correction that would be applied, when measured.</summary>
    public string? Correction { get; set; }

    /// <summary>Gets or sets why this candidate's timing was accepted or rejected.</summary>
    public string? TimingNotes { get; set; }

    /// <summary>Gets or sets an offset attributed to the reference track and discounted.</summary>
    public double? ReferenceBiasSeconds { get; set; }

    /// <summary>Gets or sets the fraction of reference cues this subtitle also marks.</summary>
    public double? Coverage { get; set; }

    /// <summary>Gets or sets the share of runtime this subtitle has something on screen.</summary>
    public double? OnScreenRatio { get; set; }
}

/// <summary>The result of a sync attempt.</summary>
public class SyncResultDto
{
    /// <summary>Gets or sets a value indicating whether a subtitle was written.</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets the verdict name.</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>Gets or sets a message for the user.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the file that was used.</summary>
    public string? FileName { get; set; }

    /// <summary>Gets or sets the sidecar that was written.</summary>
    public string? SidecarPath { get; set; }

    /// <summary>Gets or sets how the timing reference was obtained.</summary>
    public string ReferenceSource { get; set; } = string.Empty;

    /// <summary>Gets or sets the correction applied, in words.</summary>
    public string Correction { get; set; } = string.Empty;

    /// <summary>Gets or sets the offset applied, in seconds.</summary>
    public double OffsetSeconds { get; set; }

    /// <summary>Gets or sets the time scale applied.</summary>
    public double Scale { get; set; }

    /// <summary>Gets or sets the correlation achieved.</summary>
    public double Correlation { get; set; }

    /// <summary>Gets or sets the peak-to-second-peak ratio achieved.</summary>
    public double PeakRatio { get; set; }

    /// <summary>Gets or sets every candidate considered.</summary>
    public IReadOnlyList<CandidateDto> Candidates { get; set; } = Array.Empty<CandidateDto>();
}

/// <summary>Request body for applying a specific candidate.</summary>
public class ApplyRequest
{
    /// <summary>Gets or sets the Jimaku entry ID.</summary>
    public long EntryId { get; set; }

    /// <summary>Gets or sets the file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the download URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to write the file even when its timing cannot be
    /// verified. Only ever set by an explicit user choice.
    /// </summary>
    public bool ApplyEvenIfUnverified { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to apply the measured correction when writing an
    /// unverified file, rather than writing it unchanged.
    /// </summary>
    public bool UseMeasuredTransform { get; set; }

    /// <summary>Gets or sets an exact shift to apply, in seconds, bypassing measurement entirely.</summary>
    public double? ManualOffsetSeconds { get; set; }
}

/// <summary>
/// One thing that was tried for an episode.
/// </summary>
public class AttemptDto
{
    /// <summary>Gets or sets when it was tried.</summary>
    public DateTimeOffset AttemptedUtc { get; set; }

    /// <summary>Gets or sets how it ended up: applied, superseded, rejected or declined.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the timing verdict.</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jimaku file name, which the sidecar's own name does not preserve.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the release group, if the name gave one.</summary>
    public string ReleaseGroup { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jimaku entry it came from.</summary>
    public long EntryId { get; set; }

    /// <summary>Gets or sets the sidecar that was written, if any.</summary>
    public string SidecarPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the correction applied, for display.</summary>
    public string Correction { get; set; } = string.Empty;

    /// <summary>Gets or sets the correlation achieved.</summary>
    public double Correlation { get; set; }

    /// <summary>Gets or sets the explanation.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// What the plugin has done to one episode, and what it has been told not to do again.
/// </summary>
public class EpisodeHistoryDto
{
    /// <summary>Gets or sets the subtitle currently attached by this plugin, if any.</summary>
    public AttemptDto? Current { get; set; }

    /// <summary>Gets or sets everything that has been tried, newest first.</summary>
    public IReadOnlyList<AttemptDto> Attempts { get; set; } = Array.Empty<AttemptDto>();

    /// <summary>Gets or sets the file names automatic selection will now skip.</summary>
    public IReadOnlyList<string> RejectedFileNames { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the sidecar files presently on disk for this episode.</summary>
    public IReadOnlyList<string> SidecarsOnDisk { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Request to sweep a chosen part of the library.
/// </summary>
public class SweepRequest
{
    /// <summary>Gets or sets a series or season to sweep. Every episode beneath it is covered.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Gets or sets specific episodes to attempt, in place of a parent.</summary>
    public IReadOnlyList<Guid> EpisodeIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets a value indicating whether to skip episodes that already have a Japanese track.
    /// </summary>
    public bool OnlyMissingSubtitles { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to skip episodes already settled or recently declined.
    /// </summary>
    public bool RespectHistory { get; set; }
}

/// <summary>One episode's outcome during a sweep.</summary>
public class SweepOutcomeDto
{
    /// <summary>Gets or sets the episode.</summary>
    public Guid EpisodeId { get; set; }

    /// <summary>Gets or sets a display name for it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a subtitle was attached.</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets the verdict reached.</summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>Gets or sets the file that was used, when one was.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the explanation.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Live state of the running sweep, which the Scheduled Tasks view cannot show.
/// </summary>
public class SweepStatusDto
{
    /// <summary>Gets or sets a value indicating whether a sweep is running.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Gets or sets what the run covers.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode currently being worked on.</summary>
    public string CurrentEpisode { get; set; } = string.Empty;

    /// <summary>Gets or sets how many episodes have been dealt with.</summary>
    public int Completed { get; set; }

    /// <summary>Gets or sets how many episodes the run covers.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets how many subtitles were attached.</summary>
    public int Applied { get; set; }

    /// <summary>Gets or sets how many episodes were declined.</summary>
    public int Declined { get; set; }

    /// <summary>Gets or sets how many episodes were skipped.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets how the run ended, once it has.</summary>
    public string Conclusion { get; set; } = string.Empty;

    /// <summary>Gets or sets when the run started.</summary>
    public DateTimeOffset? StartedUtc { get; set; }

    /// <summary>Gets or sets the outcomes so far, newest first.</summary>
    public IReadOnlyList<SweepOutcomeDto> Outcomes { get; set; } = Array.Empty<SweepOutcomeDto>();
}

/// <summary>
/// What a series has learned about which release group to prefer.
/// </summary>
public class SeriesPreferenceDto
{
    /// <summary>Gets or sets the preferred release group, or empty when none is established.</summary>
    public string ReleaseGroup { get; set; } = string.Empty;

    /// <summary>Gets or sets how many deliberate picks stand behind it.</summary>
    public int Confirmations { get; set; }

    /// <summary>Gets or sets how many are needed before it is used.</summary>
    public int Required { get; set; }

    /// <summary>Gets or sets a value indicating whether it is currently strong enough to be applied.</summary>
    public bool InUse { get; set; }

    /// <summary>Gets or sets when it last changed.</summary>
    public DateTimeOffset? UpdatedUtc { get; set; }
}

/// <summary>One embedded subtitle stream considered as a timing reference.</summary>
public class ReferenceStreamDto
{
    /// <summary>Gets or sets the stream index within the container.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the codec.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets the language tag.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets the stream title, which is what usually reveals a signs track.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the container flags it forced.</summary>
    public bool IsForced { get; set; }

    /// <summary>Gets or sets whether the track carries readable text rather than pictures.</summary>
    public bool IsText { get; set; }

    /// <summary>Gets or sets how many cues were read, when it was read.</summary>
    public int CueCount { get; set; }

    /// <summary>Gets or sets a value indicating whether this stream was used.</summary>
    public bool Used { get; set; }

    /// <summary>Gets or sets what happened to it.</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// How the timing reference for an episode was arrived at.
/// </summary>
public class ReferenceReportDto
{
    /// <summary>Gets or sets what was used, or empty when nothing was.</summary>
    public string Chosen { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it came from embedded subtitles.</summary>
    public bool FromSubtitles { get; set; }

    /// <summary>Gets or sets an explanation of the outcome.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Gets or sets the audio stream analysed, when it came to that.</summary>
    public string AudioTrack { get; set; } = string.Empty;

    /// <summary>Gets or sets the voice activity detector used, when one was.</summary>
    public string Detector { get; set; } = string.Empty;

    /// <summary>Gets or sets every subtitle stream that was considered.</summary>
    public IReadOnlyList<ReferenceStreamDto> Streams { get; set; } = Array.Empty<ReferenceStreamDto>();
}
