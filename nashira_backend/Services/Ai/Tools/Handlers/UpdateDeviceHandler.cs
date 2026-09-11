using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Updates an existing device (resolved by its current name). Write → single_confirm.
public sealed class UpdateDeviceHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "device_name":{"type":"string","description":"Current name of the device to update"},
          "new_name":{"type":"string","description":"New hostname / label"},
          "ip_address":{"type":"string"},
          "platform":{"type":"string"},
          "vendor":{"type":"string"},
          "os_version":{"type":"string"},
          "site":{"type":"string"},
          "role":{"type":"string"},
          "status":{"type":"string"}
        },"required":["device_name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateDeviceHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "update_device";
    public string Description =>
        "Updates fields of an existing device, resolved by its current device_name. Only the fields " +
        "you provide are changed. Returns the updated device.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "device_name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("device_name is required");

        var row = await _db.Devices.FirstOrDefaultAsync(
            d => d.IsActive && d.DeviceName == name, ct);
        if (row is null) return Err($"device '{name}' not found");

        if (Str(args, "new_name")?.Trim() is { Length: > 0 } nn) row.DeviceName = nn;
        if (Str(args, "ip_address")?.Trim() is { Length: > 0 } ip)
        {
            if (!IPAddress.TryParse(ip, out _)) return Err($"'{ip}' is not a valid IP address");
            row.IpAddress = ip;
        }
        if (Str(args, "platform") is { } pf) row.Platform = pf;
        if (Str(args, "vendor") is { } vd) row.Vendor = vd;
        if (Str(args, "os_version") is { } ov) row.OsVersion = ov;
        if (Str(args, "site") is { } st) row.Site = st;
        if (Str(args, "role") is { } rl) row.Role = rl;
        if (Str(args, "status") is { } stt) row.Status = stt;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return JsonSerializer.SerializeToElement(new
        {
            device_id = row.DeviceId,
            device_name = row.DeviceName,
            ip_address = row.IpAddress,
            updated = true,
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
