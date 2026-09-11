using nashira_backend.Data.Models;

namespace nashira_backend.Services.Ai.SelfCorrection;

// Persistence for agent learnings. Reads match a tenant's learnings plus the
// curated system knowledge (IsSystem).
public interface ILearningsStore
{
    // Best (highest-confidence) learning whose pattern matches the error, scoped to
    // the tool/service (or any). Null when nothing matches above the confidence floor.
    Task<AgentLearning?> FindBestFixAsync(string toolName, string serviceType, string errorMessage, CancellationToken ct);

    // Updates a learning's success/failure counters + confidence (tenant-owned only).
    Task RecordOutcomeAsync(Guid learningId, bool success, CancellationToken ct);

    // Records a newly discovered fix (category = learning) for the tenant, or bumps
    // an existing identical one.
    Task RecordDiscoveredAsync(
        string errorPattern, string errorCategory, string serviceType,
        string toolName, string fixStrategy, string? fixParamsJson, CancellationToken ct);
}
