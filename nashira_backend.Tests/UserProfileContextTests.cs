using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Identity;

namespace nashira_backend.Tests;

// The per-user persona that rides into a turn as a second system message. These
// pin the two halves: what the loader reads for the current user, and the exact
// text the model sees (the `[USER PROFILE CONTEXT]` block netora's agent used).
public class UserProfileContextTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"user-profile-{Guid.NewGuid()}")
            .Options);

    private sealed class FakeUser(Guid id, bool authed = true) : ICurrentUser
    {
        public Guid UserId => id;
        public string? Username => "tester";
        public IReadOnlyList<string> Roles => ["viewer"];
        public bool IsAuthenticated => authed;
    }

    private static User NewUser(Guid id, Guid? profileId = null, string? customText = null) => new()
    {
        UserId = id,
        Username = $"u-{id:N}",
        Email = $"{id:N}@example.test",
        PasswordHash = "x",
        Role = "viewer",
        ProfileId = profileId,
        CustomProfileText = customText,
        IsActive = true,
    };

    private static Profile Expert(Guid id) => new()
    {
        ProfileId = id,
        Name = "expert",
        DisplayName = "Technical Expert",
        Skills = ["networking", "automation"],
        ResponseStyle = "Terse, CLI-first, assume deep knowledge.",
        IsActive = true,
    };

    // ── Render ──────────────────────────────────────────────────────

    [Fact]
    public void Render_carries_profile_skills_style_and_custom_text()
    {
        var ctx = new UserProfileContext(
            "expert", "Technical Expert", ["networking", "automation"], "Terse, CLI-first.", "I run the Madrid NOC.");

        var text = ctx.Render();

        Assert.NotNull(text);
        Assert.StartsWith(UserProfileContext.Header + "\n", text);
        Assert.Contains("Profile: Technical Expert (expert)\n", text);
        Assert.Contains("Skills: networking, automation\n", text);
        Assert.Contains("Response Style: Terse, CLI-first.\n", text);
        Assert.Contains("User's Additional Context: I run the Madrid NOC.\n", text);
        // The persona shapes answers, never permissions — the block says so itself.
        Assert.Contains("never changes which tools you may call", text);
    }

    [Fact]
    public void Render_without_skills_or_style_says_none_and_skips_the_style_line()
    {
        var text = new UserProfileContext("basic", "Basic User", [], null, null).Render();

        Assert.NotNull(text);
        Assert.Contains("Skills: None\n", text);
        Assert.DoesNotContain("Response Style:", text);
        Assert.DoesNotContain("Additional Context", text);
    }

    // A user with no profile can still tell the assistant about themselves; that
    // alone is worth a message.
    [Fact]
    public void Render_with_only_custom_text_has_no_profile_line()
    {
        var text = new UserProfileContext(null, null, [], null, "Prefer answers in Spanish.").Render();

        Assert.NotNull(text);
        Assert.DoesNotContain("Profile:", text);
        Assert.Contains("User's Additional Context: Prefer answers in Spanish.\n", text);
    }

    [Fact]
    public void Render_is_null_when_there_is_nothing_to_say()
    {
        Assert.Null(new UserProfileContext(null, null, [], null, null).Render());
        Assert.Null(new UserProfileContext(null, null, [], null, "   ").Render());
        Assert.True(new UserProfileContext(" ", null, [], null, null).IsEmpty);
    }

    // ── Loader ──────────────────────────────────────────────────────

    [Fact]
    public async Task Loader_joins_the_assigned_profile_for_the_current_user()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        await using var db = NewDb();
        db.Profiles.Add(Expert(profileId));
        db.Users.Add(NewUser(userId, profileId, "I run the Madrid NOC."));
        db.Users.Add(NewUser(Guid.NewGuid(), profileId, "someone else"));
        await db.SaveChangesAsync();

        var ctx = await new UserProfileContextLoader(db, new FakeUser(userId)).LoadAsync(CancellationToken.None);

        Assert.NotNull(ctx);
        Assert.Equal("expert", ctx.ProfileName);
        Assert.Equal("Technical Expert", ctx.DisplayName);
        Assert.Equal(["networking", "automation"], ctx.Skills);
        Assert.Equal("Terse, CLI-first, assume deep knowledge.", ctx.ResponseStyle);
        Assert.Equal("I run the Madrid NOC.", ctx.CustomText);
    }

    [Fact]
    public async Task Loader_returns_null_for_a_user_with_neither_profile_nor_text()
    {
        var userId = Guid.NewGuid();
        await using var db = NewDb();
        db.Users.Add(NewUser(userId));
        await db.SaveChangesAsync();

        Assert.Null(await new UserProfileContextLoader(db, new FakeUser(userId)).LoadAsync(CancellationToken.None));
    }

    // delete_profile detaches users, but a profile deactivated any other way must
    // not keep steering answers; the user's own text still does.
    [Fact]
    public async Task Loader_ignores_an_inactive_profile_but_keeps_the_custom_text()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        await using var db = NewDb();
        var gone = Expert(profileId);
        gone.IsActive = false;
        db.Profiles.Add(gone);
        db.Users.Add(NewUser(userId, profileId, "Prefer answers in Spanish."));
        await db.SaveChangesAsync();

        var ctx = await new UserProfileContextLoader(db, new FakeUser(userId)).LoadAsync(CancellationToken.None);

        Assert.NotNull(ctx);
        Assert.False(ctx.HasProfile);
        Assert.Equal("Prefer answers in Spanish.", ctx.CustomText);
    }

    [Fact]
    public async Task Loader_returns_null_when_unauthenticated()
    {
        await using var db = NewDb();
        Assert.Null(await new UserProfileContextLoader(db, new FakeUser(Guid.NewGuid(), authed: false))
            .LoadAsync(CancellationToken.None));
    }
}
