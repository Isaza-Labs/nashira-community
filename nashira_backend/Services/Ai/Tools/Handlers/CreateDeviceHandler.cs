using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using DeviceEntity = nashira_backend.Data.Models.Device;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Adds a device to the inventory. Write mutation (operator+, single_confirm) so the
// user confirms before it runs. Mirrors DeviceController.Post; credential linking is
// left to the UI (the agent just registers the device).
public sealed class CreateDeviceHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "ip_address":{"type":"string","description":"Management IP address, e.g. 4.2.2.2"},
          "device_name":{"type":"string","description":"Hostname / label. Defaults to the IP if omitted."},
          "platform":{"type":"string","description":"OS platform, e.g. ios, eos, nxos"},
          "vendor":{"type":"string","description":"Vendor, e.g. cisco, arista, juniper"},
          "os_version":{"type":"string"},
          "site":{"type":"string"},
          "role":{"type":"string","description":"e.g. router, switch, firewall"}
        },"required":["ip_address"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateDeviceHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "create_device";
    public string Description =>
        "Adds a device to the inventory. Requires an ip_address; device_name defaults to the IP if " +
        "omitted. Optional: platform, vendor, os_version, site, role. Returns the new device id.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var ip = Str(args, "ip_address")?.Trim();
        if (string.IsNullOrWhiteSpace(ip)) return Err("ip_address is required");
        if (!IPAddress.TryParse(ip, out _)) return Err($"'{ip}' is not a valid IP address");

        var name = Str(args, "device_name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = ip;

        var dup = await _db.Devices.AsNoTracking().AnyAsync(
            d => d.IsActive && (d.DeviceName == name || d.IpAddress == ip), ct);
        if (dup) return Err($"a device with name '{name}' or ip '{ip}' already exists");

        var now = DateTime.UtcNow;
        var row = new DeviceEntity
        {
            DeviceId = Guid.NewGuid(),
            DeviceName = name,
            IpAddress = ip,
            Platform = Str(args, "platform") ?? string.Empty,
            Vendor = Str(args, "vendor") ?? string.Empty,
            OsVersion = Str(args, "os_version") ?? string.Empty,
            Site = Str(args, "site") ?? string.Empty,
            Role = Str(args, "role") ?? string.Empty,
            Status = string.Empty,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Devices.Add(row);
        await _db.SaveChangesAsync(ct);

        return JsonSerializer.SerializeToElement(new
        {
            device_id = row.DeviceId,
            device_name = row.DeviceName,
            ip_address = row.IpAddress,
            created = true,
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
