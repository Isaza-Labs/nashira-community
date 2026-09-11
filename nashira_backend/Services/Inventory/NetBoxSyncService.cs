using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Inventory;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Net;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Inventory;

public sealed class NetBoxSyncService : INetBoxSyncService
{
    private const int PageSize = 200;
    private const int MaxPages = 200; // hard stop against a runaway paginator

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretResolver _secrets;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NetBoxSyncService> _logger;

    public NetBoxSyncService(
        AppDbContext db, ICurrentUser user, ISecretResolver secrets,
        IUrlGuard urlGuard, IHttpClientFactory httpFactory, ILogger<NetBoxSyncService> logger)
    {
        _db = db;
        _user = user;
        _secrets = secrets;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<NetBoxSyncResult> SyncAsync(InventorySource source, bool dryRun, CancellationToken ct)
    {
        if (!string.Equals(source.Kind, InventorySource.KindNetBox, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("inventory source is not a netbox source");
        var baseUrl = (source.BaseUrl ?? string.Empty).TrimEnd('/');
        if (baseUrl.Length == 0) throw new ValidationException("base_url is required");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new ValidationException(
                "base_url must be an absolute http(s) URL, e.g. https://netbox.example.com");

        var token = await ResolveTokenAsync(source.TokenSecretRef, ct);

        var url = $"{baseUrl}/api/dcim/devices/?limit={PageSize}";
        if (!string.IsNullOrWhiteSpace(source.SiteFilter))
            url += $"&site={Uri.EscapeDataString(source.SiteFilter!)}";

        var client = _httpFactory.CreateClient("rest_call");

        // Two lookups, in priority order. `byExternal` holds the rows this source
        // already created, keyed by NetBox's own id — matching on it means a device
        // renamed in NetBox updates its row instead of forking a second one. The
        // name map is the fallback that adopts pre-existing rows (manually created,
        // or synced before provenance was tracked) into this source on first pass.
        var all = await _db.Devices.ToListAsync(ct);
        var byExternal = all
            .Where(d => d.SourceId == source.InventorySourceId && d.ExternalId != null)
            .ToDictionary(d => d.ExternalId!, StringComparer.Ordinal);
        var byName = all
            .GroupBy(d => d.DeviceName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var result = new NetBoxSyncResult { DryRun = dryRun };
        var now = DateTime.UtcNow;
        var pages = 0;

        while (!string.IsNullOrEmpty(url) && pages < MaxPages)
        {
            pages++;
            try
            {
                _urlGuard.EnsureSafe(url, allowPrivate: source.AllowPrivateNetwork);
            }
            catch (InvalidOperationException ex)
            {
                // A bad/unsafe source URL is a configuration problem, not a server
                // fault — surface it as a 400 instead of letting it bubble to a 500.
                throw new ValidationException($"inventory source URL rejected: {ex.Message}");
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("Authorization", $"Token {token}");

            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new ValidationException($"netbox request failed ({(int)resp.StatusCode}): {Truncate(body, 200)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in results.EnumerateArray())
                {
                    result.Total++;
                    var mapped = MapDevice(el);
                    if (mapped is null)
                    {
                        // A record with no usable name cannot become a row. Count it —
                        // silently dropping it made "NetBox has more devices than the
                        // inventory" undiagnosable.
                        _logger.LogWarning("netbox.sync.unnamed_device external_id={ExternalId}", IdOf(el));
                        result.Skipped++;
                        continue;
                    }

                    var dev = (mapped.ExternalId is { } ext && byExternal.TryGetValue(ext, out var byExt))
                        ? byExt
                        : byName.GetValueOrDefault(mapped.Name);

                    // The name fallback exists to adopt rows without provenance (manual,
                    // or synced before external ids were tracked). A row this source
                    // already tracks under a *different* NetBox id is a different device
                    // that happens to share the name — NetBox does not enforce unique
                    // names — and merging would silently collapse the two into one row.
                    if (dev is not null && mapped.ExternalId is not null
                        && dev.SourceId == source.InventorySourceId
                        && dev.ExternalId is not null && dev.ExternalId != mapped.ExternalId)
                    {
                        _logger.LogWarning(
                            "netbox.sync.duplicate_name name={Name} kept_external_id={Kept} skipped_external_id={Skipped}",
                            mapped.Name, dev.ExternalId, mapped.ExternalId);
                        result.Skipped++;
                        continue;
                    }

                    if (dev is not null)
                    {
                        if (dryRun) { result.Updated++; continue; }
                        var previousName = dev.DeviceName;
                        var renamed = TryRename(dev, mapped.Name, byName);
                        if (ApplyTo(dev, mapped, source.InventorySourceId, now) || renamed) result.Updated++;
                        else result.Unchanged++;
                        if (renamed) byName.Remove(previousName);
                    }
                    else
                    {
                        if (dryRun)
                        {
                            result.Created++;
                            // Register the would-be row so a same-named record later in
                            // this run hits the duplicate-name guard, exactly as it
                            // would on a real pass — a dry run should predict, not flatter.
                            dev = NewDevice(mapped, source.InventorySourceId, now);
                            byName[dev.DeviceName] = dev;
                            if (dev.ExternalId is { } dryKey) byExternal[dryKey] = dev;
                            continue;
                        }
                        if (byName.ContainsKey(mapped.Name))
                        {
                            // device_name is uniquely indexed here (it is not in
                            // flow-weaver), so inserting would abort the whole sync
                            // transaction. Skip the one row and keep going.
                            _logger.LogWarning(
                                "netbox.sync.name_conflict name={Name} external_id={ExternalId}",
                                mapped.Name, mapped.ExternalId);
                            result.Skipped++;
                            continue;
                        }
                        dev = NewDevice(mapped, source.InventorySourceId, now);
                        _db.Devices.Add(dev);
                        result.Created++;
                    }

                    byName[dev.DeviceName] = dev;
                    if (dev.ExternalId is { } key) byExternal[key] = dev;
                }
            }

            url = root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }

        if (!dryRun)
        {
            source.LastSyncedAt = now;
            source.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "netbox.sync source={Source} dry_run={DryRun} created={Created} updated={Updated} skipped={Skipped} total={Total}",
            source.Name, dryRun, result.Created, result.Updated, result.Skipped, result.Total);
        return result;
    }

    // token_secret_ref arrives in one of three shapes (see InventoryTokenRef): a full
    // ${secret:...} reference, the bare name of a stored secret, or — on rows written
    // before raw tokens were rejected — the literal token itself. A full reference
    // that does not resolve is a configuration error and must fail loudly here:
    // SubstituteAsync leaves the marker in place, and shipping it as the Authorization
    // header turns "secret was deleted" into an inscrutable NetBox 403.
    private async Task<string?> ResolveTokenAsync(string? refOrName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refOrName)) return null;
        var raw = refOrName.Trim();

        if (raw.Contains("${secret:", StringComparison.Ordinal))
        {
            var resolved = await _secrets.SubstituteAsync(raw, ct);
            if (resolved.Contains("${secret:", StringComparison.Ordinal))
                throw new ValidationException(
                    $"token_secret_ref did not resolve ({raw}); check that the referenced secret still exists");
            return resolved;
        }

        // Bare value: prefer a stored secret of that name; fall back to treating it
        // as a legacy literal token so pre-existing sources keep syncing.
        var byName = await _secrets.SubstituteAsync($"${{secret:secret:{raw}:value}}", ct);
        return byName.Contains("${secret:", StringComparison.Ordinal) ? raw : byName;
    }

    // ─── mapping ────────────────────────────────────────────────────

    internal sealed record NetBoxDevice(
        string Name, string Ip, string Platform, string Vendor, string Site, string Role, string Status,
        string? ExternalId, JsonElement Properties);

    internal static NetBoxDevice? MapDevice(JsonElement d)
    {
        var name = Str(d, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new NetBoxDevice(
            Name: name!.Trim(),
            Ip: StripPrefix(NestedStr(d, "primary_ip4", "address") ?? NestedStr(d, "primary_ip", "address")),
            Platform: NestedStr(d, "platform", "slug") ?? NestedStr(d, "platform", "name") ?? string.Empty,
            Vendor: Nested2Str(d, "device_type", "manufacturer", "name") ?? string.Empty,
            Site: NestedStr(d, "site", "name") ?? NestedStr(d, "site", "slug") ?? string.Empty,
            // NetBox 3.6+ renamed device_role -> role; accept both.
            Role: NestedStr(d, "role", "name") ?? NestedStr(d, "device_role", "name") ?? string.Empty,
            Status: StatusOf(d),
            ExternalId: IdOf(d),
            Properties: PropertiesOf(d));
    }

    // NetBox's own primary key, stringified. It is what makes a re-sync idempotent
    // across renames, so a payload without one falls back to name matching.
    private static string? IdOf(JsonElement d)
    {
        if (!d.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetRawText(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    // The NetBox attributes with no column of their own, carried into Device.Properties
    // so they survive the sync and stay queryable. Cloned off the response document,
    // which is disposed as soon as the page is consumed.
    private static JsonElement PropertiesOf(JsonElement d)
    {
        var bag = new Dictionary<string, object?>();
        if (d.TryGetProperty("custom_fields", out var cf) && cf.ValueKind == JsonValueKind.Object)
            bag["custom_fields"] = cf.Clone();
        if (d.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            bag["tags"] = tags.EnumerateArray()
                .Select(t => t.ValueKind == JsonValueKind.Object ? Str(t, "name") ?? Str(t, "slug") : t.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        if (NestedStr(d, "tenant", "name") is { } tenant) bag["tenant"] = tenant;
        if (NestedStr(d, "device_type", "model") is { } model) bag["device_type"] = model;
        if (Str(d, "serial") is { Length: > 0 } serial) bag["serial"] = serial;
        if (Str(d, "url") is { Length: > 0 } url) bag["netbox_url"] = url;
        return JsonSerializer.SerializeToElement(bag);
    }

    private static Device NewDevice(NetBoxDevice m, Guid sourceId, DateTime now) => new()
    {
        DeviceId = Guid.NewGuid(),
        DeviceName = m.Name,
        IpAddress = m.Ip,
        Platform = m.Platform,
        Vendor = m.Vendor,
        OsVersion = string.Empty,
        Site = m.Site,
        Role = m.Role,
        Status = m.Status,
        SourceId = sourceId,
        ExternalId = m.ExternalId,
        LastSyncAt = now,
        Properties = m.Properties,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    // Overwrites device fields with NetBox values, but keeps existing values when
    // NetBox omits one (so a manually-set platform/credential isn't wiped). Always
    // stamps provenance — LastSyncAt included — so "when did this row last agree
    // with the source" stays answerable even on a no-op pass, which is precisely
    // when you want to know it. That stamp is not counted as a change.
    private static bool ApplyTo(Device dev, NetBoxDevice m, Guid sourceId, DateTime now)
    {
        var ip = Pick(dev.IpAddress, m.Ip);
        var platform = Pick(dev.Platform, m.Platform);
        var vendor = Pick(dev.Vendor, m.Vendor);
        var site = Pick(dev.Site, m.Site);
        var role = Pick(dev.Role, m.Role);
        var status = Pick(dev.Status, m.Status);

        var changed = ip != dev.IpAddress || platform != dev.Platform || vendor != dev.Vendor
            || site != dev.Site || role != dev.Role || status != dev.Status || !dev.IsActive;

        // Adopt the row into this source on first sight so later syncs match on the
        // external id instead of the name.
        dev.SourceId = sourceId;
        dev.ExternalId = m.ExternalId ?? dev.ExternalId;
        dev.LastSyncAt = now;
        dev.Properties = m.Properties;

        if (!changed)
        {
            dev.UpdatedAt = now;
            return false;
        }

        dev.IpAddress = ip;
        dev.Platform = platform;
        dev.Vendor = vendor;
        dev.Site = site;
        dev.Role = role;
        dev.Status = status;
        dev.IsActive = true;
        dev.UpdatedAt = now;
        return true;
    }

    private static string Pick(string existing, string incoming) => string.IsNullOrWhiteSpace(incoming) ? existing : incoming;

    // Follows a rename made in NetBox, which is only observable now that rows are
    // matched by external id. Refused when another row already holds the new name:
    // device_name is uniquely indexed, and one collision would otherwise fail the
    // SaveChanges for the entire page.
    private static bool TryRename(Device dev, string incoming, Dictionary<string, Device> byName)
    {
        if (string.IsNullOrWhiteSpace(incoming) || incoming == dev.DeviceName) return false;
        if (byName.TryGetValue(incoming, out var holder) && !ReferenceEquals(holder, dev)) return false;
        dev.DeviceName = incoming;
        return true;
    }

    private static string StatusOf(JsonElement d)
    {
        if (!d.TryGetProperty("status", out var s)) return string.Empty;
        return s.ValueKind switch
        {
            JsonValueKind.String => s.GetString() ?? string.Empty,
            JsonValueKind.Object => Str(s, "value") ?? Str(s, "label") ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static string StripPrefix(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var slash = address.IndexOf('/');
        return slash > 0 ? address[..slash] : address;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? NestedStr(JsonElement e, string p1, string p2) =>
        e.TryGetProperty(p1, out var o) && o.ValueKind == JsonValueKind.Object ? Str(o, p2) : null;

    private static string? Nested2Str(JsonElement e, string p1, string p2, string p3) =>
        e.TryGetProperty(p1, out var o1) && o1.ValueKind == JsonValueKind.Object
            && o1.TryGetProperty(p2, out var o2) && o2.ValueKind == JsonValueKind.Object
            ? Str(o2, p3) : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
