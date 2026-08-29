using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jimaku.Jimaku.Models;
using Jellyfin.Plugin.Jimaku.Matching;
using Jellyfin.Plugin.Jimaku.Timing;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// A Jimaku file considered for one episode, with everything known about it so far.
/// </summary>
public sealed class SubtitleCandidate
{
    /// <summary>Gets or sets the Jimaku entry the file belongs to.</summary>
    public long EntryId { get; set; }

    /// <summary>Gets or sets the entry name, for display.</summary>
    public string EntryName { get; set; } = string.Empty;

    /// <summary>Gets or sets editor notes on the entry, which often name the target release.</summary>
    public string EntryNotes { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the entry is flagged unverified.</summary>
    public bool EntryUnverified { get; set; }

    /// <summary>Gets or sets the file.</summary>
    public JimakuFile File { get; set; } = new JimakuFile();

    /// <summary>Gets or sets why the file was discarded, if it was.</summary>
    public RejectionReason Rejection { get; set; }

    /// <summary>Gets or sets the filename match against the local video.</summary>
    public NameMatch NameMatch { get; set; }

    /// <summary>Gets or sets what the filename suggests about the languages inside.</summary>
    public SubtitleLanguages Languages { get; set; }

    /// <summary>
    /// Gets or sets the release group parsed from the file name, when it names one. Kept here so
    /// the series preference can be consulted without re-parsing, and so the UI can show it.
    /// </summary>
    public string? ReleaseGroup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this file was attached to this episode before and
    /// then thrown away. Automatic selection skips these; an explicit pick still overrides it.
    /// </summary>
    public bool PreviouslyRejected { get; set; }

    /// <summary>
    /// Gets or sets the timing verdict, populated only once the file has actually been downloaded
    /// and analysed. Listing candidates deliberately skips this so the UI stays responsive.
    /// </summary>
    public AlignmentResult? Alignment { get; set; }

    /// <summary>Gets a value indicating whether the file survived filtering.</summary>
    public bool IsUsable => Rejection == RejectionReason.None && !NameMatch.EpisodeMismatch;
}

/// <summary>
/// The outcome of a subtitle sync attempt for one episode.
/// </summary>
public sealed class SyncResult
{
    /// <summary>Gets or sets a value indicating whether a subtitle was written.</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets the verdict reached.</summary>
    public SyncVerdict Verdict { get; set; } = SyncVerdict.Unknown;

    /// <summary>Gets or sets a message suitable for showing to the user.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the sidecar written, if any.</summary>
    public string? SidecarPath { get; set; }

    /// <summary>Gets or sets the file that was chosen, if any.</summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the Jimaku entry the chosen file came from. Recorded so the series profile can
    /// learn which entry keeps working for a series whose filenames name no release group.
    /// </summary>
    public long EntryId { get; set; }

    /// <summary>Gets or sets the release group of the chosen file, when its name gave one.</summary>
    public string ReleaseGroup { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the user picked this file, as opposed to the plugin
    /// selecting it. Only a user's choice is allowed to teach the series preference anything.
    /// </summary>
    public bool UserChosen { get; set; }

    /// <summary>Gets or sets how the timing reference was obtained.</summary>
    public string ReferenceSource { get; set; } = string.Empty;

    /// <summary>Gets or sets the correction that was applied.</summary>
    public TimingTransform Transform { get; set; } = TimingTransform.Identity;

    /// <summary>Gets or sets the correlation achieved.</summary>
    public double Correlation { get; set; }

    /// <summary>Gets or sets the peak-to-second-peak ratio achieved.</summary>
    public double PeakRatio { get; set; }

    /// <summary>Gets or sets every candidate considered, so a decline can be acted on manually.</summary>
    public IReadOnlyList<SubtitleCandidate> Candidates { get; set; } = Array.Empty<SubtitleCandidate>();

    /// <summary>
    /// Gets or sets the corrected subtitle text. Populated when the caller asked not to have the
    /// sidecar written, so it can hand the content somewhere else instead.
    /// </summary>
    public string? Content { get; set; }

    /// <summary>Gets or sets the file extension the corrected content should be saved with.</summary>
    public string? Extension { get; set; }

    /// <summary>Creates a failed result carrying an explanation.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="verdict">The verdict to record.</param>
    /// <returns>The result.</returns>
    public static SyncResult Fail(string message, SyncVerdict verdict = SyncVerdict.Declined) => new()
    {
        Applied = false,
        Verdict = verdict,
        Message = message,
    };
}

/// <summary>
/// Options for a single sync attempt.
/// </summary>
public sealed class SyncOptions
{
    /// <summary>Gets or sets a value indicating whether differing-cut correction may be attempted.</summary>
    public bool AllowPiecewise { get; set; }

    /// <summary>Gets or sets a value indicating whether audio analysis may be used as a reference.</summary>
    public bool AllowAudioFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets a specific file to apply, bypassing automatic selection. Set when the user has
    /// picked a candidate themselves.
    /// </summary>
    public JimakuFile? ForcedFile { get; set; }

    /// <summary>Gets or sets the Jimaku entry the forced file belongs to.</summary>
    public long ForcedEntryId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a forced file is written even if its timing cannot
    /// be verified. Only ever set from an explicit user action.
    /// </summary>
    public bool ApplyEvenIfUnverified { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to write the sidecar and refresh the item.
    /// </summary>
    /// <remarks>
    /// Set false when running inside Jellyfin's own subtitle provider flow: core's
    /// <c>SubtitleManager</c> saves whatever the provider returns and refreshes the item itself, so
    /// writing here as well would leave two copies of the same subtitle side by side.
    /// </remarks>
    public bool WriteSidecar { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a person is waiting on this.
    /// </summary>
    /// <remarks>
    /// Changes only how the outcome is reported, never what it is. An interactive request may
    /// notify whoever is currently at a screen and records its declines in the activity feed;
    /// an unattended sweep does neither, because nobody asked and most of its declines are simply
    /// episodes Jimaku has nothing for.
    /// </remarks>
    public bool Interactive { get; set; }
}
