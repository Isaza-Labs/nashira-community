using System.Text.Json;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Inventory;
using SourceEntity = nashira_backend.Data.Models.InventorySource;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Registers an inventory source (e.g. NetBox). token_secret_ref points to a stored
// secret — the raw token is never handled here. Write → single_confirm.
public sealed class CreateInventorySourceHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "name":{"type":"string"},
          "base_url":{"type":"string","description":"Absolute http(s) URL, e.g. https://netbox.example.com"},
          "kind":{"type":"string","description":"Source kind (default netbox)"},
          "token_secret_ref":{"type":"string","description":"Name of a stored secret holding the API token"},
          "site_filter":{"type":"string"},
          "allow_private_network":{"type":"boolean","default":false}
        },"required":["name","base_url"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateInventorySourceHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "create_inventory_source";
    public string Description =>
        "Registers an inventory source (e.g. NetBox) so it can be synced. base_url must be an absolute " +
        "http(s) URL; token_secret_ref names a stored secret (never the raw token). Returns the new id.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("name is required");
        var baseUrl = Str(args, "base_url")?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)) return Err("base_url is required");
        if (!IsHttpUrl(baseUrl)) return Err("base_url must be an absolute http(s) URL, e.g. https://netbox.example.com");

        string? tokenRef = null;
        if (Str(args, "token_secret_ref") is { } tsr)
        {
            try { tokenRef = await InventoryTokenRef.NormalizeAsync(_db, tsr, ct); }
            catch (Exceptions.ValidationException ex) { return Err(ex.Message); }
        }

        var now = DateTime.UtcNow;
        var row = new SourceEntity
        {
            InventorySourceId = Guid.NewGuid(),
            Name = name,
            Kind = Str(args, "kind")?.Trim().ToLowerInvariant() is { Length: > 0 } k ? k : SourceEntity.KindNetBox,
            BaseUrl = baseUrl,
            TokenSecretRef = tokenRef,
            SiteFilter = Str(args, "site_filter"),
            AllowPrivateNetwork = args.TryGetProperty("allow_private_network", out var ap) && ap.ValueKind == JsonValueKind.True,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.InventorySources.Add(row);
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { inventory_source_id = row.InventorySourceId, name = row.Name });
    }

    // Shared with update_inventory_source.
    internal static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url.TrimEnd('/'), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
