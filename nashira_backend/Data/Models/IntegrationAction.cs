namespace nashira_backend.Data.Models;

// One callable operation on an Integration: method + path plus the shape of its
// parameters. Rows are normally not hand-written — they are synced from the
// OpenAPI spec linked to the integration (AiApiSpec.IntegrationId), so the agent's
// discover/execute catalog and the workflow engine's action catalog are two views
// of one source instead of two lists that drift.
//
// `OperationId` is the join back to that spec's operation, and is what makes the
// sync idempotent across a re-import.
public class IntegrationAction : BaseModel
{
    public Guid IntegrationActionId { get; set; }
    public Guid IntegrationId { get; set; }

    // The spec's operationId. Unique per integration among active rows; null only
    // for actions created by hand rather than synced.
    public string? OperationId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = string.Empty;

    // JSON arrays of { name, in, required, type, description } as parsed from the
    // spec. Kept as text for the same reason the workflow DAG is: they are written
    // whole and read whole, never queried into.
    public string? PathParamsJson { get; set; }
    public string? QueryParamsJson { get; set; }
    public string? RequestBodyJson { get; set; }

    // Free-form grouping, seeded from the operation's first OpenAPI tag.
    public string Category { get; set; } = string.Empty;

    // Whether this operation changes state. Derived from the HTTP method on sync
    // (GET/HEAD/OPTIONS are reads) and overridable, because a spec that tunnels a
    // mutation through POST /search would otherwise be classified wrong — and this
    // flag is what the governance tier keys off.
    public bool ReadOnly { get; set; }

    // Admin on/off switch, distinct from IsActive (soft delete): an action can be
    // withdrawn from the catalog without losing its definition, and a re-sync will
    // not silently re-enable it.
    public bool Enabled { get; set; } = true;
}
