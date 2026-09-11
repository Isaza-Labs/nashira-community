namespace nashira_backend.Services.Identity;

// ICurrentUser for contexts with no HTTP request (background services, seeds,
// scheduled work). Call Bind() before using user-scoped services in that scope.
public sealed class MutableCurrentUser : ICurrentUser
{
    private Guid? _userId;
    private string? _username;
    private IReadOnlyList<string> _roles = Array.Empty<string>();

    public void Bind(Guid userId, string? username, IEnumerable<string> roles)
    {
        _userId = userId;
        _username = username;
        _roles = roles.ToList();
    }

    public bool IsAuthenticated => _userId.HasValue;

    public Guid UserId => _userId
        ?? throw new InvalidOperationException("MutableCurrentUser not bound — call Bind() first.");

    public string? Username => _username;

    public IReadOnlyList<string> Roles => _roles;
}
