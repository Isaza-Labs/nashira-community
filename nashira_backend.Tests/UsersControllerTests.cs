using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.User;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Email;
using nashira_backend.Services.Identity;
using UserEntity = nashira_backend.Data.Models.User;

namespace nashira_backend.Tests;

// The self-protection rules: an admin cannot demote, deactivate or delete their own
// account. Privilege removal on an admin must come from another admin, so a tenant
// can never end up admin-less through one signed-in session's own actions.
public class UsersControllerTests
{
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"users-{Guid.NewGuid()}").Options);

    private sealed class AcceptAllPolicy : IPasswordPolicy
    {
        public PasswordValidationResult Validate(string password, string? username = null, string? email = null)
            => new(true, null);
    }

    private sealed class NullAudit : IAuditLogger
    {
        public Task LogAsync(
            string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeCredentialsNotifier : IUserCredentialsNotifier
    {
        public string? Warning;
        public (string To, string Username, string Password)? Sent;

        public Task<string?> SendCredentialsAsync(
            string toAddress, string username, string password, CancellationToken ct)
        {
            Sent = (toAddress, username, password);
            return Task.FromResult(Warning);
        }
    }

    private static UserEntity Seed(AppDbContext db, string username, string role)
    {
        var user = new UserEntity
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@test.local",
            Role = role,
            IsActive = true,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static UsersController Controller(
        AppDbContext db, UserEntity actingAs, IUserCredentialsNotifier? notifier = null)
    {
        var identity = new MutableCurrentUser();
        identity.Bind(actingAs.UserId, actingAs.Username, [actingAs.Role]);
        return new UsersController(
            db, new PasswordHasher<UserEntity>(), new AcceptAllPolicy(), identity, new NullAudit(),
            notifier ?? new FakeCredentialsNotifier());
    }

    [Fact]
    public async Task Admin_cannot_change_their_own_role()
    {
        using var db = Db();
        var admin = Seed(db, "admin", "admin");
        var controller = Controller(db, admin);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            controller.Update(admin.UserId, new UpdateUser { Role = "viewer" }, default));

        Assert.Equal("self_demote", ex.Code);
        Assert.Equal("admin", (await db.Users.SingleAsync(u => u.UserId == admin.UserId)).Role);
    }

    [Fact]
    public async Task Admin_cannot_deactivate_their_own_user()
    {
        using var db = Db();
        var admin = Seed(db, "admin", "admin");
        var controller = Controller(db, admin);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            controller.Update(admin.UserId, new UpdateUser { IsActive = false }, default));

        Assert.Equal("self_deactivate", ex.Code);
        Assert.True((await db.Users.SingleAsync(u => u.UserId == admin.UserId)).IsActive);
    }

    [Fact]
    public async Task Admin_can_update_their_own_email_and_resend_same_role()
    {
        using var db = Db();
        var admin = Seed(db, "admin", "admin");
        var controller = Controller(db, admin);

        // Frontends commonly send the full form back; an unchanged role must not trip
        // the guard, and non-privilege fields on your own account stay editable.
        await controller.Update(
            admin.UserId, new UpdateUser { Email = "new@test.local", Role = "admin" }, default);

        var row = await db.Users.SingleAsync(u => u.UserId == admin.UserId);
        Assert.Equal("new@test.local", row.Email);
        Assert.Equal("admin", row.Role);
    }

    [Fact]
    public async Task Another_admin_can_demote_an_admin()
    {
        using var db = Db();
        var acting = Seed(db, "root", "admin");
        var other = Seed(db, "colleague", "admin");
        var controller = Controller(db, acting);

        await controller.Update(other.UserId, new UpdateUser { Role = "operator" }, default);

        Assert.Equal("operator", (await db.Users.SingleAsync(u => u.UserId == other.UserId)).Role);
    }

    [Fact]
    public async Task Admin_cannot_delete_their_own_user()
    {
        using var db = Db();
        var admin = Seed(db, "admin", "admin");
        var controller = Controller(db, admin);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            controller.Delete(admin.UserId, default));

        Assert.Equal("self_delete", ex.Code);
    }

    [Fact]
    public async Task Create_emails_credentials_to_the_new_user()
    {
        using var db = Db();
        var admin = Seed(db, "admin", "admin");
        var notifier = new FakeCredentialsNotifier();
        var controller = Controller(db, admin, notifier);

        var result = await controller.Post(new CreateUser
        {
            Username = "newbie",
            Email = "newbie@test.local",
            Password = "S3cret!pass",
            Role = "viewer",
        }, default);

        Assert.Equal(("newbie@test.local", "newbie", "S3cret!pass"), notifier.Sent);
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<UserResponse>(created.Value);
        Assert.True(response.CredentialsEmailSent);
        Assert.Null(response.Warning);
    }

    [Fact]
    public async Task Create_warns_the_admin_when_the_credentials_email_is_not_sent()
    {
        using var db = Db();
        var admin = Seed(db, "admin", "admin");
        var notifier = new FakeCredentialsNotifier
        {
            Warning = "The email service is not configured, so the credentials email was not sent.",
        };
        var controller = Controller(db, admin, notifier);

        var result = await controller.Post(new CreateUser
        {
            Username = "newbie",
            Email = "newbie@test.local",
            Password = "S3cret!pass",
        }, default);

        // The user exists either way; only the delivery differs.
        Assert.True(await db.Users.AnyAsync(u => u.Username == "newbie"));
        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<UserResponse>(created.Value);
        Assert.False(response.CredentialsEmailSent);
        Assert.Equal(notifier.Warning, response.Warning);
    }
}
