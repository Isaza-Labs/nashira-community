using System.Text.Json;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Ai.Tools.Handlers.Git;

// Shared argument parsing + result shaping for the git tool handlers.
internal static class GitToolSupport
{
    public static Guid RepoId(JsonElement args) =>
        args.TryGetProperty("repository_id", out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g)
            ? g
            : throw new ValidationException("repository_id is required (uuid)");

    public static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int Int(JsonElement a, string k, int def) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : def;

    public static JsonElement Ok(object value) => JsonSerializer.SerializeToElement(value);

    public static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
