using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Auth;

// Creates a deterministic company + admin user at startup so a fresh dev DB is
// immediately usable. Invoked ONLY in the Development environment — never
// production. The seeded credentials intentionally bypass IPasswordPolicy
// (hashing directly) so the weak dev password works; real create paths enforce
// the policy.
//
// Seeded: company slug "default", username "admin", password "admin", role admin.
public static class DevSeedService
{
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "admin";

    public static async Task SeedAsync(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var now = DateTime.UtcNow;

        var adminExists = await db.Users.AnyAsync(u => u.Username == DefaultAdminUsername);
        if (adminExists)
        {
            logger.LogInformation("Dev seed: admin already present, skipping");
            return;
        }

        var admin = new User
        {
            UserId = Guid.NewGuid(),
            Username = DefaultAdminUsername,
            Email = "admin@default.local",
            Role = "admin",
            PasswordChangedAt = now,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        admin.PasswordHash = hasher.HashPassword(admin, DefaultAdminPassword); // bypasses policy on purpose

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        logger.LogWarning(
            "Dev seed: created admin '{Username}' with password '{Password}'. Change before non-local use.",
            admin.Username, DefaultAdminPassword);
    }
}
