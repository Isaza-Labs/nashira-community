using System.Security.Claims;

namespace nashira_backend.Services.Identity;

// Reads claims set by JwtBearer authentication. UserId throws when unauthenticated
// so calling a user-scoped service from an anonymous endpoint fails loudly instead
// of silently attributing work to nobody.
public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;

    public CurrentUser(IHttpContextAccessor http) => _http = http;

    public bool IsAuthenticated =>
        _http.HttpContext?.User.Identity?.IsAuthenticated ?? false;

    public Guid UserId
    {
        get
        {
            var claim = _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException(
                    "NameIdentifier claim missing — ICurrentUser accessed on an anonymous request.");
            return Guid.Parse(claim);
        }
    }

    public string? Username => _http.HttpContext?.User.FindFirstValue(ClaimTypes.Name);

    public IReadOnlyList<string> Roles =>
        _http.HttpContext?.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();
}
