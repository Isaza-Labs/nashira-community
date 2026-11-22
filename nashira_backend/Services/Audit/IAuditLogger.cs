namespace nashira_backend.Services.Audit;

// Append-only write surface for the tenant audit trail. Each call persists one
// hash-chained AuditEvent capturing who mutated what (the executed artifact) and
// when. Writes are serialized per company to keep the chain consistent.
public interface IAuditLogger
{
    // Security mutations require their pending tracked changes and the audit row
    // to be persisted in the same SaveChanges transaction, or neither to succeed.
    Task LogRequiredAsync(string entityType, Guid? entityId, string action,
        object? after = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Required audit persistence is not implemented.");

    Task LogAsync(
        string entityType,
        Guid? entityId,
        string action,
        object? before = null,
        object? after = null,
        CancellationToken ct = default);
}
