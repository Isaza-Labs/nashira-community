namespace nashira_backend.Services.Workflow;

// The input keys each snippet type is contractually allowed to carry — canonical keys
// and the aliases a handler must accept — transcribed from
// workflow-v1-conformance/snippets/SPEC.md. Keep the two in step: the spec is the
// source, this is its encoding.
//
// Rule 3 of that document is why this exists: a handler ignores keys it does not
// know, so a typo in a bundle (`comands`) or a product-specific extension the other
// side never had would surface as a step that silently did something else at 3am.
// The importer compares each node's config_overrides against this table and notes
// every key no alias covers, per node, at import time — visible, not fatal.
public static class SnippetKeyCatalog
{
    // Accepted on every type: a stricter-only idempotency override (execution/SPEC.md
    // §2). It is noted separately because it changes retry behaviour.
    public const string IdempotencyOverride = "idempotency";

    private static readonly Dictionary<string, HashSet<string>> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ping"] = Set("host", "device", "count", "timeout_ms", "port"),
        ["ssh"] = Set(
            "commands", "command", "device", "host", "credential", "credential_id",
            "username", "password", "private_key", "key_passphrase",
            "use_structured", "structured", "stop_on_error", "enable_secret", "enable",
            "timeout_seconds", "port", "device_type", "setup_commands", "read_until_pattern",
            "direct_exec", "preserve_ansi", "use_timing"),
        // Raw form (url…) and catalogued form (source…) share one type; the union is
        // what is known, the handler picks the form by which key is present.
        ["rest_call"] = Set(
            "url", "method", "headers", "body", "query",
            "source", "operation_id", "path_params", "query_params"),
        ["transform"] = Set("expression", "input", "language", "mapping"),
        ["integration_action"] = Set(
            "integration", "action", "integration_id", "action_id",
            "body", "params", "path_params", "query", "query_params"),
        ["mcp_call"] = Set("server", "mcp_server_id", "tool", "tool_name", "arguments"),
        ["git"] = Set(
            "operation", "repository", "repository_id", "path", "paths", "ref", "branch",
            "content", "commit_message", "push", "author_name", "author_email"),
        ["report"] = Set("format", "document", "content", "retain_days"),
        ["email_send"] = Set(
            "to", "cc", "bcc", "subject", "body", "html", "from_address", "from_name",
            "reply_to", "attachments", "channel", "channel_id"),
        ["slack_message"] = Set("channel", "text", "thread_ts", "blocks", "via"),
        ["ansible_playbook"] = Set("hosts", "device", "host", "targets", "extra_vars", "timeout_seconds"),
    };

    // Types whose payload is free-form by contract (a python script reads whatever it
    // is given; the mailbox extension and the unimplemented protocols have no table).
    // Nothing can be "unknown" for them, so they are never noted.
    private static readonly HashSet<string> Open = new(StringComparer.OrdinalIgnoreCase)
    {
        "python_snippet", "email_mailbox", "netconf", "snmp_v3",
    };

    // ssh material that must never travel as a plain value, in EITHER direction —
    // refused on import and refused on export (snippets/SPEC.md, `ssh`).
    //
    // `username` is deliberately not here. The spec calls it an identity, not a
    // secret: a plain username travels like every other identity in this contract, and
    // the handler already reads it that way — it resolves a `${secret:…}` username and
    // accepts a literal one.
    public static readonly IReadOnlySet<string> SshSecretKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "password", "private_key", "key_passphrase",
    };

    /// <summary>
    /// The node-level change declaration (`workflow-v1/run-outcome`), and the sibling of
    /// the idempotency override — one says whether an action could be undone, the other
    /// whether anything was done.
    /// </summary>
    /// <remarks>
    /// Excluded from <see cref="UnknownKeys"/> for the same reason as the tier: "no known key
    /// covers this" is the wrong thing to say about a key the contract names. Left in, the
    /// note claimed a possible typo AND that the handler ignores the key — and the executor
    /// reads it, failing the step when a deferring type has no declaration anywhere.
    /// </remarks>
    public const string ChangesOverride = "changes";

    /// <summary>True when the spec defines a key table for this type.</summary>
    public static bool HasContract(string type) => Keys.ContainsKey(type);

    /// <summary>
    /// The keys in <paramref name="present"/> that neither the canonical set nor any
    /// alias for <paramref name="type"/> covers, in the order given. Empty for open
    /// types and for types without a contract.
    /// </summary>
    public static IReadOnlyList<string> UnknownKeys(string type, IEnumerable<string> present)
    {
        if (Open.Contains(type) || !Keys.TryGetValue(type, out var known)) return [];
        return present
            .Where(k => !known.Contains(k)
                        && !string.Equals(k, IdempotencyOverride, StringComparison.Ordinal)
                        && !string.Equals(k, ChangesOverride, StringComparison.Ordinal))
            .ToList();
    }

    private static HashSet<string> Set(params string[] keys) => new(keys, StringComparer.Ordinal);
}
