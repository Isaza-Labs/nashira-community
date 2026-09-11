using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

public sealed class QueryDevicesHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "keyword":{"type":"string","description":"Substring match on name, ip, vendor, platform"},
          "site":{"type":"string"},
          "role":{"type":"string"},
          "missing_ip":{"type":"boolean","description":"Only devices with no IP address — use to preview a bulk_delete_devices selection"},
          "limit":{"type":"integer","minimum":1,"maximum":200,"default":50}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public QueryDevicesHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "query_devices";
    public string Description =>
        "Searches the device inventory by keyword (name/ip/vendor/platform), site, role, or " +
        "missing_ip (devices with no IP address). " +
        "Returns device metadata — never credentials — including which workflow " +
        "environments each device allows (allow_draft / allow_qa / allow_production).";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var keyword = Str(args, "keyword");
        var site = Str(args, "site");
        var role = Str(args, "role");
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 200) : 50;

        var missingIp = args.TryGetProperty("missing_ip", out var mi) && mi.ValueKind == JsonValueKind.True;

        var q = _db.Devices.AsNoTracking().Where(d => d.IsActive);
        if (missingIp) q = q.Where(d => d.IpAddress == null || d.IpAddress == "");
        if (!string.IsNullOrWhiteSpace(site)) q = q.Where(d => d.Site == site);
        if (!string.IsNullOrWhiteSpace(role)) q = q.Where(d => d.Role == role);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword}%";
            q = q.Where(d =>
                EF.Functions.ILike(d.DeviceName, pattern)
                || EF.Functions.ILike(d.IpAddress, pattern)
                || EF.Functions.ILike(d.Vendor, pattern)
                || EF.Functions.ILike(d.Platform, pattern));
        }

        var rows = await q.OrderBy(d => d.DeviceName).Take(limit)
            .Select(d => new
            {
                device_name = d.DeviceName,
                ip_address = d.IpAddress,
                platform = d.Platform,
                vendor = d.Vendor,
                site = d.Site,
                role = d.Role,
                status = d.Status,
                has_credential = d.CredentialId != null,
                // Which promotion stages may target this device. Surfaced so the
                // agent can explain "that workflow can't reach this box" before a
                // run refuses it, instead of after.
                allow_draft = d.AllowDraft,
                allow_qa = d.AllowQa,
                allow_production = d.AllowProduction,
                synced = d.SourceId != null,
                last_sync_at = d.LastSyncAt,
            })
            .ToListAsync(ct);

        return JsonSerializer.SerializeToElement(new { devices = rows, count = rows.Count });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
