using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// The outcome of rewriting a subtitle's timings.
/// </summary>
/// <param name="Text">The rewritten file contents, or the original when nothing changed.</param>
/// <param name="CuesRewritten">How many timed lines had their timecodes replaced.</param>
/// <param name="InlineTagsScaled">Whether inline ASS tag timings were rescaled.</param>
/// <param name="ClampedToZero">How many timecodes would have gone negative and were clamped.</param>
public readonly record struct RewriteResult(
    string Text,
    int CuesRewritten,
    bool InlineTagsScaled,
    int ClampedToZero);

/// <summary>
/// Applies a timing correction to a parsed subtitle, rewriting only the timecodes.
/// </summary>
/// <remarks>
/// Everything else is copied through byte for byte: script info, style definitions, Aegisub project
/// metadata, embedded fonts, comments, and inline override tags. That is the whole point of holding
/// the file as raw lines plus offsets rather than as a parsed object model that has to be
/// re-serialized.
/// </remarks>
public static class SubtitleRewriter
{
    /// <summary>Applies a global linear transform.</summary>
    /// <param name="document">The parsed subtitle.</param>
    /// <param name="transform">The correction to apply.</param>
    /// <param name="karaokePolicy">What to do about karaoke when the transform scales time.</param>
    /// <returns>The rewritten text and a summary of what changed.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the transform scales time, the file has karaoke, and the policy is
    /// <see cref="KaraokeScalePolicy.Decline"/>.
    /// </exception>
    public static RewriteResult Apply(
        SubtitleDocument document,
        TimingTransform transform,
        KaraokeScalePolicy karaokePolicy = KaraokeScalePolicy.Rescale)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (transform.IsIdentity)
        {
            return new RewriteResult(document.ToText(), 0, false, 0);
        }

        if (!transform.IsShiftOnly && document.HasKaraoke && karaokePolicy == KaraokeScalePolicy.Decline)
        {
            throw new InvalidOperationException(
                "Refusing to rescale a subtitle containing karaoke timing while the karaoke policy is set to decline.");
        }

        var scaleInlineTags = !transform.IsShiftOnly && document.Kind == SubtitleFormatKind.Ass;
        return Rewrite(document, _ => transform, scaleInlineTags ? transform.Scale : 1.0);
    }

    /// <summary>
    /// Applies per-block offsets produced by the split aligner, so each region of a differently cut
    /// subtitle gets the shift it actually needs.
    /// </summary>
    /// <param name="document">The parsed subtitle.</param>
    /// <param name="blocks">The blocks, covering the cue indices of the aligned cue track.</param>
    /// <returns>The rewritten text and a summary of what changed.</returns>
    public static RewriteResult ApplyBlocks(SubtitleDocument document, IReadOnlyList<SplitBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(blocks);

        if (blocks.Count == 0)
        {
            return new RewriteResult(document.ToText(), 0, false, 0);
        }

        // Block indices address the aligned cue track, which excludes comments and non-dialogue
        // lines and is ordered by start time. The projection carries the map back to the original
        // timed lines, so offsets land on the lines they were actually computed for.
        var projection = document.Project();
        var offsets = new double[document.TimedLines.Count];
        var assigned = new bool[document.TimedLines.Count];

        var blockIndex = 0;
        for (var cueIndex = 0; cueIndex < projection.TimedLineIndices.Count; cueIndex++)
        {
            while (blockIndex < blocks.Count - 1 && cueIndex > blocks[blockIndex].LastCueIndex)
            {
                blockIndex++;
            }

            var lineIndex = projection.TimedLineIndices[cueIndex];
            offsets[lineIndex] = blocks[blockIndex].OffsetSeconds;
            assigned[lineIndex] = true;
        }

        // Lines the aligner never saw - comments, signs, music cues - take the offset of the
        // nearest preceding line that was aligned, so they stay with the section they belong to.
        var carried = blocks[0].OffsetSeconds;
        for (var i = 0; i < offsets.Length; i++)
        {
            if (assigned[i])
            {
                carried = offsets[i];
            }
            else
            {
                offsets[i] = carried;
            }
        }

        return Rewrite(document, i => new TimingTransform(1.0, offsets[i]), 1.0);
    }

    private static RewriteResult Rewrite(
        SubtitleDocument document,
        Func<int, TimingTransform> transformFor,
        double inlineTagScale)
    {
        var lines = new string[document.Lines.Count];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = document.Lines[i];
        }

        var rewritten = 0;
        var clamped = 0;
        var scaledTags = false;

        for (var i = 0; i < document.TimedLines.Count; i++)
        {
            var timed = document.TimedLines[i];
            var transform = transformFor(i);
            var raw = lines[timed.LineIndex];

            var start = transform.Apply(timed.StartSeconds);
            var end = transform.Apply(timed.EndSeconds);

            if (start < 0)
            {
                start = 0;
                clamped++;
            }

            if (end < 0)
            {
                end = 0;
                clamped++;
            }

            var builder = new StringBuilder(raw.Length + 8);

            // Fields are replaced from the right so earlier offsets stay valid as the string grows
            // or shrinks. Text sits after End, which sits after Start, in every real ASS layout.
            var edits = new (int Offset, int Length, string Replacement)[timed.TextOffset >= 0 && inlineTagScale != 1.0 ? 3 : 2];
            edits[0] = (timed.StartOffset, timed.StartLength, FormatTime(document.Kind, start));
            edits[1] = (timed.EndOffset, timed.EndLength, FormatTime(document.Kind, end));

            if (edits.Length == 3)
            {
                var text = raw.Substring(timed.TextOffset, timed.TextLength);
                var scaled = AssTagScaler.Scale(text, inlineTagScale);
                if (!string.Equals(scaled, text, StringComparison.Ordinal))
                {
                    scaledTags = true;
                }

                edits[2] = (timed.TextOffset, timed.TextLength, scaled);
            }

            Array.Sort(edits, static (a, b) => a.Offset.CompareTo(b.Offset));

            var cursor = 0;
            foreach (var (offset, length, replacement) in edits)
            {
                if (offset < cursor)
                {
                    continue;
                }

                builder.Append(raw, cursor, offset - cursor);
                builder.Append(replacement);
                cursor = offset + length;
            }

            builder.Append(raw, cursor, raw.Length - cursor);
            lines[timed.LineIndex] = builder.ToString();
            rewritten++;
        }

        return new RewriteResult(string.Concat(lines), rewritten, scaledTags, clamped);
    }

    /// <summary>
    /// Formats a timecode for the given format.
    /// </summary>
    /// <remarks>
    /// ASS uses <c>h:mm:ss.cc</c> with an unpadded hour and centisecond precision, rounded
    /// away from zero and carrying into the seconds when it reaches 100. This matches what Aegisub
    /// and Subtitle Edit write, so corrected files still round-trip through ordinary tooling.
    /// </remarks>
    /// <param name="kind">The subtitle format.</param>
    /// <param name="seconds">The time to format.</param>
    /// <returns>The formatted timecode.</returns>
    internal static string FormatTime(SubtitleFormatKind kind, double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        if (kind == SubtitleFormatKind.Srt)
        {
            var totalMs = (long)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
            var ms = totalMs % 1000;
            var totalSecondsSrt = totalMs / 1000;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{totalSecondsSrt / 3600:00}:{totalSecondsSrt / 60 % 60:00}:{totalSecondsSrt % 60:00},{ms:000}");
        }

        var totalCs = (long)Math.Round(seconds * 100.0, MidpointRounding.AwayFromZero);
        var cs = totalCs % 100;
        var totalSecondsAss = totalCs / 100;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalSecondsAss / 3600}:{totalSecondsAss / 60 % 60:00}:{totalSecondsAss % 60:00}.{cs:00}");
    }
}
