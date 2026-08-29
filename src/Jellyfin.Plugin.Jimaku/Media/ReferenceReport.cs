using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Media;

/// <summary>
/// One subtitle stream that was considered as a timing reference, and what became of it.
/// </summary>
public sealed class ReferenceStreamInfo
{
    /// <summary>Gets or sets the stream index within the container.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the codec.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets the language tag, or "und".</summary>
    public string Language { get; set; } = "und";

    /// <summary>Gets or sets the stream title, which is what usually reveals a signs track.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the container flags it forced.</summary>
    public bool IsForced { get; set; }

    /// <summary>Gets or sets how many cues were read, when it was read at all.</summary>
    public int CueCount { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the stream that was used.</summary>
    public bool Used { get; set; }

    /// <summary>Gets or sets what happened to it, in plain language.</summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// A full account of how the timing reference for one episode was arrived at.
/// </summary>
/// <remarks>
/// Written because the answer was previously unobtainable. A decline said only "reference:
/// band-energy voice activity", which does not say whether the file had embedded subtitles at all,
/// which of them were considered, or why none was used - so a decline was indistinguishable from a
/// misconfiguration, and a reasonable first guess was that it had picked a signs track. That guess
/// should be checkable without reading the server log.
/// </remarks>
public sealed class ReferenceReport
{
    /// <summary>Gets or sets a description of what was used, or empty when nothing was.</summary>
    public string Chosen { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the reference came from embedded subtitles.</summary>
    public bool FromSubtitles { get; set; }

    /// <summary>Gets or sets an explanation of the outcome, particularly when it is a poor one.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Gets or sets the audio stream analysed, when it came to that.</summary>
    public string AudioTrack { get; set; } = string.Empty;

    /// <summary>Gets or sets the voice activity detector used, when one was.</summary>
    public string Detector { get; set; } = string.Empty;

    /// <summary>Gets or sets every subtitle stream that was considered.</summary>
    public List<ReferenceStreamInfo> Streams { get; set; } = [];

    /// <summary>
    /// Summarizes the reference in one sentence, for a decline message.
    /// </summary>
    /// <returns>A sentence, or empty when there is nothing worth saying.</returns>
    public string Explain()
    {
        if (FromSubtitles)
        {
            return string.Empty;
        }

        var text = source(this.Streams);
        return string.IsNullOrEmpty(Note) ? text : text + " " + Note;

        static string source(List<ReferenceStreamInfo> streams)
        {
            var usable = streams.Count(s => s.CueCount > 0);

            if (streams.Count == 0)
            {
                return "This file has no embedded subtitle track at all, so the timing had to be compared against voice activity in the audio.";
            }

            if (usable == 0)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"None of this file's {streams.Count} embedded subtitle track(s) could be used as a timing reference, so the comparison fell back to voice activity in the audio.");
            }

            return "The embedded subtitle tracks could not be used, so the comparison fell back to voice activity in the audio.";
        }
    }
}
