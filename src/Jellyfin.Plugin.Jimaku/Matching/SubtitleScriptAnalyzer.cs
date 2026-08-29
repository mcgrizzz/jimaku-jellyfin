using System;
using System.Globalization;
using Jellyfin.Plugin.Jimaku.Timing;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>
/// The mix of writing systems in a subtitle's visible text.
/// </summary>
/// <param name="Kana">Share of letters that are hiragana or katakana.</param>
/// <param name="Han">Share that are Han characters, shared by Japanese and Chinese.</param>
/// <param name="Other">Share belonging to any other script.</param>
/// <param name="Letters">Total letters counted, ignoring punctuation and whitespace.</param>
public readonly record struct ScriptProfile(double Kana, double Han, double Other, int Letters);

/// <summary>
/// Identifies the language of a subtitle by reading it, rather than by reading its filename.
/// </summary>
/// <remarks>
/// <para>
/// Kana settle the question that Han characters cannot. Japanese and Chinese share Han, so counting
/// those alone tells you nothing, but Japanese is written with kana throughout and Chinese has none
/// at all. Measured across real files: Japanese subtitles run 76-78% kana, a Chinese track 0%, and
/// a bilingual Chinese-Japanese release lands near 43% - which is itself the signature of two
/// languages in one file.
/// </para>
/// <para>
/// This supersedes guessing from the filename, which fails in both directions. A correct Japanese
/// subtitle carrying no language tag was being ranked below a worse one purely because the worse
/// one said "[JPN]" in its name, and a bilingual release whose name omits the second language would
/// have passed as monolingual.
/// </para>
/// </remarks>
public static class SubtitleScriptAnalyzer
{
    /// <summary>Kana share at or above which a file is considered Japanese.</summary>
    public const double JapaneseKanaThreshold = 0.10;

    /// <summary>
    /// Kana share below which a file carrying substantial Han is considered to hold a second
    /// language as well. Japanese prose does not fall this low; a bilingual file does.
    /// </summary>
    public const double BilingualKanaCeiling = 0.55;

    /// <summary>Measures the script mix of a parsed subtitle's visible text.</summary>
    /// <param name="document">The parsed subtitle.</param>
    /// <returns>The profile.</returns>
    public static ScriptProfile Profile(SubtitleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var kana = 0;
        var han = 0;
        var other = 0;

        foreach (var line in document.TimedLines)
        {
            if (line.IsComment || line.TextOffset < 0)
            {
                continue;
            }

            var raw = document.Lines[line.LineIndex].Substring(line.TextOffset, line.TextLength);
            var text = AssTagScaler.StripOverrideBlocks(raw);

            foreach (var c in text)
            {
                // Punctuation, symbols and spacing say nothing about language.
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c) || char.IsControl(c))
                {
                    continue;
                }

                if (c is >= '぀' and <= 'ヿ')
                {
                    kana++;
                }
                else if ((c is >= '一' and <= '鿿') || (c is >= '㐀' and <= '䶿'))
                {
                    han++;
                }
                else if (char.IsLetter(c))
                {
                    other++;
                }
            }
        }

        var total = kana + han + other;
        return total == 0
            ? new ScriptProfile(0, 0, 0, 0)
            : new ScriptProfile((double)kana / total, (double)han / total, (double)other / total, total);
    }

    /// <summary>Classifies a subtitle from its content.</summary>
    /// <param name="document">The parsed subtitle.</param>
    /// <returns>What languages the file appears to contain.</returns>
    public static SubtitleLanguages Classify(SubtitleDocument document)
    {
        var profile = Profile(document);

        // Too little text to judge; leave the verdict to the filename.
        if (profile.Letters < 200)
        {
            return SubtitleLanguages.Unknown;
        }

        // No kana means no Japanese, whether the rest is Han or another script entirely.
        if (profile.Kana < JapaneseKanaThreshold)
        {
            return SubtitleLanguages.NoJapanese;
        }

        // Japanese prose is kana-dominant. A file that is substantially Han yet still well short of
        // that is carrying Chinese alongside the Japanese.
        return profile.Kana < BilingualKanaCeiling && profile.Han > 0.30
            ? SubtitleLanguages.Multilingual
            : SubtitleLanguages.JapaneseOnly;
    }

    /// <summary>Renders a profile for logs and the settings page.</summary>
    /// <param name="profile">The profile.</param>
    /// <returns>A short description.</returns>
    public static string Describe(ScriptProfile profile) => profile.Letters == 0
        ? "no text"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{profile.Kana:P0} kana, {profile.Han:P0} han");
}
