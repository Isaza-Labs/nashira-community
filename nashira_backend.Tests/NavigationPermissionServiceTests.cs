using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Navigation;

namespace nashira_backend.Tests;

public class NavigationPermissionServiceTests
{
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"navigation-permissions-{Guid.NewGuid()}").Options);

    private static User SeedUser(AppDbContext db, string role = "viewer")
    {
        var user = new User
        {
            UserId = Guid.NewGuid(),
            Username = $"user-{Guid.NewGuid():N}",
            Email = "user@test.local",
            Role = role,
            IsActive = true,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task Effective_visibility_uses_user_override_before_role_override()
    {
        await using var db = Db();
        var user = SeedUser(db);
        var service = new NavigationPermissionService(db);

        await service.UpdateRoleAsync("viewer",
        [
            new("/devices", false),
            new("/reports", false),
        ], default);
        await service.UpdateUserAsync(user.UserId,
        [
            new("/reports", true),
        ], default);

        var effective = await service.GetEffectiveAsync(user.UserId, default);

        Assert.False(effective["/devices"]);
        Assert.True(effective["/reports"]);
    }

    [Fact]
    public async Task Null_visibility_removes_user_override_and_restores_role_value()
    {
        await using var db = Db();
        var user = SeedUser(db, "operator");
        var service = new NavigationPermissionService(db);

        await service.UpdateRoleAsync("operator", [new("/workflows", false)], default);
        await service.UpdateUserAsync(user.UserId, [new("/workflows", true)], default);
        await service.UpdateUserAsync(user.UserId, [new("/workflows", null)], default);

        var effective = await service.GetEffectiveAsync(user.UserId, default);
        var userOverrides = await service.ListUserAsync(user.UserId, default);

        Assert.False(effective["/workflows"]);
        Assert.Empty(userOverrides);
    }

    [Fact]
    public async Task Updates_reject_unknown_roles_and_invalid_page_keys()
    {
        await using var db = Db();
        var service = new NavigationPermissionService(db);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateRoleAsync("owner", [new("/devices", false)], default));
        await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateRoleAsync("viewer", [new("devices", false)], default));
    }

    [Fact]
    public void Management_endpoints_are_admin_only_but_current_user_endpoint_is_authenticated()
    {
        var controllerAuthorization = typeof(NavigationPermissionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>()
            .Single();
        var managedMethods = new[]
        {
            nameof(NavigationPermissionsController.GetRole),
            nameof(NavigationPermissionsController.UpdateRole),
            nameof(NavigationPermissionsController.GetUser),
            nameof(NavigationPermissionsController.UpdateUser),
        };

        Assert.Null(controllerAuthorization.Policy);
        Assert.DoesNotContain(
            typeof(NavigationPermissionsController)
                .GetMethod(nameof(NavigationPermissionsController.GetMine))!
                .GetCustomAttributes(typeof(AuthorizeAttribute), true)
                .Cast<AuthorizeAttribute>(),
            attribute => attribute.Policy is not null);
        foreach (var method in managedMethods)
        {
            Assert.Contains(
                typeof(NavigationPermissionsController)
                    .GetMethod(method)!
                    .GetCustomAttributes(typeof(AuthorizeAttribute), true)
                    .Cast<AuthorizeAttribute>(),
                attribute => attribute.Policy == "Admin");
        }
    }
}
