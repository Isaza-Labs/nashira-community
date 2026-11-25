using System.Text.Json;
using nashira_backend.Services.Ai.Secrets;

namespace nashira_backend.Services.Worker;

public static class StepOutputPrivacy
{
    public const string Redacted = "[redacted]";

    // Codes the host itself decides, before or instead of running any snippet or
    // integration code: the authorization decision on stored secrets and the Python
    // sandbox gates. They name a fixed condition and carry no payload, so keeping
    // them lets the caller tell "denied" or "sandbox not configured" from a generic
    // withheld failure. Anything else, including every code a handler builds from
    // a script, a remote response or a status line, is replaced with
    // `output_redacted`. Matching is exact and case-sensitive.
    private static readonly HashSet<string> HostCodes = new(StringComparer.Ordinal)
    {
        SecretResolver.AccessDeniedCode,
        "unsandboxed_admin_required",
        "sandbox_not_configured",
        "sandbox_invalid",
        "runtime_unavailable",
    };

    // Never expose the payload's shape, property names, scalar values, logs or
    // error text: all can encode a secret. Keep only host-controlled status.
    public static SnippetResult Withhold(SnippetResult result) => new()
    {
        Success = result.Success,
        Change = result.Change,
        Retryable = result.Retryable,
        ErrorCode = result.Success ? null
            : result.ErrorCode is { } code && HostCodes.Contains(code) ? code : "output_redacted",
        Output = JsonSerializer.SerializeToElement(Redacted),
        Logs = Redacted,
        Error = result.Success ? string.Empty : Redacted,
    };

    public static bool TryRead(JsonElement input, out bool redact)
    {
        redact = false;
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("redact_output", out var flag)) return true;
        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        redact = flag.GetBoolean();
        return true;
    }
}
