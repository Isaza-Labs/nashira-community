using System.Text;
using System.Text.Json.Serialization;

namespace nashira_backend.Services.Ai.Conversation;

// One tool call as the conversation history keeps it: enough to say what ran and
// what came of it, small enough to replay on every later turn.
public sealed record StoredToolCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("args")] string? Args,
    [property: JsonPropertyName("result")] string? Result);

// What actually ran in an earlier turn, written by the server and replayed to the model.
//
// The history used to keep only the text of each turn. Tool calls and their results
// were dropped, so on the next turn the model saw its own earlier words ("18 interfaces
// created, ids 95-112") and nothing to tell them apart from what had happened. Over a
// long thread it learned from that history that an answer is only text: it began to
// announce actions and end the turn without calling anything, and to report results of
// calls it never made. The record is the evidence it was missing, including the record
// that a turn ran no tool at all.
public static class ToolRunRecord
{
    private const int MaxArgsChars = 120;
    private const int MaxResultChars = 160;
    // Rendered once per earlier turn into a history with a character budget, so it is
    // kept well under that budget: past this the remaining calls are only counted.
    internal const int MaxRenderChars = 1_500;

    public static List<StoredToolCall> FromTelemetry(IReadOnlyList<ToolTelemetryEntry> calls) =>
        calls.Select(c => new StoredToolCall(
                c.Name,
                c.Ok,
                Clip(ToolTelemetry.Redact(c.Arguments, MaxArgsChars * 4)?.ToJsonString(), MaxArgsChars),
                Clip(c.Result.ValueKind is System.Text.Json.JsonValueKind.Undefined ? null : c.Result.GetRawText(),
                    MaxResultChars)))
            .ToList();

    // The note that goes in front of the user message that followed the turn. It is
    // addressed to the model, framed as the server's, and says plainly when nothing ran.
    public static string Render(IReadOnlyList<StoredToolCall> calls)
    {
        if (calls.Count == 0)
            return "[Nashira server record of your previous reply: it ran no tools, so nothing was read, "
                + "created or changed in it.]";

        var failed = calls.Count(c => !c.Ok);
        var sb = new StringBuilder()
            .Append("[Nashira server record of your previous reply — tools actually run: ")
            .Append(calls.Count)
            .Append(failed > 0 ? $" ({calls.Count - failed} ok, {failed} failed)" : " (all ok)")
            .Append('.');

        var listed = 0;
        foreach (var c in calls)
        {
            var line = $"\n- {c.Name} {c.Args} → {(c.Ok ? "ok" : "FAILED")}: {c.Result}";
            if (sb.Length + line.Length > MaxRenderChars) break;
            sb.Append(line);
            listed++;
        }
        if (listed < calls.Count)
            sb.Append($"\n- …and {calls.Count - listed} more calls not listed.");
        return sb.Append(']').ToString();
    }

    private static string? Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
