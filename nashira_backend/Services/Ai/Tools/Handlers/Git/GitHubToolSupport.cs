using System.Text.Json;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Argument parsing shared by the GitHub handlers.
internal static class GitHubToolSupport
{
    // A credential reference as the agent can plausibly have it: the uuid from
    // list_credentials, or the name the user said out loud. Validation of "at least
    // one of them" lives in IGitHubService.ResolveCredentialAsync so every handler
    // reports it the same way.
    public static (Guid? Id, string? Name) CredentialRef(JsonElement args)
    {
        Guid? id = null;
        if (args.TryGetProperty("credential_id", out var v)
            && v.ValueKind == JsonValueKind.String
            && Guid.TryParse(v.GetString(), out var g))
            id = g;
        return (id, GitToolSupport.Str(args, "credential_name"));
    }

    public static bool Bool(JsonElement a, string k, bool def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : def;

    public static int Int(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : 0;
}
