using System;
using System.Text;

namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>
/// Decodes subtitle bytes, guessing the encoding when there is no byte-order mark.
/// </summary>
/// <remarks>
/// Jimaku hosts a great many files that predate the move to UTF-8, so Shift-JIS is common. Getting
/// this wrong produces a file that loads and renders but is unreadable mojibake, which is easy to
/// ship without noticing.
/// </remarks>
public static class EncodingDetector
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static int _providerRegistered;

    /// <summary>
    /// Decodes subtitle bytes to text.
    /// </summary>
    /// <param name="bytes">Raw file contents.</param>
    /// <param name="encodingName">Receives the name of the encoding that was used.</param>
    /// <returns>The decoded text, with any byte-order mark removed.</returns>
    public static string Decode(ReadOnlySpan<byte> bytes, out string encodingName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingName = "utf-8-bom";
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingName = "utf-16le";
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encodingName = "utf-16be";
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        // No BOM. Strict UTF-8 is a reliable discriminator: Shift-JIS and EUC-JP text of any real
        // length is overwhelmingly unlikely to also be valid UTF-8.
        try
        {
            var text = StrictUtf8.GetString(bytes);
            encodingName = "utf-8";
            return text;
        }
        catch (DecoderFallbackException)
        {
            // Fall through to the Japanese legacy encodings.
        }

        EnsureCodePagesRegistered();

        foreach (var (codePage, name) in new[] { (932, "shift-jis"), (51932, "euc-jp") })
        {
            try
            {
                var encoding = Encoding.GetEncoding(
                    codePage,
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback);
                var text = encoding.GetString(bytes);
                encodingName = name;
                return text;
            }
            catch (DecoderFallbackException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }
            catch (ArgumentException)
            {
                continue;
            }
        }

        // Nothing decoded cleanly. Latin-1 is lossless at the byte level, so the file at least
        // round-trips and the timing rewrite still works even if the text is wrong.
        encodingName = "latin-1";
        return Encoding.Latin1.GetString(bytes);
    }

    private static void EnsureCodePagesRegistered()
    {
        if (System.Threading.Interlocked.Exchange(ref _providerRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
