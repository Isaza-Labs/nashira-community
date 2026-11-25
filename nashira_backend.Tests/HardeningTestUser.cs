using nashira_backend.Services.Identity;

namespace nashira_backend.Tests;

internal sealed class HardeningTestUser(string? role = "admin", bool authenticated = true) : ICurrentUser
{
    public Guid UserId { get; } = Guid.NewGuid();
    public string Username => "hardening-test";
    public IReadOnlyList<string> Roles => role is null ? [] : [role];
    public bool IsAuthenticated => authenticated;
}
