using nashira_backend.Exceptions;

namespace nashira_backend.Services.Messaging;

// Shape check for the Azure Relay connection string a Teams channel stores in
// its app_token — the no-public-ingress transport `TeamsRelayHostedService`
// listens on.
//
// It lives here rather than inline in the controller because nothing downstream
// can report the mistake: HybridConnectionListener throws on a malformed value,
// the hosted service logs `teams.relay.error` and retries with backoff forever,
// and the admin is left with a channel that looks configured, shows "Azure
// Relay · no ingress" in the transport column, and never receives a message.
// The only place with somebody to tell is the save.
public static class TeamsRelayConnectionString
{
    // Validated, not parsed: the Relay SDK owns the parse. This only rejects the
    // values that cannot possibly work, so a connection string with a key the SDK
    // understands and this does not still gets through.
    public static void Validate(string? appToken)
    {
        if (string.IsNullOrWhiteSpace(appToken)) return;

        var cs = appToken.Trim();

        // The likeliest wrong paste by a wide margin: the details panel shows the
        // Hybrid Connection's public URL as what the Azure Bot's messaging
        // endpoint points at, one field away from where the credential goes.
        if (cs.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || cs.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException(
                "that is the Hybrid Connection's public URL, not its connection string. The URL is what the "
                + "Azure Bot's messaging endpoint points at; this field wants the listener credential from "
                + "Azure Relay → your Hybrid Connection → Shared access policies → Primary Connection String "
                + "(Endpoint=sb://…;SharedAccessKeyName=…;SharedAccessKey=…;EntityPath=…).",
                "teams_relay_url_not_connection_string");

        var missing = new List<string>();
        if (!cs.Contains("Endpoint=sb://", StringComparison.OrdinalIgnoreCase)) missing.Add("Endpoint=sb://…");
        if (!cs.Contains("SharedAccessKey=", StringComparison.OrdinalIgnoreCase)) missing.Add("SharedAccessKey=…");
        // EntityPath is what names the Hybrid Connection. A namespace-level policy
        // string omits it, and a listener built from that has nothing to attach to
        // — the one malformed value that looks entirely plausible.
        if (!cs.Contains("EntityPath=", StringComparison.OrdinalIgnoreCase)) missing.Add("EntityPath=…");

        if (missing.Count > 0)
            throw new ValidationException(
                $"the Azure Relay connection string is missing {string.Join(", ", missing)}. Copy the Primary "
                + "Connection String from the Hybrid Connection itself, not from the namespace — only the "
                + "connection-level policy carries EntityPath.",
                "teams_relay_connection_string_invalid");
    }
}
