using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Credential discovery for the agent. Metadata only — never the secret bytes —
// but it does return credential_id, because every tool that consumes a credential
// (github_*, repository registration, integrations) references it by uuid. Without
// the id here the agent could see a credential and still have no way to name it:
// the only path left was asking the user to paste a uuid out of the UI.
public sealed class ListCredentialsHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "type":{"type":"string","description":"Filter by the free-form type tag (ssh, git_token, netbox, ...)"},
          "auth_method":{"type":"string","enum":["password","key","token","api_key","oauth2"],"description":"Filter by auth method. Use 'token' to find the PAT the github_* tools need."},
          "name_contains":{"type":"string","description":"Case-insensitive substring match on the name. Use it to resolve a name the user typed."},
          "limit":{"type":"integer","minimum":1,"maximum":200,"default":100}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListCredentialsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_credentials";
    public string Description =>
        "Lists the SSH/API credentials available to this tenant (credential_id, name, type, username, " +
        "auth method — never the secret bytes). CALL THIS FIRST whenever the user names a credential " +
        "in words ('the github credential'): the returned credential_id is what github_create_repo, " +
        "github_create_pr and github_merge_pr take. Filter with type / auth_method / name_contains.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var type = Str(args, "type")?.Trim();
        var authMethod = Str(args, "auth_method")?.Trim();
        var nameContains = Str(args, "name_contains")?.Trim();
        var limit = Math.Clamp(Int(args, "limit") ?? 100, 1, 200);

        var query = _db.Credentials.AsNoTracking().Where(c => c.IsActive);
        query = CredentialQuery.FilterByType(query, type);
        query = CredentialQuery.FilterByAuthMethod(query, authMethod);
        query = CredentialQuery.FilterByName(query, nameContains);

        var rows = await query
            .OrderBy(c => c.Name)
            .Take(limit)
            .Select(c => new
            {
                credential_id = c.CredentialId,
                name = c.Name,
                type = c.Type,
                username = c.Username,
                auth_method = c.AuthMethod,
                has_private_key = c.EncryptedPrivateKey != null,
                has_token = c.EncryptedToken != null,
                client_id = c.ClientId,
            })
            .ToListAsync(ct);

        return JsonSerializer.SerializeToElement(new { credentials = rows, count = rows.Count });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
