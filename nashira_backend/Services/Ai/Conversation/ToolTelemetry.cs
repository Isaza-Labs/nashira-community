using System.Text.Json;
using System.Text.Json.Nodes;

namespace nashira_backend.Services.Ai.Conversation;

// One tool call as it will be recorded: the arguments that went out, whether it
// worked, and what came back.
public sealed record ToolTelemetryEntry(
    string Name, JsonElement Arguments, bool Ok, JsonElement Result, int ElapsedMs);

// Turns tool calls into something safe and bounded to store.
//
// Two hazards make this more than a JSON serialize. A tool's arguments can carry the
// very secret the platform exists to protect — `set_secret` takes the value, and
// `create_credential` takes a password — so recording them verbatim would put
// plaintext into a table built to be read by auditors. And a tool result can be a
// NetBox page with three hundred devices in it, which no forensic view needs and no
// database column should carry per turn.
public static class ToolTelemetry
{
    // ASCII on purpose: System.Text.Json escapes non-ASCII by default, so a prettier
    // marker would sit in the column as `«redacted»` for whoever reads the
    // raw row — which is exactly the person this record exists for.
    public const string RedactedMarker = "[redacted]";

    private const int MaxArgumentChars = 4_000;
    private const int MaxResultChars = 4_000;
    private const int MaxTotalChars = 128_000;
    public const int MaxTextChars = 16_000;

    // Argument names whose VALUE is secret material. Matched case-insensitively on the
    // whole name, not as a substring: `api_key_header` names a header, and redacting it
    // would hide the very thing an operator is trying to diagnose.
    private static readonly HashSet<string> SecretArgumentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "password", "new_password", "current_password", "token", "api_key", "apikey",
        "secret", "client_secret", "private_key", "passphrase", "credential_value",
    };

    public static string Serialize(IReadOnlyList<ToolTelemetryEntry> calls)
    {
        if (calls.Count == 0) return "[]";

        var array = new JsonArray();
        foreach (var c in calls)
        {
            array.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["ok"] = c.Ok,
                ["elapsed_ms"] = c.ElapsedMs,
                ["arguments"] = Redact(c.Arguments, MaxArgumentChars),
                ["result"] = Truncate(c.Result, MaxResultChars),
            });
        }

        var json = array.ToJsonString();
        // A last-resort bound. Dropping the payloads but keeping the sequence of names
        // is more useful than a row that fails to write at all.
        if (json.Length <= MaxTotalChars) return json;

        var slim = new JsonArray();
        foreach (var c in calls)
            slim.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["ok"] = c.Ok,
                ["elapsed_ms"] = c.ElapsedMs,
                ["result"] = JsonValue.Create($"[{calls.Count} calls: payloads omitted, turn exceeded {MaxTotalChars} chars]"),
            });
        return slim.ToJsonString();
    }

    // Replaces secret values anywhere in the argument object, at any depth, and caps
    // what survives. Returns a node rather than a string so the stored JSON stays
    // structured and greppable.
    public static JsonNode? Redact(JsonElement args, int maxChars = MaxArgumentChars)
    {
        if (args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;

        var node = RedactNode(args);
        var json = node?.ToJsonString() ?? "null";
        return json.Length <= maxChars
            ? node
            : JsonValue.Create(json[..maxChars] + $"…[truncated {json.Length - maxChars} chars]");
    }

    private static JsonNode? RedactNode(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var obj = new JsonObject();
                foreach (var p in el.EnumerateObject())
                    obj[p.Name] = SecretArgumentNames.Contains(p.Name)
                        ? JsonValue.Create(RedactedMarker)
                        : RedactNode(p.Value);
                return obj;
            }
            case JsonValueKind.Array:
            {
                var arr = new JsonArray();
                foreach (var item in el.EnumerateArray()) arr.Add(RedactNode(item));
                return arr;
            }
            default:
                return JsonNode.Parse(el.GetRawText());
        }
    }

    private static JsonNode? Truncate(JsonElement value, int maxChars)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        var raw = value.GetRawText();
        return raw.Length <= maxChars
            ? JsonNode.Parse(raw)
            : JsonValue.Create(raw[..maxChars] + $"…[truncated {raw.Length - maxChars} chars]");
    }

    public static string Cap(string? text, int maxChars = MaxTextChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxChars ? text : text[..maxChars] + $"\n…[truncated {text.Length - maxChars} chars]";
    }
}
