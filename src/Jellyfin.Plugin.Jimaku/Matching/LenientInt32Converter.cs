using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jimaku.Matching;

/// <summary>
/// Reads an optional integer that upstream data may express as a number, a string, or a
/// comma-separated list of numbers.
/// </summary>
/// <remarks>
/// The Kometa anime ID table is community-maintained, regenerated frequently, and not schema
/// validated. Its <c>mal_id</c> field is an integer in 13,661 entries and a string such as
/// <c>"849,4382"</c> in six of them. A strict deserializer throws on the first of those and takes
/// the entire 16,000-entry table with it, which previously surfaced to the user as a failed
/// request. Being lenient about the shape of one field is much cheaper than losing the table.
/// </remarks>
public sealed class LenientInt32Converter : JsonConverter<int?>
{
    /// <inheritdoc />
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? number : null;

            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                // A list means the entry maps to several IDs; the first is the primary one.
                var comma = text.IndexOf(',', StringComparison.Ordinal);
                if (comma >= 0)
                {
                    text = text[..comma];
                }

                return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

            default:
                reader.Skip();
                return null;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
