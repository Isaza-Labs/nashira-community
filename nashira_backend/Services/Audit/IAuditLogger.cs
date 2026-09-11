namespace nashira_backend.Services.Audit;

// Append-only write surface for the tenant audit trail. Each call persists one
// hash-chained AuditEvent capturing who mutated what (the executed artifact) and
// when. Writes are serialized per company to keep the chain consistent.
public interface IAuditLogger
{
    Task LogAsync(
        string entityType,
        Guid? entityId,
        string action,
        object? before = null,
        object? after = null,
        CancellationToken ct = default);
}
