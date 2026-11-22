using System.Text.Json;

namespace nashira_backend.Services.Worker;

public static class StepOutputPrivacy
{
    public const string Redacted = "[redacted]";

    // Never expose the payload's shape, property names, scalar values, logs or
    // error text: all can encode a secret. Keep only host-controlled status.
    public static SnippetResult Withhold(SnippetResult result) => new()
    {
        Success = result.Success,
        Change = result.Change,
        Retryable = result.Retryable,
        ErrorCode = result.Success ? null : "output_redacted",
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
