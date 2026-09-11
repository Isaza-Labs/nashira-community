using System.Text.Json;

namespace nashira_backend.Services.Ai.Tools;

public static class ToolSchemas
{
    // A parameter-less tool schema. Cloned so it's self-contained (independent
    // of the parsing JsonDocument's lifetime).
    public static readonly JsonElement Empty =
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();
}
