using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// Rescales the time-valued arguments of ASS inline override tags.
/// </summary>
/// <remarks>
/// <para>
/// Inline tag timings are relative to the cue start, so a pure shift leaves them alone: only the
/// Dialogue Start and End need rewriting. A framerate correction is different. It changes the cue's
/// duration, so karaoke syllables, animated transforms, movements and fades must all be stretched
/// by the same ratio or they drift within the line.
/// </para>
/// <para>
/// Karaoke durations are handled cumulatively rather than one at a time: scaling and rounding each
/// syllable independently lets the rounding error accumulate until the syllables no longer add up
/// to the line duration. Scaling the running total and taking differences keeps the sum correct.
/// </para>
/// </remarks>
public static class AssTagScaler
{
    /// <summary>Tests whether text contains any karaoke timing tag.</summary>
    /// <param name="text">The dialogue text field.</param>
    /// <returns><see langword="true"/> if a karaoke tag is present.</returns>
    public static bool ContainsKaraoke(ReadOnlySpan<char> text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '\\')
            {
                continue;
            }

            var c = text[i + 1];
            if (c is 'k' or 'K')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes <c>{...}</c> override blocks, leaving only visible text.</summary>
    /// <param name="text">The dialogue text field.</param>
    /// <returns>Text with override blocks stripped.</returns>
    public static string StripOverrideBlocks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Scales every time-valued inline tag in a dialogue text field.
    /// </summary>
    /// <param name="text">The dialogue text field.</param>
    /// <param name="scale">The time scale to apply.</param>
    /// <returns>The rewritten text. Returns the input unchanged when the scale is 1.</returns>
    public static string Scale(string text, double scale)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Math.Abs(scale - 1.0) < 1e-9 || text.IndexOf('\\', StringComparison.Ordinal) < 0)
        {
            return text;
        }

        // Pass one collects karaoke durations so they can be scaled as a cumulative series.
        var durations = new List<int>();
        Walk(text, scale, durations, null);

        var scaled = ScaleCumulative(durations, scale);

        // Pass two emits, consuming the pre-computed durations in the same order.
        var output = new StringBuilder(text.Length + 16);
        Walk(text, scale, null, new EmitState(scaled, output));
        return output.ToString();
    }

    /// <summary>
    /// Scales a series of consecutive durations by scaling the running total and re-differencing,
    /// so accumulated rounding error cannot pull the sum away from the scaled total.
    /// </summary>
    /// <param name="durations">The original durations.</param>
    /// <param name="scale">The scale factor.</param>
    /// <returns>The scaled durations, summing to the scaled total.</returns>
    internal static int[] ScaleCumulative(IReadOnlyList<int> durations, double scale)
    {
        var result = new int[durations.Count];
        double cumulative = 0;
        long emitted = 0;
        for (var i = 0; i < durations.Count; i++)
        {
            cumulative += durations[i];
            var target = (long)Math.Round(cumulative * scale, MidpointRounding.AwayFromZero);
            result[i] = (int)(target - emitted);
            emitted = target;
        }

        return result;
    }

    private sealed class EmitState(int[] karaoke, StringBuilder output)
    {
        public int[] Karaoke { get; } = karaoke;

        public StringBuilder Output { get; } = output;

        public int KaraokeIndex { get; set; }
    }

    private static void Walk(string text, double scale, List<int>? collect, EmitState? emit)
    {
        var i = 0;
        var depth = 0;
        while (i < text.Length)
        {
            var c = text[i];

            if (c == '{')
            {
                depth++;
                emit?.Output.Append(c);
                i++;
                continue;
            }

            if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
                emit?.Output.Append(c);
                i++;
                continue;
            }

            // Only override blocks contain tags; a backslash in visible text is an escape such as
            // \N and must never be treated as a tag.
            if (depth == 0 || c != '\\')
            {
                emit?.Output.Append(c);
                i++;
                continue;
            }

            var nameStart = i + 1;
            var nameEnd = nameStart;
            while (nameEnd < text.Length && char.IsLetter(text[nameEnd]))
            {
                nameEnd++;
            }

            var name = text[nameStart..nameEnd];

            switch (name)
            {
                case "k":
                case "K":
                case "kf":
                case "ko":
                    if (TryReadInt(text, nameEnd, out var duration, out var afterNumber))
                    {
                        if (collect is not null)
                        {
                            collect.Add(duration);
                        }

                        if (emit is not null)
                        {
                            var value = emit.KaraokeIndex < emit.Karaoke.Length
                                ? emit.Karaoke[emit.KaraokeIndex]
                                : duration;
                            emit.KaraokeIndex++;
                            emit.Output.Append('\\').Append(name);
                            emit.Output.Append(value.ToString(CultureInfo.InvariantCulture));
                        }

                        i = afterNumber;
                        continue;
                    }

                    break;

                case "kt":
                    // An absolute position within the line rather than a duration, so it is scaled
                    // on its own instead of joining the cumulative chain.
                    if (TryReadInt(text, nameEnd, out var absolute, out var afterAbsolute))
                    {
                        if (emit is not null)
                        {
                            var value = (int)Math.Round(absolute * scale, MidpointRounding.AwayFromZero);
                            emit.Output.Append("\\kt").Append(value.ToString(CultureInfo.InvariantCulture));
                        }

                        i = afterAbsolute;
                        continue;
                    }

                    break;

                case "t":
                case "move":
                case "fad":
                case "fade":
                    if (TryReadArguments(text, nameEnd, out var arguments, out var afterArguments))
                    {
                        if (emit is not null)
                        {
                            emit.Output.Append('\\').Append(name).Append('(');
                            emit.Output.Append(ScaleArguments(name, arguments, scale));
                            emit.Output.Append(')');
                        }

                        i = afterArguments;
                        continue;
                    }

                    break;

                default:
                    break;
            }

            emit?.Output.Append(c);
            i++;
        }
    }

    private static string ScaleArguments(string tag, List<string> arguments, double scale)
    {
        // Which arguments are times depends on the tag and, for \t and \move, on how many were
        // supplied - both have optional leading or trailing time pairs.
        var timeIndices = tag switch
        {
            "fad" => arguments.Count == 2 ? new[] { 0, 1 } : Array.Empty<int>(),
            "fade" => arguments.Count == 7 ? new[] { 3, 4, 5, 6 } : Array.Empty<int>(),
            "move" => arguments.Count >= 6 ? new[] { 4, 5 } : Array.Empty<int>(),
            "t" => arguments.Count >= 3 ? new[] { 0, 1 } : Array.Empty<int>(),
            _ => Array.Empty<int>(),
        };

        // \t(accel, style) also has three arguments in some writings; only rescale when the two
        // leading arguments really are numbers.
        if (tag == "t" && timeIndices.Length > 0 &&
            (!IsNumeric(arguments[0]) || !IsNumeric(arguments[1])))
        {
            timeIndices = Array.Empty<int>();
        }

        foreach (var index in timeIndices)
        {
            if (index < arguments.Count &&
                double.TryParse(arguments[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                var scaled = Math.Round(value * scale, MidpointRounding.AwayFromZero);
                arguments[index] = scaled.ToString("0", CultureInfo.InvariantCulture);
            }
        }

        return string.Join(',', arguments);
    }

    private static bool IsNumeric(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static bool TryReadInt(string text, int start, out int value, out int next)
    {
        value = 0;
        next = start;
        var i = start;
        if (i < text.Length && (text[i] == '-' || text[i] == '+'))
        {
            i++;
        }

        var digitsStart = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i == digitsStart)
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        next = i;
        return true;
    }

    /// <summary>
    /// Reads a parenthesized argument list, splitting on commas at depth zero. Nested parentheses
    /// are tracked so a tag such as <c>\t(0,500,\clip(...))</c> is not truncated mid-argument.
    /// </summary>
    private static bool TryReadArguments(string text, int start, out List<string> arguments, out int next)
    {
        arguments = new List<string>();
        next = start;

        if (start >= text.Length || text[start] != '(')
        {
            return false;
        }

        var depth = 0;
        var current = new StringBuilder();
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                depth++;
                if (depth == 1)
                {
                    continue;
                }
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    arguments.Add(current.ToString());
                    next = i + 1;
                    return true;
                }
            }
            else if (c == ',' && depth == 1)
            {
                arguments.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        return false;
    }
}
