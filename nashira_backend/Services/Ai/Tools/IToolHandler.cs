using System.Text.Json;

namespace nashira_backend.Services.Ai.Tools;

// One agent tool. Metadata (Name/Description/ParametersSchema) is read once at
// boot into the ToolRegistry; ExecuteAsync runs per dispatch on a scoped instance.
public interface IToolHandler
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }
    Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct);
}
