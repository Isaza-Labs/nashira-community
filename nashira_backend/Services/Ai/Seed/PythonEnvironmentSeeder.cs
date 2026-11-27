using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Ai.Seed;

// Seeds `ssh-legacy`, so a device whose SSH server only offers `ssh-rsa` works out of
// the box instead of after someone works out why.
//
// paramiko 5 removed the `ssh-rsa` host key algorithm entirely, and the image installs
// whatever paramiko is newest. A switch such as Extreme EXOS on OpenSSH 8.1 offers
// nothing else, so every connection to it failed with
//   IncompatiblePeer: Incompatible ssh peer (no acceptable host key)
// paramiko 3.5 and 4.0 still negotiate it. This environment pins below 5 and is the SSH
// fallback: the runner retries a connection that fails that way once with it.
//
// Only when the table is EMPTY — soft-deleted rows included. An admin who deleted or
// renamed the seeded environment decided something, and a restart must not undo it.
public static class PythonEnvironmentSeeder
{
    public const string SshLegacyName = "ssh-legacy";

    public const string SshLegacyRequirements =
        "netmiko>=4,<5\n"
        + "paramiko>=3.5,<5\n"
        + "textfsm>=1.1\n"
        + "ntc-templates>=4";

    public const string SshLegacyDescription =
        "paramiko < 5 for SSH servers that only offer the ssh-rsa host key algorithm (for example "
        + "Extreme EXOS on OpenSSH 8.1). paramiko 5 removed ssh-rsa, so those connections fail with "
        + "\"Incompatible ssh peer (no acceptable host key)\". This is the SSH fallback: a connection "
        + "failing that way is retried once with it.";

    public static async Task SeedAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await SeedAsync(db, ct))
            logger.LogInformation("python.env.seeded name={Name}", SshLegacyName);
    }

    // Returns whether a row was written. Split out so a test can drive it with a context.
    public static async Task<bool> SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.PythonEnvironments.AnyAsync(ct)) return false;

        var now = DateTime.UtcNow;
        db.PythonEnvironments.Add(new PythonEnvironment
        {
            PythonEnvironmentId = Guid.NewGuid(),
            Name = SshLegacyName,
            Description = SshLegacyDescription,
            PythonVersion = null,
            Requirements = SshLegacyRequirements,
            IsSshFallback = true,
            Status = PythonEnvironment.StatusPending,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
