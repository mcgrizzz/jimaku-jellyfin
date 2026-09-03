using System;
using System.Globalization;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>
/// How well a subtitle filename matches the local video's filename.
/// </summary>
/// <param name="Score">Score out of 100.</param>
/// <param name="IsExactRelease">The two filenames carry the same CRC32, so they describe the same video.</param>
/// <param name="EpisodeMismatch">The filenames name different episodes; the candidate is unusable.</param>
/// <param name="SourceMismatch">
/// The releases came from different sources, such as a broadcast subtitle against a disc video.
/// A strong hint that the cut differs and that a single global offset will not be enough.
/// </param>
/// <param name="SourceMatch">
/// Both releases name a source and the two agree. Distinct from the absence of a mismatch, which is
/// also what an unnamed source produces: a filename that says nothing about its origin is not
/// evidence that the origin is the same one.
/// </param>
/// <param name="Notes">A short explanation for display.</param>
public readonly record struct NameMatch(
    int Score,
    bool IsExactRelease,
    bool EpisodeMismatch,
    bool SourceMismatch,
    bool SourceMatch,
    string Notes);

/// <summary>
/// Scores a candidate subtitle filename against the local video filename.
/// </summary>
/// <remarks>
/// This is a cheap pre-filter, not the decision. It runs before anything is downloaded, so the
/// expensive timing analysis is only spent on plausible candidates, and it flags the cases where
/// the timing analysis should expect a differing cut. The actual accept decision is always made on
/// measured timing, never on the filename.
/// </remarks>
public static class ReleaseMatcher
{
    /// <summary>Compares a video filename with a subtitle filename.</summary>
    /// <param name="videoFileName">The local media file name.</param>
    /// <param name="subtitleFileName">The candidate subtitle file name.</param>
    /// <param name="expectedEpisode">The episode number expected, if known.</param>
    /// <param name="alternateEpisode">
    /// A second acceptable numbering for the same episode.
    /// </param>
    /// <remarks>
    /// Two numbers, because one entry legitimately holds both. Where a season is split across two
    /// AniList entries, Jimaku numbers the entry from one while the uploads inside it are named by
    /// fansubbers who numbered the season straight through - so an entry for the second cour holds
    /// files called "E03" and files called "14" describing the same episode, and Jimaku returns
    /// both because its own relations table knows they are the same. Insisting on one of them
    /// rejected every correctly named file in the entry.
    /// </remarks>
    /// <returns>The match assessment.</returns>
    public static NameMatch Compare(
        string videoFileName,
        string subtitleFileName,
        int? expectedEpisode,
        int? alternateEpisode = null)
    {
        var video = ReleaseInfo.Parse(videoFileName ?? string.Empty);
        var subtitle = ReleaseInfo.Parse(subtitleFileName ?? string.Empty);

        // A shared CRC32 means the subtitle was released against this exact video file. Nothing else
        // comes close as evidence, and it makes the timing check a formality.
        if (!string.IsNullOrEmpty(video.Checksum) &&
            string.Equals(video.Checksum, subtitle.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            return new NameMatch(100, true, false, false, true, "exact release match (CRC32)");
        }

        var episodeMismatch = subtitle.EpisodeNumber.HasValue
                              && (expectedEpisode.HasValue || alternateEpisode.HasValue)
                              && subtitle.EpisodeNumber != expectedEpisode
                              && subtitle.EpisodeNumber != alternateEpisode;

        if (episodeMismatch)
        {
            var wanted = alternateEpisode.HasValue && alternateEpisode != expectedEpisode
                ? string.Create(CultureInfo.InvariantCulture, $"{expectedEpisode} or {alternateEpisode}")
                : expectedEpisode?.ToString(CultureInfo.InvariantCulture) ?? "none";

            return new NameMatch(
                0,
                false,
                true,
                false,
                false,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"names episode {subtitle.EpisodeNumber}, expected {wanted}"));
        }

        var score = 0;
        var notes = new System.Collections.Generic.List<string>();

        if (TokensEqual(video.ReleaseGroup, subtitle.ReleaseGroup))
        {
            score += 45;
            notes.Add("same group");
        }

        var sourceMismatch = false;
        var sourceMatch = false;
        if (video.SourceFamily is not null && subtitle.SourceFamily is not null)
        {
            if (TokensEqual(video.SourceFamily, subtitle.SourceFamily))
            {
                score += 25;
                sourceMatch = true;
                notes.Add("same source");
            }
            else
            {
                sourceMismatch = true;
                notes.Add($"{subtitle.SourceFamily} subtitle on {video.SourceFamily} video");
            }
        }

        if (TokensEqual(video.ResolutionFamily, subtitle.ResolutionFamily))
        {
            score += 10;
        }

        if (TokensEqual(video.VideoTerm, subtitle.VideoTerm))
        {
            score += 5;
        }

        if (TokensEqual(video.AudioTerm, subtitle.AudioTerm))
        {
            score += 5;
        }

        if (expectedEpisode.HasValue && subtitle.EpisodeNumber == expectedEpisode)
        {
            score += 10;
            notes.Add("episode matches");
        }

        if (notes.Count == 0)
        {
            notes.Add("no shared release details");
        }

        return new NameMatch(
            Math.Clamp(score, 0, 100),
            false,
            false,
            sourceMismatch,
            sourceMatch,
            string.Join(", ", notes));
    }

    /// <summary>
    /// Compares release tokens ignoring case, spacing and punctuation, since groups are written
    /// inconsistently across releases.
    /// </summary>
    private static bool TokensEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }
}
