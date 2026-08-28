using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>What a subtitle filename suggests about the languages inside it.</summary>
public enum SubtitleLanguages
{
    /// <summary>The filename says nothing about language.</summary>
    Unknown = 0,

    /// <summary>Japanese only. What this plugin is looking for.</summary>
    JapaneseOnly = 1,

    /// <summary>Japanese alongside another language, typically Chinese.</summary>
    Multilingual = 2,

    /// <summary>No Japanese at all.</summary>
    NoJapanese = 3,
}

/// <summary>
/// Guesses which languages a subtitle file contains from its name.
/// </summary>
/// <remarks>
/// <para>
/// Chinese fansub groups very commonly release a bilingual file, marked <c>[CHS, JPN]</c>,
/// <c>[中日双语]</c> and similar, in which the Chinese line is the styled, prominent one and the
/// Japanese sits underneath. That is a poor result for someone who wants Japanese subtitles, and
/// those same groups usually publish a Japanese-only file beside it.
/// </para>
/// <para>
/// This only reads the filename, so it is a preference rather than a verdict: a bilingual file is
/// ranked last, not discarded, because it still beats having nothing. A file with no Japanese
/// marker at all is discarded, since it cannot be useful.
/// </para>
/// </remarks>
public static class SubtitleLanguageHint
{
    private static readonly string[] JapaneseTokens =
    [
        "jpn", "jap", "jp", "ja", "japanese", "日本語", "日文", "日语",
    ];

    private static readonly string[] ChineseTokens =
    [
        "chs", "cht", "chi", "zho", "zh", "sc", "tc", "gb", "big5", "hans", "hant",
        "简体", "繁體", "繁体", "中文", "简中", "繁中",
    ];

    // Markers that mean "Chinese and Japanese together" in a single token.
    private static readonly string[] BilingualTokens =
    [
        "中日", "日中", "简日", "繁日", "双语", "雙語", "bilingual",
    ];

    /// <summary>Classifies a subtitle filename.</summary>
    /// <param name="fileName">The file name.</param>
    /// <returns>What the name suggests the file contains.</returns>
    public static SubtitleLanguages Classify(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var lower = fileName.ToLowerInvariant();

        if (BilingualTokens.Any(t => lower.Contains(t, StringComparison.Ordinal)))
        {
            return SubtitleLanguages.Multilingual;
        }

        var tokens = Tokenize(lower);

        var hasJapanese = JapaneseTokens.Any(t => tokens.Contains(t));
        var hasChinese = ChineseTokens.Any(t => tokens.Contains(t));

        if (hasJapanese && hasChinese)
        {
            return SubtitleLanguages.Multilingual;
        }

        if (hasJapanese)
        {
            return SubtitleLanguages.JapaneseOnly;
        }

        return hasChinese ? SubtitleLanguages.NoJapanese : SubtitleLanguages.Unknown;
    }

    /// <summary>
    /// Splits a filename on the separators release names use, so language codes are matched as
    /// whole tokens. Substring matching would find "ja" inside "japanese" but also inside
    /// "jav" or a title, and "sc" inside "x265_flac".
    /// </summary>
    private static HashSet<string> Tokenize(string value)
    {
        var separators = new[] { '.', '[', ']', '(', ')', '_', '-', ' ', ',', '&', '+', '{', '}', '@' };
        var tokens = value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            set.Add(token);

            // "ja-jp", "zh-hans" and similar arrive as one token after the split on '-'; also add
            // the leading subtag so "ja" is seen in "ja-jp".
            var dash = token.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                set.Add(token[..dash]);
            }
        }

        return set;
    }
}
