using nashira_backend.Services.Messaging;
using Xunit;

namespace nashira_backend.Tests;

// The channel ceiling is the whole no-escalation guarantee: a turn arriving from
// Slack must never be able to do more than the same person can do in the browser.
public class MessagingRolesTests
{
    [Theory]
    // No ceiling — the user's own role stands.
    [InlineData("admin", null, "admin")]
    [InlineData("operator", null, "operator")]
    [InlineData("viewer", null, "viewer")]
    [InlineData("admin", "", "admin")]
    // The ceiling narrows.
    [InlineData("admin", "operator", "operator")]
    [InlineData("admin", "viewer", "viewer")]
    [InlineData("operator", "viewer", "viewer")]
    // The ceiling never widens — this is the property that matters.
    [InlineData("viewer", "admin", "viewer")]
    [InlineData("operator", "admin", "operator")]
    [InlineData("viewer", "operator", "viewer")]
    // Equal ranks are a no-op.
    [InlineData("operator", "operator", "operator")]
    public void Effective_takes_the_more_restrictive_of_user_and_ceiling(
        string userRole, string? maxRole, string expected)
        => Assert.Equal(expected, MessagingRoles.Effective(userRole, maxRole));

    [Fact]
    public void Role_comparison_is_case_insensitive()
        => Assert.Equal("operator", MessagingRoles.Effective("ADMIN", "Operator"));

    // A typo in either column must fail closed. An unknown user role ranks lowest,
    // so it cannot be promoted by a permissive ceiling...
    [Fact]
    public void An_unknown_user_role_cannot_be_raised_by_a_ceiling()
        => Assert.Equal("wizard", MessagingRoles.Effective("wizard", "admin"));

    // ...and an unknown ceiling is treated as no ceiling rather than as admin, so a
    // misspelled MaxRole leaves the user's real role intact instead of granting one.
    [Fact]
    public void An_unknown_ceiling_leaves_the_users_real_role_intact()
        => Assert.Equal("viewer", MessagingRoles.Effective("viewer", "supervisor"));
}
