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
}
