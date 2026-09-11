using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes a device from inventory (resolved by name). Write → single_confirm.
public sealed class DeleteDeviceHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "device_name":{"type":"string","description":"Name of the device to remove"}
        },"required":["device_name"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DeleteDeviceHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "delete_device";
    public string Description => "Removes a device from the inventory, resolved by its device_name.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var name = Str(args, "device_name")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Err("device_name is required");

        var row = await _db.Devices.FirstOrDefaultAsync(
            d => d.IsActive && d.DeviceName == name, ct);
        if (row is null) return Err($"device '{name}' not found");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { device_name = name, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
