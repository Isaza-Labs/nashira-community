using System.Text.Json;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

public sealed class WhoAmIHandler : IToolHandler
{
    private readonly ICurrentUser _user;

    public WhoAmIHandler(ICurrentUser user) => _user = user;

    public string Name => "whoami";
    public string Description => "Returns the current user's identity (username, role, company).";
    public JsonElement ParametersSchema => ToolSchemas.Empty;

    public Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
        => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            username = _user.Username,
            role = _user.Roles.FirstOrDefault() ?? "viewer",
            user_id = _user.UserId,
        }));
}
