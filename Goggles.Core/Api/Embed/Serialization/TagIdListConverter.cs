using System.Text.Json;
using System.Text.Json.Serialization;

namespace Goggles.Core.Api.Embed.Serialization;

/// <summary>
/// Reads a product's tag ids whether GOG sends them as <c>["1","2"]</c> or as
/// <c>[{"id":"1",…}]</c>. The legacy embed endpoints are inconsistent about this, and a mismatch
/// would otherwise fail the whole page rather than one field.
/// </summary>
internal sealed class TagIdListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array of tags but found {reader.TokenType}.");
        }

        var ids = new List<string>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    ids.Add(reader.GetString()!);
                    break;

                case JsonTokenType.Number:
                    ids.Add(reader.GetInt64().ToString());
                    break;

                case JsonTokenType.StartObject:
                    var id = ReadIdFromObject(ref reader);
                    if (id is not null)
                    {
                        ids.Add(id);
                    }

                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return ids;
    }

    public override void Write(
        Utf8JsonWriter writer,
        IReadOnlyList<string> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var id in value)
        {
            writer.WriteStringValue(id);
        }

        writer.WriteEndArray();
    }

    private static string? ReadIdFromObject(ref Utf8JsonReader reader)
    {
        string? id = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var isId = reader.ValueTextEquals("id");
            reader.Read();

            if (!isId)
            {
                reader.Skip();
                continue;
            }

            id = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.GetInt64().ToString(),
                _ => null,
            };
        }

        return id;
    }
}
