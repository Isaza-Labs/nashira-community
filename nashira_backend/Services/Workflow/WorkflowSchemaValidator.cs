using System.Text.Json;
using Json.Schema;

namespace nashira_backend.Services.Workflow;

public sealed record WorkflowSchemaError(string Keyword, string Path);

public sealed record WorkflowSchemaValidationResult(bool Valid, IReadOnlyList<WorkflowSchemaError> Errors);

// Validates a workflow.v1 document against the reified JSON Schema (JsonSchema.Net).
// The schema text is supplied by the caller — the conformance kit ships the canonical
// workflow.v1.schema.json, so the engine never hard-codes the contract. The conformance
// vectors assert structural validity (valid true/false); detailed error extraction is a
// later enhancement.
public sealed class WorkflowSchemaValidator
{
    private readonly JsonSchema _schema;

    public WorkflowSchemaValidator(string schemaJson) => _schema = JsonSchema.FromText(schemaJson);

    public WorkflowSchemaValidationResult Validate(JsonElement document)
    {
        var results = _schema.Evaluate(document);
        return new WorkflowSchemaValidationResult(results.IsValid, []);
    }
}
