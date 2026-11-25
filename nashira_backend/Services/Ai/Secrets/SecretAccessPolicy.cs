using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Secrets;

// The one definition of who may use stored secret material. SecretResolver applies it
// before any lookup; other components that hand out secret-derived material without
// going through the resolver (a cached OAuth access token, for example) apply the same
// rule so a cache cannot become a way around it.
public static class SecretAccessPolicy
{
    public const string DeniedMessage = "Admin role is required to resolve stored secrets";

    public static bool MayResolveStored(ICurrentUser user) =>
        user.IsAuthenticated && user.Roles.Contains("admin", StringComparer.Ordinal);
}
