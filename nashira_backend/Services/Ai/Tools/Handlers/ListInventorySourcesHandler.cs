using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Lists configured inventory sources so the agent can reference one by id. Read → autonomous.
public sealed class ListInventorySourcesHandler : IToolHandler
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListInventorySourcesHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_inventory_sources";
    public string Description =>
        "Lists configured inventory sources (id, name, kind, base_url, last sync). Use the id with " +
        "sync_netbox_inventory / update_inventory_source / delete_inventory_source.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rows = await _db.InventorySources.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                inventory_source_id = s.InventorySourceId,
                name = s.Name,
                kind = s.Kind,
                base_url = s.BaseUrl,
                site_filter = s.SiteFilter,
                last_synced_at = s.LastSyncedAt,
            })
            .ToListAsync(ct);
        return JsonSerializer.SerializeToElement(new { sources = rows, count = rows.Count });
    }
}
