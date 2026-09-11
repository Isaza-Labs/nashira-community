using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists secret-store entries as metadata only — name, description, whether a value is
// set. Secret VALUES are never returned. Admin-only; read → autonomous.
public sealed class ListSecretsHandler : IToolHandler
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListSecretsHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_secrets";
    public string Description =>
        "Lists secret-store entries (name, description, whether a value is set, and the " +
        "${secret:secret:<name>:value} reference to use). Secret VALUES are never returned. " +
        "Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var secrets = await _db.Secrets.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        var rows = secrets.Select(s => new
        {
            secret_id = s.SecretId,
            name = s.Name,
            description = s.Description,
            has_value = s.EncryptedValue is { Length: > 0 },
            created_by = s.CreatedBy,
            updated_at = s.UpdatedAt,
            reference = $"${{secret:secret:{s.Name}:value}}",
        }).ToList();
        return JsonSerializer.SerializeToElement(new { secrets = rows, count = rows.Count });
    }
}
