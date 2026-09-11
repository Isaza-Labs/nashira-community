using System.Text.Json;
using System.Text.Json.Nodes;

namespace nashira_backend.Services.Workflow;

/// <summary>
/// Rewrites a resolved step payload into the CANONICAL input keys of
/// workflow-v1-conformance/snippets/SPEC.md, immediately before the handler sees it.
/// </summary>
/// <remarks>
/// <para>
/// Rule 1 of that document makes Flow Weaver the oracle for payload keys: where the two
/// products used different keys for the same thing, the FW key is canonical and the
/// Nashira key is an accepted alias. Handlers here already read both, one
/// <c>?? Str(input, "…")</c> at a time — which is fine right up to the moment two of
/// them disagree about which side wins. A node carrying BOTH <c>tool</c> and
/// <c>tool_name</c> called the wrong tool for exactly that reason: the precedence was
/// inverted in one handler and nowhere else, and no test could see it because there was
/// no single place the question was answered.
/// </para>
/// <para>
/// This is that place. bundle/SPEC.md §4 fixes the rule — <em>when both are present the
/// canonical one wins</em> — and applying it once, on the way in, means a handler can
/// stop guessing and a conformance vector has something real to assert against.
/// </para>
/// <para><b>What is deliberately NOT here.</b></para>
/// <list type="bullet">
/// <item><description>
/// The <b>legacy id keys</b> (<c>credential_id</c>, <c>integration_id</c>,
/// <c>action_id</c>, <c>mcp_server_id</c>, <c>repository_id</c>, <c>channel_id</c>).
/// Those are translated at IMPORT, against the mapping the bundle carries
/// (<see cref="NodeReferences.TranslateLegacyKeys"/>), because a foreign GUID cannot be
/// turned into a local name by looking at the payload. Two of them are then written back
/// onto the stored row on purpose — the git handler reads <c>repository_id</c> — so
/// dropping them here would break the very nodes the importer just fixed.
/// </description></item>
/// <item><description>
/// The aliases whose value is in a <b>different language</b> from the canonical key:
/// <c>transform.mapping</c> (a path map, not a JMESPath expression) and
/// <c>report.content</c> (a markdown string, not a structured document). The spec calls
/// them equivalent and they are, but the equivalence is a translation, not a rename, and
/// performing it here would quietly change which tables a `csv`/`xlsx` export sees. Both
/// still get the canonical-wins rule, which is the half that was actually broken.
/// </description></item>
/// <item><description>
/// <c>ssh.host</c> and <c>ansible_playbook.host</c>. The spec lists them as aliases, but
/// the value is a literal address the handler resolves BY IP, not an inventory name;
/// renaming one to <c>device</c>/<c>hosts</c> would turn an address into a name lookup
/// and fail with <c>not_found</c> on a device that is right there.
/// </description></item>
/// </list>
/// </remarks>
public static class SnippetInputNormalizer
{
    /// <summary>How an alias relates to its canonical key.</summary>
    private enum Shape
    {
        /// <summary>Same value, different name.</summary>
        Rename,

        /// <summary>Scalar alias, array canonical: the value is wrapped in a one-element array.</summary>
        RenameIntoArray,

        /// <summary>
        /// Same meaning, different value language: the alias is left where it is, but the
        /// canonical key still wins when both are present.
        /// </summary>
        CanonicalWinsOnly,
    }

    private readonly record struct Alias(string Canonical, string Name, Shape Shape);

    // Applied in order, so an earlier alias that fills the canonical key makes a later
    // one a duplicate — which is what `hosts` / `targets` / `device` need.
    private static readonly Dictionary<string, Alias[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ping"] = [new("host", "device", Shape.Rename)],
        ["ssh"] =
        [
            new("commands", "command", Shape.RenameIntoArray),
            new("use_structured", "structured", Shape.Rename),
            // Only the string form: `enable_secret` is a ${secret:…} reference or a
            // credential field, while Nashira also accepts `enable: true` meaning
            // "reuse the password". The boolean is not the same key wearing another
            // name, so it is left for the handler.
            new("enable_secret", "enable", Shape.Rename),
        ],
        ["integration_action"] =
        [
            new("params", "path_params", Shape.Rename),
            new("query", "query_params", Shape.Rename),
        ],
        ["mcp_call"] = [new("tool", "tool_name", Shape.Rename)],
        ["transform"] = [new("expression", "mapping", Shape.CanonicalWinsOnly)],
        ["report"] = [new("document", "content", Shape.CanonicalWinsOnly)],
        ["ansible_playbook"] =
        [
            new("hosts", "targets", Shape.Rename),
            new("hosts", "device", Shape.RenameIntoArray),
        ],
    };

    /// <summary>Types whose aliases only the string form of the alias may carry.</summary>
    private static readonly HashSet<string> StringOnlyAliases = new(StringComparer.Ordinal) { "enable" };

    /// <summary>
    /// <paramref name="input"/> with every alias of <paramref name="type"/> resolved to
    /// its canonical key. A payload with no aliases, a non-object payload, and a type
    /// with no table all come back unchanged.
    /// </summary>
    public static JsonElement Normalize(string? type, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return input;
        if (type is null || !Aliases.TryGetValue(type.Trim(), out var aliases)) return input;
        if (!aliases.Any(a => Has(input, a.Name))) return input;

        if (JsonNode.Parse(input.GetRawText()) is not JsonObject payload) return input;

        foreach (var alias in aliases)
        {
            if (!payload.ContainsKey(alias.Name)) continue;
            var value = payload[alias.Name];

            if (alias.Shape == Shape.CanonicalWinsOnly)
            {
                // The canonical key decides; the alias only stands in for it when the
                // canonical key is absent, and then it is the handler that reads it.
                if (Present(payload, alias.Canonical)) payload.Remove(alias.Name);
                continue;
            }

            if (StringOnlyAliases.Contains(alias.Name) && value is not JsonValue sv) continue;
            if (StringOnlyAliases.Contains(alias.Name)
                && value is JsonValue v && !v.TryGetValue<string>(out _)) continue;

            payload.Remove(alias.Name);

            // Canonical wins: the alias is dropped, not merged. Merging is how an
            // `ansible_playbook` node that kept both keys ended up running against the
            // UNION of them — a wider blast radius than either key alone, on a type
            // that cannot be rolled back.
            if (Present(payload, alias.Canonical)) continue;

            payload[alias.Canonical] = alias.Shape == Shape.RenameIntoArray && value is not JsonArray
                ? new JsonArray(value?.DeepClone())
                : value?.DeepClone();
        }

        return JsonDocument.Parse(payload.ToJsonString()).RootElement.Clone();
    }

    private static bool Has(JsonElement input, string key) => input.TryGetProperty(key, out _);

    // A null value is not a value: `{"host": null, "device": "core-1"}` means the author
    // wrote one of them and the exporter emitted the other empty, and dropping the alias
    // there would leave the node with nothing to connect to.
    private static bool Present(JsonObject payload, string key) =>
        payload.TryGetPropertyValue(key, out var v) && v is not null;
}
