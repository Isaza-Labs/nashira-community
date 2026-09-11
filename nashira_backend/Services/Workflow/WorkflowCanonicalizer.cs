using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace nashira_backend.Services.Workflow;

// Canonical SchemaHash for a workflow.v1 DAG, per the workflow-v1-conformance
// canonicalization/SPEC.md (reified from flow-weaver, oracle fw@95fc69e). Deterministic:
// object keys sorted ordinal, array order preserved, numbers trailing-zero-stripped. This is
// the fingerprint the simulation-staleness gate compares; the canonical engine (Phase 5.5)
// reuses it, and the workflow.v1 conformance suite verifies it.
public static class WorkflowCanonicalizer
{
    public static string ComputeSchemaHash(JsonElement nodes, JsonElement edges)
    {
        var sb = new StringBuilder();
        WriteCanonical(sb, nodes);
        sb.Append('|');
        WriteCanonical(sb, edges);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    // Canonical serialization of a single JSON value (exposed for the conformance adapter).
    public static string Canonical(JsonElement value)
    {
        var sb = new StringBuilder();
        WriteCanonical(sb, value);
        return sb.ToString();
    }

    private static void WriteCanonical(StringBuilder sb, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                sb.Append("null");
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Number:
                if (el.TryGetDecimal(out var dec))
                    sb.Append(StripTrailingZeros(dec.ToString(CultureInfo.InvariantCulture)));
                else if (el.TryGetDouble(out var dbl))
                    sb.Append(dbl.ToString("R", CultureInfo.InvariantCulture));
                else
                    sb.Append(el.GetRawText());
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(el.GetString()));
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    WriteCanonical(sb, item);
                    firstItem = false;
                }
                sb.Append(']');
                break;
            case JsonValueKind.Object:
                sb.Append('{');
                var props = el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonSerializer.Serialize(props[i].Name));
                    sb.Append(':');
                    WriteCanonical(sb, props[i].Value);
                }
                sb.Append('}');
                break;
        }
    }

    internal static string StripTrailingZeros(string s)
    {
        if (!s.Contains('.', StringComparison.Ordinal)) return s;
        s = s.TrimEnd('0');
        if (s.EndsWith('.')) s = s[..^1];
        return s;
    }
}
