using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using SourceEntity = nashira_backend.Data.Models.InventorySource;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes an inventory source (resolved by id or name). Write → single_confirm.
public sealed class DeleteInventorySourceHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "source_id":{"type":"string","description":"Inventory source id (or identify by name)"},
          "name":{"type":"string","description":"Identify the source by name if source_id is omitted"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DeleteInventorySourceHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "delete_inventory_source";
    public string Description => "Removes an inventory source, identified by source_id or name.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        SourceEntity? row = null;
        if (Str(args, "source_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            row = await _db.InventorySources.FirstOrDefaultAsync(
                s => s.InventorySourceId == id && s.IsActive, ct);
        else if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            row = await _db.InventorySources.FirstOrDefaultAsync(
                s => s.Name == name && s.IsActive, ct);

        if (row is null) return Err("inventory source not found (pass a valid source_id or name)");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { inventory_source_id = row.InventorySourceId, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
