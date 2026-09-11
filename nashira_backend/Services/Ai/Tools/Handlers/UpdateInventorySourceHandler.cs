using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Inventory;
using SourceEntity = nashira_backend.Data.Models.InventorySource;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates an inventory source (resolved by id or name). Write → single_confirm.
public sealed class UpdateInventorySourceHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "source_id":{"type":"string","description":"Inventory source id (or identify by name)"},
          "name":{"type":"string","description":"Identify the source by name if source_id is omitted"},
          "new_name":{"type":"string","description":"Rename the source"},
          "base_url":{"type":"string"},
          "token_secret_ref":{"type":"string"},
          "site_filter":{"type":"string"},
          "allow_private_network":{"type":"boolean"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateInventorySourceHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "update_inventory_source";
    public string Description =>
        "Updates an inventory source identified by source_id or name. Only provided fields change; " +
        "use new_name to rename.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var row = await ResolveAsync(args, ct);
        if (row is null) return Err("inventory source not found (pass a valid source_id or name)");

        if (Str(args, "new_name")?.Trim() is { Length: > 0 } nn) row.Name = nn;
        if (Str(args, "base_url")?.Trim() is { Length: > 0 } bu)
        {
            if (!CreateInventorySourceHandler.IsHttpUrl(bu)) return Err("base_url must be an absolute http(s) URL");
            row.BaseUrl = bu;
        }
        if (args.TryGetProperty("token_secret_ref", out var tsr) && tsr.ValueKind == JsonValueKind.String)
        {
            try { row.TokenSecretRef = await InventoryTokenRef.NormalizeAsync(_db, tsr.GetString()!, ct); }
            catch (Exceptions.ValidationException ex) { return Err(ex.Message); }
        }
        if (args.TryGetProperty("site_filter", out var sf) && sf.ValueKind == JsonValueKind.String) row.SiteFilter = sf.GetString();
        if (args.TryGetProperty("allow_private_network", out var ap) && ap.ValueKind is JsonValueKind.True or JsonValueKind.False)
            row.AllowPrivateNetwork = ap.GetBoolean();
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { inventory_source_id = row.InventorySourceId, name = row.Name, updated = true });
    }

    private async Task<SourceEntity?> ResolveAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "source_id")?.Trim() is { Length: > 0 } sid && Guid.TryParse(sid, out var id))
            return await _db.InventorySources.FirstOrDefaultAsync(
                s => s.InventorySourceId == id && s.IsActive, ct);
        if (Str(args, "name")?.Trim() is { Length: > 0 } name)
            return await _db.InventorySources.FirstOrDefaultAsync(
                s => s.Name == name && s.IsActive, ct);
        return null;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
