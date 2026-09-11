using System.Text.Json;
using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos;

// Reads a field that is stored as a JSON *string* but is naturally written as a JSON
// *object*, and accepts either spelling.
//
// auth_config is the case that motivated it: `{"auth_config": {"method":"token"}}` is
// what everyone types, and rejecting it with a bare 400 from the model binder made an
// integration's stored credentials look unrepairable — the field appeared to be
// read-only when it was only being sent in the other of two reasonable shapes.
// Objects are re-serialized to their raw text, so what reaches the database is
// identical either way.
public sealed class JsonStringOrObjectConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
            {
                using var doc = JsonDocument.ParseValue(ref reader);
                return doc.RootElement.GetRawText();
            }
            default:
                throw new JsonException("expected a JSON object or a JSON string");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
