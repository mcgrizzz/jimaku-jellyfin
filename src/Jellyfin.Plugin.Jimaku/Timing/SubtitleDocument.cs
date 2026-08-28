using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// A subtitle line carrying timings, located precisely within the raw source text.
/// </summary>
/// <remarks>
/// Character offsets rather than parsed values are what make byte-preserving rewriting possible:
/// retiming replaces exactly the start and end substrings and copies every other byte through.
/// </remarks>
public sealed class TimedLine
{
    /// <summary>Gets or sets the index of the raw line this cue lives on.</summary>
    public int LineIndex { get; set; }

    /// <summary>Gets or sets the cue start, in seconds.</summary>
    public double StartSeconds { get; set; }

    /// <summary>Gets or sets the cue end, in seconds.</summary>
    public double EndSeconds { get; set; }

    /// <summary>Gets or sets the character offset of the start timecode within the raw line.</summary>
    public int StartOffset { get; set; }

    /// <summary>Gets or sets the character length of the start timecode.</summary>
    public int StartLength { get; set; }

    /// <summary>Gets or sets the character offset of the end timecode within the raw line.</summary>
    public int EndOffset { get; set; }

    /// <summary>Gets or sets the character length of the end timecode.</summary>
    public int EndLength { get; set; }

    /// <summary>Gets or sets the character offset of the text field, or -1 when there is none inline.</summary>
    public int TextOffset { get; set; } = -1;

    /// <summary>Gets or sets the character length of the text field.</summary>
    public int TextLength { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is an ASS <c>Comment:</c> line rather than a
    /// <c>Dialogue:</c> line. Comments are retimed alongside dialogue so the file stays coherent,
    /// but they are excluded from alignment because they never appear on screen.
    /// </summary>
    public bool IsComment { get; set; }
}

/// <summary>
/// A cue track together with the index of the timed line each cue came from.
/// </summary>
/// <param name="Track">The cue timings, filtered and ordered by start time.</param>
/// <param name="TimedLineIndices">
/// For each cue, the index into <see cref="SubtitleDocument.TimedLines"/> it originated from.
/// </param>
public readonly record struct CueProjection(CueTrack Track, IReadOnlyList<int> TimedLineIndices);

/// <summary>
/// A parsed subtitle file that retains every original byte, so retiming can rewrite only the
/// timecodes and leave styling, headers and inline tags untouched.
/// </summary>
public sealed class SubtitleDocument
{
    private SubtitleDocument(SubtitleFormatKind kind, string[] lines, IReadOnlyList<TimedLine> timedLines)
    {
        Kind = kind;
        Lines = lines;
        TimedLines = timedLines;
    }

    /// <summary>Gets the detected format.</summary>
    public SubtitleFormatKind Kind { get; }

    /// <summary>
    /// Gets the raw lines, each including its own line terminator. Preserving terminators
    /// individually keeps mixed CRLF/LF files and a missing trailing newline byte-exact.
    /// </summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Gets the lines that carry timings, in file order.</summary>
    public IReadOnlyList<TimedLine> TimedLines { get; }

    /// <summary>Gets the name of the encoding the file was decoded from.</summary>
    public string SourceEncoding { get; internal set; } = "utf-8";

    /// <summary>Gets a value indicating whether any inline ASS tag carries karaoke timing.</summary>
    public bool HasKaraoke { get; private set; }

    /// <summary>Parses subtitle text, sniffing the format from its content.</summary>
    /// <param name="text">The decoded file contents.</param>
    /// <returns>The parsed document.</returns>
    public static SubtitleDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = SplitKeepingTerminators(text);
        var looksLikeAss = lines.Any(static l =>
            l.StartsWith("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("[V4 Styles]", StringComparison.OrdinalIgnoreCase));

        var document = looksLikeAss ? ParseAss(lines) : ParseSrt(lines);
        document.HasKaraoke = document.Kind == SubtitleFormatKind.Ass && document.TimedLines.Any(t =>
            t.TextOffset >= 0 &&
            AssTagScaler.ContainsKaraoke(lines[t.LineIndex].AsSpan(t.TextOffset, t.TextLength)));

        return document;
    }

    /// <summary>Decodes and parses raw file bytes.</summary>
    /// <param name="bytes">Raw file contents.</param>
    /// <returns>The parsed document.</returns>
    public static SubtitleDocument Parse(ReadOnlySpan<byte> bytes)
    {
        var text = EncodingDetector.Decode(bytes, out var encodingName);
        var document = Parse(text);
        document.SourceEncoding = encodingName;
        return document;
    }

    /// <summary>
    /// Reduces the document to bare cue timings for alignment. ASS comment lines are excluded, as
    /// are cues whose text is purely a sign, a music note or markup rather than dialogue.
    /// </summary>
    /// <returns>The cue track.</returns>
    public CueTrack ToCueTrack() => Project().Track;

    /// <summary>
    /// Builds the cue track alongside a map from each cue back to the timed line it came from.
    /// </summary>
    /// <remarks>
    /// Piecewise alignment returns offsets addressed by cue index, and those indices refer to this
    /// filtered, start-time-sorted track rather than to raw line order. ASS files are not required
    /// to store events in chronological order, and non-dialogue lines are dropped here, so walking
    /// the lines again and assuming the two line up would silently apply the wrong offsets.
    /// </remarks>
    /// <returns>The track and its line map.</returns>
    public CueProjection Project()
    {
        var entries = new List<(Cue Cue, int LineIndex)>(TimedLines.Count);

        for (var i = 0; i < TimedLines.Count; i++)
        {
            var line = TimedLines[i];
            if (line.IsComment || line.EndSeconds <= line.StartSeconds)
            {
                continue;
            }

            if (line.TextOffset >= 0)
            {
                var text = Lines[line.LineIndex].Substring(line.TextOffset, line.TextLength);
                if (IsNonDialogue(text))
                {
                    continue;
                }
            }

            entries.Add((new Cue(line.StartSeconds, line.EndSeconds), i));
        }

        // Ordered exactly as CueTrack orders internally, so cue index i corresponds to ordered[i].
        // OrderBy is a stable sort; List.Sort is not, and cues sharing a start time are common.
        var ordered = entries.OrderBy(static e => e.Cue.StartSeconds).ToArray();

        return new CueProjection(
            new CueTrack(ordered.Select(static e => e.Cue)),
            ordered.Select(static e => e.LineIndex).ToArray());
    }

    /// <summary>Reassembles the raw text exactly as parsed.</summary>
    /// <returns>The original text.</returns>
    public string ToText() => string.Concat(Lines);

    private static bool IsNonDialogue(string text)
    {
        var stripped = AssTagScaler.StripOverrideBlocks(text)
            .Replace("\\N", " ", StringComparison.Ordinal)
            .Replace("\\n", " ", StringComparison.Ordinal)
            .Replace("\\h", " ", StringComparison.Ordinal)
            .Trim();

        if (stripped.Length == 0)
        {
            return true;
        }

        // Music notes mark songs, which are frequently timed to the karaoke rather than to speech.
        if (stripped.All(static c => c is '♪' or '♫' or '♬' or '~' or ' '))
        {
            return true;
        }

        return false;
    }

    private static string[] SplitKeepingTerminators(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines.ToArray();
    }

    private static SubtitleDocument ParseAss(string[] lines)
    {
        // Column order is declared per file by the Format: line in [Events]; it is not safe to
        // assume the canonical order, though it is the overwhelmingly common one.
        var startColumn = 1;
        var endColumn = 2;
        var textColumn = 9;
        var columnCount = 10;
        var inEvents = false;

        var timed = new List<TimedLine>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd('\n', '\r');

            if (line.StartsWith('['))
            {
                inEvents = line.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents)
            {
                continue;
            }

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var columns = line["Format:".Length..]
                    .Split(',')
                    .Select(static c => c.Trim())
                    .ToArray();
                columnCount = columns.Length;
                startColumn = Array.FindIndex(columns, static c => c.Equals("Start", StringComparison.OrdinalIgnoreCase));
                endColumn = Array.FindIndex(columns, static c => c.Equals("End", StringComparison.OrdinalIgnoreCase));
                textColumn = Array.FindIndex(columns, static c => c.Equals("Text", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            var isDialogue = line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase);
            var isComment = line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase);
            if (!isDialogue && !isComment)
            {
                continue;
            }

            if (startColumn < 0 || endColumn < 0)
            {
                continue;
            }

            var prefixLength = line.IndexOf(':', StringComparison.Ordinal) + 1;
            var fields = SplitFields(line, prefixLength, columnCount);
            if (fields.Count <= Math.Max(startColumn, endColumn))
            {
                continue;
            }

            var startField = fields[startColumn];
            var endField = fields[endColumn];

            if (!TryParseAssTime(line.AsSpan(startField.Offset, startField.Length).Trim(), out var start) ||
                !TryParseAssTime(line.AsSpan(endField.Offset, endField.Length).Trim(), out var end))
            {
                continue;
            }

            var entry = new TimedLine
            {
                LineIndex = i,
                StartSeconds = start,
                EndSeconds = end,
                StartOffset = startField.Offset,
                StartLength = startField.Length,
                EndOffset = endField.Offset,
                EndLength = endField.Length,
                IsComment = isComment,
            };

            if (textColumn >= 0 && textColumn < fields.Count)
            {
                entry.TextOffset = fields[textColumn].Offset;
                entry.TextLength = fields[textColumn].Length;
            }

            timed.Add(entry);
        }

        return new SubtitleDocument(SubtitleFormatKind.Ass, lines, timed);
    }

    private static SubtitleDocument ParseSrt(string[] lines)
    {
        var timed = new List<TimedLine>();

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var arrow = raw.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue;
            }

            var line = raw.TrimEnd('\n', '\r');
            arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                continue;
            }

            var leftRaw = line[..arrow];
            var rightRaw = line[(arrow + 3)..];

            var startOffset = leftRaw.Length - leftRaw.TrimStart().Length;
            var startLength = leftRaw.Trim().Length;

            var rightTrimStart = rightRaw.Length - rightRaw.TrimStart().Length;

            // SubRip permits trailing display coordinates after the end timecode; take only the
            // first whitespace-delimited token so those survive untouched.
            var rightBody = rightRaw.TrimStart();
            var space = rightBody.IndexOf(' ', StringComparison.Ordinal);
            var endText = space >= 0 ? rightBody[..space] : rightBody;
            var endOffset = arrow + 3 + rightTrimStart;

            if (!TryParseSrtTime(line.AsSpan(startOffset, startLength), out var start) ||
                !TryParseSrtTime(endText.AsSpan(), out var end))
            {
                continue;
            }

            timed.Add(new TimedLine
            {
                LineIndex = i,
                StartSeconds = start,
                EndSeconds = end,
                StartOffset = startOffset,
                StartLength = startLength,
                EndOffset = endOffset,
                EndLength = endText.Length,
            });
        }

        return new SubtitleDocument(SubtitleFormatKind.Srt, lines, timed);
    }

    private readonly record struct Field(int Offset, int Length);

    /// <summary>
    /// Splits an ASS event line into at most <paramref name="columnCount"/> comma-separated fields,
    /// returning offsets into the original line. The final field is the text, which routinely
    /// contains commas of its own, so the split must stop counting once it is reached.
    /// </summary>
    private static List<Field> SplitFields(string line, int start, int columnCount)
    {
        var fields = new List<Field>(columnCount);
        var cursor = start;
        while (fields.Count < columnCount - 1)
        {
            var comma = line.IndexOf(',', cursor);
            if (comma < 0)
            {
                break;
            }

            fields.Add(new Field(cursor, comma - cursor));
            cursor = comma + 1;
        }

        fields.Add(new Field(cursor, line.Length - cursor));
        return fields;
    }

    /// <summary>Parses an ASS timecode, <c>h:mm:ss.cc</c>.</summary>
    internal static bool TryParseAssTime(ReadOnlySpan<char> span, out double seconds)
    {
        seconds = 0;
        var first = span.IndexOf(':');
        if (first < 0)
        {
            return false;
        }

        var rest = span[(first + 1)..];
        var second = rest.IndexOf(':');
        if (second < 0)
        {
            return false;
        }

        if (!int.TryParse(span[..first], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(rest[..second], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(rest[(second + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            return false;
        }

        seconds = (hours * 3600.0) + (minutes * 60.0) + secs;
        return true;
    }

    /// <summary>Parses a SubRip timecode, <c>hh:mm:ss,mmm</c>. A dot separator is also accepted.</summary>
    internal static bool TryParseSrtTime(ReadOnlySpan<char> span, out double seconds)
    {
        Span<char> normalized = span.Length <= 32 ? stackalloc char[span.Length] : new char[span.Length];
        span.CopyTo(normalized);
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == ',')
            {
                normalized[i] = '.';
            }
        }

        return TryParseAssTime(normalized, out seconds);
    }
}
