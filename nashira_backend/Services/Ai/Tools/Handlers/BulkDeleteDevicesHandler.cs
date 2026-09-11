using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes MANY devices in one confirmed call. Write → single_confirm, and the
// whole batch spends ONE mutation-budget slot — the reason this exists at all: a
// cleanup of 200 IP-less devices through delete_device burned the turn's budget
// twenty rows at a time.
//
// Selection is a name list, a filter set, or both — when both are given the filter
// further RESTRICTS the list (intersection), never widens it. An empty selection is
// refused outright: "no criteria" must never mean "everything".
//
// Beyond the dispatcher's per-call audit row (which records only the arguments),
// this handler writes its own audit event carrying the RESOLVED device names — for
// a filter delete the arguments alone cannot tell an auditor what actually died,
// and AuditLogger stamps who asked onto the row.
public sealed class BulkDeleteDevicesHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "device_names":{"type":"array","items":{"type":"string"},"description":"Explicit devices to delete, by exact device_name"},
          "missing_ip":{"type":"boolean","description":"Only devices with no IP address"},
          "site":{"type":"string","description":"Only devices at this site (exact match)"},
          "role":{"type":"string","description":"Only devices with this role (exact match)"},
          "vendor":{"type":"string","description":"Only devices from this vendor (exact match)"},
          "platform":{"type":"string","description":"Only devices on this platform (exact match)"},
          "expected_count":{"type":"integer","minimum":0,"description":"Refuse unless exactly this many devices match — pass the count the user confirmed"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;
    private readonly ILogger<BulkDeleteDevicesHandler> _logger;

    public BulkDeleteDevicesHandler(
        AppDbContext db, ICurrentUser user, IAuditLogger audit,
        ILogger<BulkDeleteDevicesHandler> logger)
    {
        _db = db;
        _user = user;
        _audit = audit;
        _logger = logger;
    }

    public string Name => "bulk_delete_devices";
    public string Description =>
        "Removes MANY devices from the inventory in one call — one mutation, one confirmation — " +
        "instead of one delete_device per row. Select by an explicit device_names list, by filters " +
        "(missing_ip, site, role, vendor, platform), or both: filters restrict the list. At least one " +
        "criterion is required. Before calling, preview the matches with query_devices, show the user " +
        "the names and the count, and pass that count as expected_count so a changed inventory refuses " +
        "rather than deleting something the user never saw.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var names = StrList(args, "device_names");
        var missingIp = args.TryGetProperty("missing_ip", out var mi) && mi.ValueKind == JsonValueKind.True;
        var site = Str(args, "site")?.Trim();
        var role = Str(args, "role")?.Trim();
        var vendor = Str(args, "vendor")?.Trim();
        var platform = Str(args, "platform")?.Trim();

        var hasFilter = missingIp
            || !string.IsNullOrEmpty(site) || !string.IsNullOrEmpty(role)
            || !string.IsNullOrEmpty(vendor) || !string.IsNullOrEmpty(platform);
        if (names.Count == 0 && !hasFilter)
            return Err("pass device_names and/or at least one filter — an empty selection would mean the whole inventory");

        List<Data.Models.Device> candidates;
        var notFound = new List<string>();
        var excludedByFilter = new List<string>();

        if (names.Count > 0)
        {
            var rows = await _db.Devices
                .Where(d => d.IsActive && names.Contains(d.DeviceName))
                .ToListAsync(ct);
            var found = rows.Select(r => r.DeviceName).ToHashSet(StringComparer.Ordinal);
            notFound = names.Where(n => !found.Contains(n)).ToList();

            // The filters run in memory over the name-selected rows: same predicates the
            // query path uses, and it keeps "named but filtered out" reportable.
            candidates = rows.Where(d => Matches(d, missingIp, site, role, vendor, platform)).ToList();
            excludedByFilter = rows.Except(candidates).Select(d => d.DeviceName).OrderBy(n => n).ToList();
        }
        else
        {
            var q = _db.Devices.Where(d => d.IsActive);
            if (missingIp) q = q.Where(d => d.IpAddress == null || d.IpAddress == "");
            if (!string.IsNullOrEmpty(site)) q = q.Where(d => d.Site == site);
            if (!string.IsNullOrEmpty(role)) q = q.Where(d => d.Role == role);
            if (!string.IsNullOrEmpty(vendor)) q = q.Where(d => d.Vendor == vendor);
            if (!string.IsNullOrEmpty(platform)) q = q.Where(d => d.Platform == platform);
            candidates = await q.ToListAsync(ct);
        }

        if (args.TryGetProperty("expected_count", out var ec) && ec.TryGetInt32(out var expected)
            && expected != candidates.Count)
            return Err($"expected_count is {expected} but {candidates.Count} devices match now — " +
                       "re-run query_devices, show the user the new list, and confirm again");

        var deleted = candidates.Select(d => d.DeviceName).OrderBy(n => n).ToList();
        if (candidates.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var d in candidates)
            {
                d.IsActive = false;
                d.UpdatedAt = now;
            }
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync("agent.bulk", entityId: null, action: Name,
                before: new { device_names = names, missing_ip = missingIp, site, role, vendor, platform },
                after: new { deleted_count = deleted.Count, deleted }, ct);
        }

        _logger.LogInformation(
            "ai.tool.bulk_delete_devices user={UserId} actor={Actor} deleted={Deleted} not_found={NotFound} excluded={Excluded}",
            _user.IsAuthenticated ? _user.UserId : null, _user.Username,
            deleted.Count, notFound.Count, excludedByFilter.Count);

        return JsonSerializer.SerializeToElement(new
        {
            deleted_count = deleted.Count,
            deleted,
            not_found = notFound.Count > 0 ? notFound : null,
            excluded_by_filter = excludedByFilter.Count > 0 ? excludedByFilter : null,
            note = deleted.Count == 0 ? "no devices matched the selection — nothing was deleted" : null,
        });
    }

    private static bool Matches(
        Data.Models.Device d, bool missingIp, string? site, string? role, string? vendor, string? platform) =>
        (!missingIp || string.IsNullOrWhiteSpace(d.IpAddress))
        && (string.IsNullOrEmpty(site) || d.Site == site)
        && (string.IsNullOrEmpty(role) || d.Role == role)
        && (string.IsNullOrEmpty(vendor) || d.Vendor == vendor)
        && (string.IsNullOrEmpty(platform) || d.Platform == platform);

    private static List<string> StrList(JsonElement a, string k)
    {
        if (!a.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
