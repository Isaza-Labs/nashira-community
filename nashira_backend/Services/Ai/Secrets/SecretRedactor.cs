using System.Text;
using System.Text.Json;

namespace nashira_backend.Services.Ai.Secrets;

// Masks exact secret values, including the JSON-string form, in text that did not come from a
// secret-consuming execution. It does not decide whether output may be stored. Steps that
// resolved secret material are withheld entirely before this runs.
internal static class SecretRedactor
{
    public const string Mask = "[redacted]";

    // Below this length a value is masked only when it is the whole string. A two-character
    // password found inside other text is far more likely to be coincidence than exposure, and
    // masking it would garble the output.
    internal const int MinSubstringLength = 4;

    public static string? Redact(string? text, IReadOnlyCollection<string> secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets.Count == 0) return text;
        return Plan.For(secrets).Apply(text);
    }

    public static JsonElement Redact(JsonElement element, IReadOnlyCollection<string> secrets)
    {
        if (secrets.Count == 0 || element.ValueKind == JsonValueKind.Undefined) return element;
        var plan = Plan.For(secrets);
        if (plan.IsEmpty) return element;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, element, plan);
        using var doc = JsonDocument.Parse(buffer.ToArray());
        return doc.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement el, Plan plan)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(plan.Apply(el.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in el.EnumerateObject())
                {
                    writer.WritePropertyName(plan.Apply(prop.Name));
                    Write(writer, prop.Value, plan);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in el.EnumerateArray()) Write(writer, item, plan);
                writer.WriteEndArray();
                break;
            default:
                // Numbers, booleans, null: a four-digit secret must not turn a port into a mask.
                el.WriteTo(writer);
                break;
        }
    }

    private sealed class Plan
    {
        private readonly List<string> _needles = [];
        private readonly HashSet<string> _wholeOnly = new(StringComparer.Ordinal);

        public bool IsEmpty => _needles.Count == 0 && _wholeOnly.Count == 0;

        public static Plan For(IReadOnlyCollection<string> secrets)
        {
            var plan = new Plan();
            foreach (var secret in secrets)
            {
                if (string.IsNullOrEmpty(secret)) continue;
                if (secret.Length < MinSubstringLength)
                {
                    plan._wholeOnly.Add(secret);
                    continue;
                }
                plan._needles.Add(secret);
                var escaped = JsonEscaped(secret);
                if (!string.Equals(escaped, secret, StringComparison.Ordinal)) plan._needles.Add(escaped);
            }
            // Longest first, so a secret that contains another one is masked whole.
            plan._needles.Sort((a, b) => b.Length.CompareTo(a.Length));
            return plan;
        }

        // One left-to-right pass rather than a Replace per value: a value that happens to be
        // part of the mask text must not be masked again inside it.
        public string Apply(string text)
        {
            if (_wholeOnly.Contains(text)) return Mask;
            if (_needles.Count == 0) return text;

            StringBuilder? sb = null;
            var i = 0;
            var copiedUpTo = 0;
            while (i < text.Length)
            {
                var hit = 0;
                foreach (var needle in _needles)
                {
                    if (string.CompareOrdinal(text, i, needle, 0, needle.Length) == 0)
                    {
                        hit = needle.Length;
                        break;
                    }
                }
                if (hit == 0)
                {
                    i++;
                    continue;
                }
                sb ??= new StringBuilder(text.Length);
                sb.Append(text, copiedUpTo, i - copiedUpTo).Append(Mask);
                i += hit;
                copiedUpTo = i;
            }
            if (sb is null) return text;
            return sb.Append(text, copiedUpTo, text.Length - copiedUpTo).ToString();
        }
    }

    // The body of a JSON string literal as Python's json.dumps writes it (ensure_ascii on):
    // quotes, backslashes, control characters and non-ASCII escaped, everything else as is.
    internal static string JsonEscaped(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7F) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
