using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Ai.SelfCorrection;

// Seeds curated "knowledge" learnings (IsSystem = true) once, at
// boot. These are network-focused escalate hints that go beyond the engine's generic
// category hints. Idempotent: skipped if any system knowledge already exists.
public static class LearningsSeedService
{
    private const string EscalateStrategy = AgentLearning.StrategyEscalate;

    private static readonly (string Pattern, string Category, string Service, string Tool, string FixJson)[] Seed =
    [
        ("host key (mismatch|verification failed)", ErrorClassifier.Connection, "", "device_connect",
            """{"message":"SSH host-key mismatch — the device key changed. Verify the device and update its expected fingerprint before retrying."}"""),
        ("is not in inventory|device .* not found", ErrorClassifier.NotFound, "", "",
            """{"message":"The device is not registered in inventory. Register it (with a credential) or run sync_netbox_inventory before connecting."}"""),
        ("no credential assigned|has no credential", ErrorClassifier.Validation, "", "device_connect",
            """{"message":"The device has no SSH credential assigned. Assign a credential to the device before connecting."}"""),
        ("push failed|push rejected|protected branch", ErrorClassifier.Connection, "", "",
            """{"message":"The git push was rejected — check the token's write scope and whether the branch is protected."}"""),
    ];

    public static async Task SeedSystemKnowledgeAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var already = await db.AgentLearnings
            .AnyAsync(l => l.IsSystem, ct);
        if (already) return;

        var now = DateTime.UtcNow;
        foreach (var (pattern, category, service, tool, fixJson) in Seed)
        {
            db.AgentLearnings.Add(new AgentLearning
            {
                AgentLearningId = Guid.NewGuid(),
                ErrorPattern = pattern,
                ErrorCategory = category,
                ServiceType = service,
                ToolName = tool,
                FixStrategy = EscalateStrategy,
                FixParamsJson = fixJson,
                Category = AgentLearning.CategoryKnowledge,
                IsSystem = true,
                Confidence = 0.8,
                SuccessCount = 0,
                FailureCount = 0,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("learnings.seed.done count={Count}", Seed.Length);
    }
}
