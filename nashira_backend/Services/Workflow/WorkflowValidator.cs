using System.Text.Json;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Workflow;

// Validates a workflow's nodes/edges against workflow.v1 (structural schema) and as an
// acyclic DAG, then returns the canonical SchemaHash. The single write-time gate for a
// workflow definition; throws ValidationException on any violation.
public sealed class WorkflowValidator
{
    private readonly WorkflowSchemaValidator _schema;

    public WorkflowValidator(WorkflowSchemaValidator schema) => _schema = schema;

    public string ValidateAndHash(JsonElement nodes, JsonElement edges)
    {
        var document = JsonSerializer.SerializeToElement(new { nodes, edges });
        if (!_schema.Validate(document).Valid)
            throw new ValidationException("workflow does not conform to workflow.v1");

        var dag = Dag.Parse(nodes, edges);
        dag.TopologicalOrder(); // rejects cycles

        return WorkflowCanonicalizer.ComputeSchemaHash(nodes, edges);
    }
}
