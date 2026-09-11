namespace nashira_backend.Services.Ai.Specs;

// Lightweight metadata for one OpenAPI operation, held in the in-memory index
// for search. Full parameter/body schema is sliced from the raw YAML on demand
// (OperationYamlSlicer) — keeping the index lean.
public sealed class ApiOperation
{
    public required string OperationId { get; init; }
    public required string Api { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = [];
}
