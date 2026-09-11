using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Ai.Seed;

// Seeds a conservative baseline of Python modules, so python_snippet is usable on
// a fresh install without an admin first hand-populating the allowlist.
//
// Purely computational modules only — nothing here opens a socket, and nothing is
// seeded with RequiresNetwork. `socket`, `urllib` and friends are deliberately
// absent even as network-gated entries: the network opt-in should mean an admin
// consciously added a network module, not that flipping one flag unlocked a
// pre-approved set. (`open` and filesystem access are a sandbox-level concern the
// allowlist never governed; see PythonSandbox.)
//
// Idempotent per module, checked against ALL rows including soft-deleted ones: an
// admin who removed `random` from the allowlist decided something, and a redeploy
// must not quietly reverse it.
public static class PythonModuleSeeder
{
    private static readonly (string Module, string Description)[] Baseline =
    [
        ("json", "Parse and produce JSON"),
        ("re", "Regular expressions"),
        ("math", "Mathematical functions"),
        ("statistics", "Mean, median, stdev"),
        ("datetime", "Dates and times"),
        ("time", "Timestamps and sleep"),
        ("collections", "Counter, defaultdict, deque"),
        ("itertools", "Iterator building blocks"),
        ("functools", "reduce, lru_cache, partial"),
        ("string", "String constants and templates"),
        ("textwrap", "Text wrapping and dedent"),
        ("base64", "Base64 encoding"),
        ("hashlib", "Hashes and digests"),
        ("uuid", "UUID generation and parsing"),
        ("ipaddress", "IP address and network arithmetic"),
        ("csv", "CSV reading and writing"),
        ("difflib", "Diffs between sequences"),
        ("random", "Pseudo-random values"),
    ];

    public static async Task SeedAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = new HashSet<string>(
            await db.AllowedPythonModules.AsNoTracking().Select(m => m.Module).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var seeded = 0;
        foreach (var (module, description) in Baseline)
        {
            if (existing.Contains(module)) continue;
            db.AllowedPythonModules.Add(new AllowedPythonModule
            {
                AllowedPythonModuleId = Guid.NewGuid(),
                Module = module,
                Description = description,
                RequiresNetwork = false,
                // Explicit rather than leaning on the property defaults: Status is what
                // gates an import now, so a seeded row landing as anything other than
                // ready would break python_snippet on a fresh install.
                Source = AllowedPythonModule.SourceStdlib,
                Status = AllowedPythonModule.StatusReady,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            seeded++;
        }

        if (seeded == 0) return;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("python.modules.seeded count={Count}", seeded);
    }
}
