using MediaBrowser.Model.Plugins;
using Jellyfin.Plugin.Jimaku.Timing;

namespace Jellyfin.Plugin.Jimaku.Configuration;

/// <summary>
/// User-facing plugin settings.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the Jimaku API key, generated at <c>https://jimaku.cc/account</c>.
    /// Every Jimaku API endpoint requires one.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the language tag used for the sidecar filename. Jellyfin resolves
    /// <c>ja</c>, <c>jpn</c> and <c>Japanese</c> identically, all to the ISO 639-2 code <c>jpn</c>.
    /// </summary>
    public string LanguageTag { get; set; } = "jpn";

    /// <summary>Gets or sets a value indicating whether the scheduled library sweep is enabled.</summary>
    public bool EnableScheduledTask { get; set; }

    /// <summary>
    /// Gets or sets the library folder IDs the scheduled task is restricted to. Empty means every
    /// library, which is rarely what anyone wants on a mixed-content server.
    /// </summary>
    public string[] LibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the minimum baseline-corrected correlation required to accept an alignment.
    /// </summary>
    /// <remarks>
    /// Measured separation on synthetic tracks: correct matches score at or near 1.0 with a floor
    /// of 1.00 across 39 trials, while the best a wrong episode managed was 0.28. The default sits
    /// in that gap with room for the extra noise a VAD-derived reference introduces.
    /// </remarks>
    public double MinCorrelation { get; set; } = 0.50;

    /// <summary>
    /// Gets or sets the minimum correlation required when the comparison was made on cue starts
    /// rather than on how long cues stay on screen.
    /// </summary>
    /// <remarks>
    /// Onset signals are sparse - a short pulse per cue rather than a filled interval - so the same
    /// pair of subtitles scores far lower this way even when perfectly aligned. Measured on real
    /// files, correctly timed subtitles land around 0.44 to 0.56, while an unrelated episode scores
    /// about 0.04. Uniqueness does the heavy lifting on this path, cleanly separating roughly 5.0
    /// for a real match from 1.0 for a false one, so the correlation floor only has to exclude
    /// noise.
    /// </remarks>
    public double MinOnsetCorrelation { get; set; } = 0.25;

    /// <summary>
    /// Gets or sets the minimum peak-to-second-peak ratio required to accept an alignment.
    /// </summary>
    /// <remarks>
    /// This is the uniqueness test, and it is the measure that most cleanly separates a real match
    /// from a plausible-looking accident: correct matches measured 1.51 and above, wrong ones never
    /// exceeded 1.03.
    /// </remarks>
    public double MinPeakRatio { get; set; } = 1.20;

    /// <summary>
    /// Gets or sets the largest correction that will be applied, in seconds. A subtitle needing
    /// more than this is more likely the wrong file than a badly timed right one.
    /// </summary>
    public double MaxOffsetSeconds { get; set; } = 30;

    /// <summary>Gets or sets how far the alignment search looks for a peak, in seconds.</summary>
    public double MaxSearchOffsetSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the smallest correction worth applying, in seconds. Anything smaller is treated
    /// as already in sync and the file is written unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Guards against acting on measurement noise, not against small genuine offsets. When the
    /// correlation is high against a dense reference the measurement is reliable at this scale, and
    /// a fifth of a second is perceptible, so refusing to apply it leaves subtitles visibly early.
    /// </para>
    /// <para>
    /// Raise it towards 0.35 if corrections at this magnitude make things worse rather than better,
    /// which happens when the reference track carries a pronounced lead-in of its own and the
    /// subtitle was already well matched to the audio.
    /// </para>
    /// </remarks>
    public double MinCorrectionSeconds { get; set; } = 0.15;

    /// <summary>
    /// Gets or sets a value indicating whether an offset shared by most candidates is treated as
    /// the reference track's own timing and discounted rather than applied.
    /// </summary>
    /// <remarks>
    /// Several independently produced subtitles needing the identical correction is evidence about
    /// the reference, not about the subtitles. Turn this off to apply measured offsets verbatim.
    /// </remarks>
    public bool DetectReferenceBias { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether framerate ratios are tested.</summary>
    public bool EnableFramerateCorrection { get; set; } = true;

    /// <summary>Gets or sets the largest permitted deviation of the time scale from 1.0.</summary>
    public double MaxScaleDeviation { get; set; } = 0.1;

    /// <summary>
    /// Gets or sets a value indicating whether piecewise alignment may run when the on-demand
    /// action is used.
    /// </summary>
    public bool AllowPiecewiseOnDemand { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether piecewise alignment may run during the scheduled
    /// sweep. Off by default: it is the most permissive correction available, and an unattended
    /// batch job is the worst place to be permissive.
    /// </summary>
    public bool AllowPiecewiseScheduled { get; set; }

    /// <summary>Gets or sets the largest number of blocks a piecewise fit may use.</summary>
    public int MaxSplitBlocks { get; set; } = 4;

    /// <summary>Gets or sets the fewest cues a piecewise block must contain to be credible.</summary>
    public int MinCuesPerSplitBlock { get; set; } = 10;

    /// <summary>
    /// Gets or sets how much of the dialogue a differing-cut match must actually land on.
    /// </summary>
    /// <remarks>
    /// The check correlation cannot provide. The piecewise aligner has one free offset per section,
    /// so it raises correlation almost by construction - which is how a subtitle whose global fit
    /// was completely non-unique came back as a confident two-section match. A real cut, correctly
    /// split, puts nearly every reference cue next to a subtitle cue; sections fitted to noise do
    /// not, however well they correlate.
    /// </remarks>
    public double MinPiecewiseCoverage { get; set; } = 0.65;

    /// <summary>
    /// Gets or sets how much more of the dialogue a differing-cut match must land on than the
    /// single-offset fit it replaces.
    /// </summary>
    public double MinPiecewiseCoverageGain { get; set; } = 0.10;

    /// <summary>What to do when a framerate correction meets a file containing karaoke.</summary>
    public KaraokeScalePolicy KaraokePolicy { get; set; } = KaraokeScalePolicy.Rescale;

    /// <summary>Gets or sets a value indicating whether an existing sidecar may be overwritten.</summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether ZIP archives are considered when no plain subtitle
    /// file is offered. RAR and 7z are never opened.
    /// </summary>
    public bool AllowArchives { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether audio voice-activity analysis may be used when the
    /// media has no embedded subtitle track to align against.
    /// </summary>
    public bool EnableAudioFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets the path to a Silero VAD ONNX model. When set, and when the optional Silero
    /// assembly and ONNX Runtime are present alongside the plugin, this is used in preference to
    /// the built-in energy detector.
    /// </summary>
    public string SileroModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long to wait before retrying an episode the plugin previously declined,
    /// in days. Jimaku gains new uploads over time, so a decline is not permanent.
    /// </summary>
    public int RetryDeclinedAfterDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets how many candidates may be downloaded and timing-checked for one episode.
    /// </summary>
    /// <remarks>
    /// Candidates are tried in filename-match order, but the filename never excludes one. Jimaku
    /// uploads are named after the release they came from, which frequently has nothing in common
    /// with the local file's name, so a poorly scoring candidate is often the one that matches. The
    /// cap bounds the work without pre-judging the answer.
    /// </remarks>
    public int MaxCandidatesToTry { get; set; } = 8;

    /// <summary>
    /// Gets or sets a value indicating whether to push a short message to client sessions when an
    /// episode's subtitle changes.
    /// </summary>
    /// <remarks>
    /// Jellyfin's own subtitle dialog reports nothing after "download queued", and a plugin cannot
    /// change that. This is the substitute: the server tells the client directly once the work is
    /// actually done.
    /// </remarks>
    public bool ShowClientNotifications { get; set; } = true;

    /// <summary>
    /// Gets or sets how recently a session must have been active to be considered the one that
    /// asked for an interactive sync, in minutes.
    /// </summary>
    public int NotifyRecentMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets a value indicating whether outcomes are recorded in Dashboard - Activity.
    /// </summary>
    /// <remarks>
    /// Successes are always recorded when this is on. Declines are only recorded for interactive
    /// requests: an unattended sweep declines most of what it examines, because most episodes have
    /// nothing on Jimaku, and logging those would drown the feed.
    /// </remarks>
    public bool WriteActivityLog { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to prefer the release group that has already worked
    /// for other episodes of the same series.
    /// </summary>
    public bool UseSeriesPreference { get; set; } = true;

    /// <summary>
    /// Gets or sets how many deliberate picks a series needs before its release group is preferred.
    /// </summary>
    /// <remarks>
    /// Only files the user chose by hand count towards this. A subtitle the plugin selected on its
    /// own is not evidence about anything: letting automatic picks confirm the preference that
    /// produced them makes the preference self-fulfilling, hardening whatever the first run
    /// happened to land on. Because each confirmation is now a deliberate act rather than a
    /// by-product, two of them mean considerably more than three used to.
    /// </remarks>
    public int SeriesPreferenceMinConfirmations { get; set; } = 2;

    /// <summary>
    /// Gets or sets how much measured quality the series preference may override.
    /// </summary>
    /// <remarks>
    /// The quality score is dominated by coverage - the fraction of reference cues the subtitle
    /// also marks - so this is roughly "five percentage points of coverage". Past that the
    /// measurement is telling us something real about this particular episode, and a habit formed
    /// on earlier episodes should not be allowed to argue with it.
    /// </remarks>
    public double SeriesPreferenceTolerance { get; set; } = 0.05;

    /// <summary>
    /// Gets or sets a value indicating whether to prefer a subtitle released against the same kind
    /// of source as the local file - disc for a disc rip, broadcast for a web release.
    /// </summary>
    /// <remarks>
    /// The strongest available predictor of a differing cut, and the reason two subtitles for the
    /// same episode can want offsets a second apart. It only breaks a close call: a measurably
    /// better match from the other source still wins.
    /// </remarks>
    public bool PreferMatchingSource { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a subtitle picked in a client's own subtitle dialog
    /// is written even when its timing cannot be verified.
    /// </summary>
    /// <remarks>
    /// That dialog offers no way to explain a refusal: the download simply fails, with no reason
    /// and no alternative. Refusing there is therefore worse than refusing on the plugin's own
    /// page, where the numbers and a manual shift are both to hand. Picking a file by name is an
    /// explicit choice, so it is written - with the measured correction when one was found, which
    /// is more useful than writing it untouched.
    /// </remarks>
    public bool NativePickerAppliesUnverified { get; set; } = true;

    /// <summary>
    /// Gets or sets how much measured quality the source preference may override.
    /// </summary>
    public double SourcePreferenceTolerance { get; set; } = 0.05;

    /// <summary>
    /// Gets or sets how long a series' Jimaku entry list is reused before searching again, in
    /// hours. Zero disables the cache.
    /// </summary>
    /// <remarks>
    /// Every episode of a series resolves to the same entries, so searching per episode spends the
    /// 25-requests-per-minute budget re-asking a settled question. Caching it roughly halves the
    /// requests a sweep makes. The cost of staleness is bounded and small: a newly uploaded entry
    /// is picked up on the next expiry.
    /// </remarks>
    public int SeriesEntryCacheHours { get; set; } = 12;

    /// <summary>
    /// Gets or sets the most episodes one scheduled run will attempt. Zero means no limit.
    /// </summary>
    /// <remarks>
    /// Jimaku's rate limit is respected proactively, so a sweep cannot exceed it - but a large
    /// library can keep the limiter saturated for hours on its first run. Capping the run spreads
    /// that over successive days instead, and the history store means each day resumes where the
    /// last left off rather than starting over.
    /// </remarks>
    public int MaxEpisodesPerRun { get; set; } = 250;

    /// <summary>
    /// Gets or sets a limit on how recently an episode must have been added to the library to be
    /// swept, in days. Zero sweeps everything.
    /// </summary>
    /// <remarks>
    /// Once a library has been through one full pass, the only episodes worth revisiting are the
    /// new ones. Setting this turns the daily sweep into a watch for newly added content, which is
    /// both far cheaper and what most libraries actually need day to day.
    /// </remarks>
    public int OnlySweepEpisodesAddedWithinDays { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin removes the sidecar it is replacing.
    /// </summary>
    /// <remarks>
    /// Only ever a file the plugin recorded writing itself; a subtitle placed by hand is never
    /// touched. Without this, replacing a subtitle left the old one behind under a ".1." counter,
    /// so an episode accumulated files and the player could pick any of them - which is exactly
    /// what makes a re-download look like it did nothing.
    /// </remarks>
    public bool RemoveSupersededSidecars { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether written subtitles carry a comment naming the Jimaku
    /// file they came from.
    /// </summary>
    /// <remarks>
    /// The sidecar's own filename is dictated by Jellyfin's resolver and cannot carry it, so
    /// without this there is nothing on disk that distinguishes one upload's timing from another's.
    /// ASS only; SubRip has no comment syntax.
    /// </remarks>
    public bool StampProvenance { get; set; } = true;
}
