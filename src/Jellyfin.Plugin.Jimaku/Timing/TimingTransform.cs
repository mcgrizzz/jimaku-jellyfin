using System;
using System.Globalization;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// A linear time map <c>t' = (Scale * t) + OffsetSeconds</c> applied to a subtitle's timings.
/// </summary>
/// <param name="Scale">Time scale. 1.0 is a pure shift.</param>
/// <param name="OffsetSeconds">Constant offset in seconds, added after scaling.</param>
public readonly record struct TimingTransform(double Scale, double OffsetSeconds)
{
    /// <summary>Gets the identity transform, which changes nothing.</summary>
    public static TimingTransform Identity { get; } = new TimingTransform(1.0, 0.0);

    /// <summary>
    /// Gets a value indicating whether this is a pure shift. Shift-only corrections are always safe
    /// on ASS because inline tag timings are relative to the cue start and so are unaffected.
    /// </summary>
    public bool IsShiftOnly => Math.Abs(Scale - 1.0) < 1e-9;

    /// <summary>Gets a value indicating whether this transform changes nothing at all.</summary>
    public bool IsIdentity => IsShiftOnly && Math.Abs(OffsetSeconds) < 1e-9;

    /// <summary>Applies the transform to a time value.</summary>
    /// <param name="seconds">The input time in seconds.</param>
    /// <returns>The transformed time in seconds.</returns>
    public double Apply(double seconds) => (Scale * seconds) + OffsetSeconds;

    /// <summary>Returns a short human-readable description, for logs and the config UI.</summary>
    /// <returns>A description such as <c>+2.500s</c> or <c>x1.042709 -0.120s</c>.</returns>
    public string Describe()
    {
        if (IsIdentity)
        {
            return "unchanged";
        }

        var offset = string.Create(CultureInfo.InvariantCulture, $"{OffsetSeconds:+0.000;-0.000}s");
        return IsShiftOnly
            ? offset
            : string.Create(CultureInfo.InvariantCulture, $"x{Scale:0.######} {offset}");
    }
}
