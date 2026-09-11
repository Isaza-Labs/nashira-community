namespace nashira_backend.Services.Identity;

// Read-only view of the authenticated principal for the current request. Scoped
// to the request so values never cross between concurrent requests. On anonymous
// requests, accessing UserId throws — check IsAuthenticated first.
//
// This replaced ICurrentTenant when multi-tenancy was removed: the company
// dimension is gone, the user identity it also carried is not.
public interface ICurrentUser
{
    Guid UserId { get; }
    string? Username { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsAuthenticated { get; }
}
