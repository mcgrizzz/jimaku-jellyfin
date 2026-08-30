using System;
using System.Collections.Generic;
using System.Globalization;
using AnitomySharp;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>
/// The fields of an anime release filename that matter when deciding whether a subtitle was timed
/// against the same video.
/// </summary>
public sealed class ReleaseInfo
{
    private static readonly HashSet<string> BroadcastAnimeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "TV", "OVA", "ONA", "OAV" };

    private static readonly (string Family, string[] Tokens)[] SourceFamilies =
    [
        ("BD", ["bd", "bdrip", "bluray", "blu-ray", "bdmv", "bdremux"]),
        ("DVD", ["dvd", "dvdrip", "dvd5", "dvd9", "r2j", "r2dvd"]),
        ("WEB", ["web", "webdl", "web-dl", "webrip", "www"]),
        ("TV", ["tv", "hdtv", "tvrip", "sdtv", "dtv", "ova", "ona", "oav"]),
    ];

    /// <summary>
    /// Bracketed tokens anitomy reports as a release group when they are nothing of the kind.
    /// </summary>
    /// <remarks>
    /// A trailing <c>[sdh]</c> or <c>[cc]</c> occupies the same position in a filename as a group
    /// tag and is parsed as one. Harmless for scoring, since it simply fails to match - but the
    /// series preference learns from release groups, and would have recorded that a show is best
    /// served by the group "sdh".
    /// </remarks>
    private static readonly HashSet<string> NotReleaseGroups =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "sdh", "cc", "forced", "full", "dialogue", "signs", "songs", "hi", "sub", "subs",
        };

    /// <summary>
    /// Reduces a resolution to its shorthand, so that the two ways of writing the same frame size
    /// compare equal.
    /// </summary>
    /// <remarks>
    /// Release names are split about evenly between <c>1080p</c> and <c>1920x1080</c>, and treating
    /// them as different costs a subtitle the resolution match it has earned. The disc-sourced
    /// candidate for one episode scored lower than a stream rip for exactly this reason.
    /// </remarks>
    /// <param name="resolution">The raw resolution token.</param>
    /// <returns>The shorthand form, or the original when it is not a frame size.</returns>
    public static string? NormalizeResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return null;
        }

        var text = resolution.Trim().ToLowerInvariant();
        var separator = text.IndexOfAny(['x', '\u00d7']);

        if (separator > 0
            && int.TryParse(
                text[(separator + 1)..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var height))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{height}p");
        }

        return text;
    }

    /// <summary>
    /// Reduces a source token to its family, so that superficially different spellings of the same
    /// origin are treated as the same origin.
    /// </summary>
    /// <param name="source">The raw source token.</param>
    /// <returns>The family name, or null when there is no source or it is unrecognized.</returns>
    public static string? NormalizeSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var normalized = source.Replace("-", string.Empty, StringComparison.Ordinal)
                               .Replace(" ", string.Empty, StringComparison.Ordinal)
                               .ToLowerInvariant();

        foreach (var (family, tokens) in SourceFamilies)
        {
            foreach (var token in tokens)
            {
                if (normalized.Equals(token.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal))
                {
                    return family;
                }
            }
        }

        return source.ToUpperInvariant();
    }

    /// <summary>Gets the anime title, as parsed.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the release group, e.g. <c>SubsPlease</c>.</summary>
    public string? ReleaseGroup { get; init; }

    /// <summary>Gets the episode number, if one was found.</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets the video resolution token, e.g. <c>1080p</c>.</summary>
    public string? Resolution { get; init; }

    /// <summary>
    /// Gets the resolution reduced to its shorthand, so <c>1920x1080</c> and <c>1080p</c> compare
    /// equal rather than reading as two different sizes.
    /// </summary>
    public string? ResolutionFamily => NormalizeResolution(Resolution);

    /// <summary>
    /// Gets the source token: <c>BD</c>, <c>WEB</c>, <c>TV</c> and so on. This is the single most
    /// useful field for predicting a differing cut, because disc releases routinely re-cut episodes.
    /// </summary>
    /// <remarks>
    /// Anitomy files <c>TV</c> under anime type rather than source, so a broadcast release would
    /// otherwise report no source at all - and broadcast-versus-disc is precisely the comparison
    /// that matters here. Broadcast-like anime types are folded in.
    /// </remarks>
    public string? Source { get; init; }

    /// <summary>
    /// Gets the source reduced to a family name, so <c>Web-DL</c>, <c>WEBRip</c> and <c>WEB</c>
    /// compare equal rather than reading as three different origins.
    /// </summary>
    public string? SourceFamily => NormalizeSource(Source);

    /// <summary>
    /// Gets the CRC32 checksum fansub groups append in brackets. When two filenames carry the same
    /// checksum they describe the same bytes, so the timing is guaranteed identical.
    /// </summary>
    public string? Checksum { get; init; }

    /// <summary>Gets the video codec term, e.g. <c>Hi10P</c>.</summary>
    public string? VideoTerm { get; init; }

    /// <summary>Gets the audio codec term, e.g. <c>FLAC</c>.</summary>
    public string? AudioTerm { get; init; }

    /// <summary>Parses a filename.</summary>
    /// <param name="fileName">The file name, with or without a path.</param>
    /// <returns>The parsed fields.</returns>
    public static ReleaseInfo Parse(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var elements = AnitomySharp.AnitomySharp.Parse(fileName);

        string? Find(Element.ElementCategory category)
        {
            foreach (var element in elements)
            {
                if (element.Category == category && !string.IsNullOrWhiteSpace(element.Value))
                {
                    return element.Value;
                }
            }

            return null;
        }

        var episodeText = Find(Element.ElementCategory.ElementEpisodeNumber);
        int? episode = null;
        if (episodeText is not null &&
            int.TryParse(episodeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            episode = parsed;
        }

        // Anitomy puts broadcast markers under anime type; fold the source-like ones back in.
        var source = Find(Element.ElementCategory.ElementSource);
        if (source is null)
        {
            var animeType = Find(Element.ElementCategory.ElementAnimeType);
            if (animeType is not null && BroadcastAnimeTypes.Contains(animeType))
            {
                source = animeType;
            }
        }

        // A trailing accessibility tag sits where a group tag sits and is parsed as one. Dropping
        // it keeps it out of the series preference, which learns from release groups.
        var group = Find(Element.ElementCategory.ElementReleaseGroup);
        if (group is not null && NotReleaseGroups.Contains(group.Trim()))
        {
            group = null;
        }

        return new ReleaseInfo
        {
            Title = Find(Element.ElementCategory.ElementAnimeTitle),
            ReleaseGroup = group,
            EpisodeNumber = episode,
            Resolution = Find(Element.ElementCategory.ElementVideoResolution),
            Source = source,
            Checksum = Find(Element.ElementCategory.ElementFileChecksum),
            VideoTerm = Find(Element.ElementCategory.ElementVideoTerm),
            AudioTerm = Find(Element.ElementCategory.ElementAudioTerm),
        };
    }
}
