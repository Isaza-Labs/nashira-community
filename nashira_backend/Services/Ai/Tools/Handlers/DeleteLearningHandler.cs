using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes a tenant's agent learning, resolved by learning_id. System-wide
// knowledge (IsSystem) is read-only and cannot be deleted.
// Write → single_confirm.
public sealed class DeleteLearningHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "learning_id":{"type":"string","description":"AgentLearning id (uuid) to delete"}
        },"required":["learning_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DeleteLearningHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "delete_learning";
    public string Description =>
        "Removes an agent learning identified by learning_id. System-wide knowledge is read-only and " +
        "cannot be deleted.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "learning_id")?.Trim() is not { Length: > 0 } lid || !Guid.TryParse(lid, out var id))
            return Err("learning_id is required (a valid uuid)");

        var row = await _db.AgentLearnings.FirstOrDefaultAsync(
            l => l.AgentLearningId == id && l.IsActive, ct);
        if (row is null) return Err("learning not found");
        if (row.IsSystem) return Err("system knowledge is read-only");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { agent_learning_id = row.AgentLearningId, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
