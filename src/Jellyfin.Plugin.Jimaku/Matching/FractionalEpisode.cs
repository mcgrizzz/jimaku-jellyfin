using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>
/// Spots a file whose name gives a fractional episode number.
/// </summary>
/// <remarks>
/// <para>
/// An OVA or side story released outside the numbered run gets a half - 23.5 sits between 23 and
/// 24, which is how trackers file something that belongs to no episode slot. Jellyfin's own parser
/// reads the whole number and drops the fraction, so the file is filed as episode 23 and everything
/// downstream asks for subtitles for episode 23. Those subtitles exist, download cleanly, and
/// cannot possibly line up, because the episode on disk is not the one they belong to - and it is
/// shadowing the real episode 23 into the bargain.
/// </para>
/// <para>
/// Nothing else notices. Anitomy declines to parse the number at all and puts the whole string in
/// the title, so the filename score sees no episode to disagree with; the timing check sees only
/// that nothing correlates, which is what a wrong subtitle and a wrong episode look like alike.
/// Saying so plainly is the difference between a puzzling failure and a one-line explanation.
/// </para>
/// </remarks>
public static partial class FractionalEpisode
{
    /// <summary>
    /// Reads a fractional episode number out of a filename.
    /// </summary>
    /// <param name="fileName">The media file name.</param>
    /// <param name="whole">Receives the whole part, so it can be compared with the library's number.</param>
    /// <param name="text">Receives the number as written, for the explanation.</param>
    /// <returns><see langword="true"/> when the name gives a fractional episode.</returns>
    public static bool TryParse(string? fileName, out int whole, out string text)
    {
        whole = 0;
        text = string.Empty;

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var match = Pattern().Match(fileName);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(
                match.Groups["whole"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out whole))
        {
            return false;
        }

        // Normalised from the parsed number, so a zero-padded "07.5" reads back as "7.5" and
        // compares with the library's episode number as written.
        text = string.Create(
            CultureInfo.InvariantCulture,
            $"{whole}.{match.Groups["fraction"].Value}");

        return true;
    }

    /// <summary>
    /// The three ways a half episode is written: after a season marker, after an episode marker, or
    /// as a bare number set off by a spaced dash.
    /// </summary>
    /// <remarks>
    /// A bare number is not enough on its own. Release names are full of decimals that are not
    /// episodes - "5.1" for audio channels, "10.2 Mbps", "v1.5" - and matching those would raise a
    /// warning about a misidentified episode on files that are perfectly ordinary, which is worse
    /// than staying quiet. Each form here requires something that only precedes an episode number,
    /// and the fraction must end the number so a resolution or a bitrate cannot be mistaken for one.
    /// </remarks>
    [GeneratedRegex(
        @"(?:[Ss]\d{1,2}\s?[Ee]|(?<![A-Za-z])[Ee][Pp]?|(?<=\s[-\u2013]\s))(?<whole>\d{1,3})\.(?<fraction>\d)(?![\d\w])",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
