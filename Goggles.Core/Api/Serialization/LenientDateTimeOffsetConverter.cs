using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goggles.Core.Api.Serialization;

/// <summary>
/// Reads timestamps that are nearly, but not quite, ISO 8601.
/// </summary>
/// <remarks>
/// <c>content-system.gog.com</c> publishes dates as <c>2023-09-19T18:36:09+0000</c> — an offset with
/// no colon, which <see cref="System.Text.Json"/> rejects outright. Rather than lose the field, the
/// offset is normalised before parsing.
/// </remarks>
internal sealed class LenientDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a timestamp string but found {reader.TokenType}.");
        }

        var value = reader.GetString();

        return string.IsNullOrWhiteSpace(value) ? null : Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Value);
        }
    }

    internal static DateTimeOffset? Parse(string value)
    {
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed;
        }

        // Turn a trailing "+0000" into "+00:00", which the standard parser does accept.
        var sign = value.LastIndexOfAny(['+', '-']);
        if (sign > 0 && value.Length - sign == 5)
        {
            var withColon = value.Insert(sign + 3, ":");

            if (DateTimeOffset.TryParse(
                    withColon,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
