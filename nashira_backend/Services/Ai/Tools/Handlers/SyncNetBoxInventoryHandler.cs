using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Inventory;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Syncs devices from a NetBox inventory source into the local Device table.
// Bulk inventory mutation → write / single_confirm.
public sealed class SyncNetBoxInventoryHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "source_id":{"type":"string","description":"Inventory source id (uses the only NetBox source if omitted)"},
          "dry_run":{"type":"boolean","default":false,"description":"Report counts without writing changes"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly INetBoxSyncService _sync;

    public SyncNetBoxInventoryHandler(AppDbContext db, ICurrentUser user, INetBoxSyncService sync)
    {
        _db = db;
        _user = user;
        _sync = sync;
    }

    public string Name => "sync_netbox_inventory";
    public string Description =>
        "Pulls devices from a configured NetBox source and upserts them into the device " +
        "inventory (matched by name). Use dry_run to preview counts first.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        try
        {
            var dryRun = args.TryGetProperty("dry_run", out var dr) && dr.ValueKind is JsonValueKind.True or JsonValueKind.False && dr.GetBoolean();

            InventorySource? source;
            if (args.TryGetProperty("source_id", out var sid) && sid.ValueKind == JsonValueKind.String)
            {
                if (!Guid.TryParse(sid.GetString(), out var id))
                    return Err("source_id must be a uuid");
                source = await _db.InventorySources.FirstOrDefaultAsync(
                    s => s.InventorySourceId == id && s.IsActive, ct);
                if (source is null) return Err("inventory source not found");
            }
            else
            {
                var netboxSources = await _db.InventorySources
                    .Where(s => s.IsActive && s.Kind == InventorySource.KindNetBox)
                    .Take(2).ToListAsync(ct);
                if (netboxSources.Count == 0) return Err("no NetBox inventory source is configured");
                if (netboxSources.Count > 1) return Err("multiple NetBox sources exist; pass source_id");
                source = netboxSources[0];
            }

            var result = await _sync.SyncAsync(source, dryRun, ct);
            return JsonSerializer.SerializeToElement(result);
        }
        catch (DomainException ex)
        {
            return Err(ex.Message);
        }
    }

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
