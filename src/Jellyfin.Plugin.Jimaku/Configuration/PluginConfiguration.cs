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
}
