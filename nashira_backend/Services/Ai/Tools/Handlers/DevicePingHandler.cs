using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// TCP reachability check on SSH port 22. Accepts a device name (resolved from
// inventory) or a literal IP. No side effects → autonomous.
public sealed class DevicePingHandler : IToolHandler
{
    private const int SshPort = 22;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "device":{"type":"string","description":"Device name (resolved from inventory) or an IP address"}
        },"required":["device"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DevicePingHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "device_ping";
    public string Description =>
        "Checks TCP reachability of a device on SSH port 22. Accepts a device name (resolved " +
        "from inventory) or an IP address. Returns reachable + latency_ms.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("device", out var dEl) || dEl.ValueKind != JsonValueKind.String)
            return Err("device is required");

        var input = dEl.GetString()!.Trim();
        var ip = input;
        var resolvedFrom = "input";
        if (!IPAddress.TryParse(input, out _))
        {
            var dev = await _db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.IsActive && d.DeviceName == input, ct);
            if (dev is null) return Err($"device '{input}' not found and is not an IP address");
            ip = dev.IpAddress;
            resolvedFrom = "inventory";
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var sw = Stopwatch.StartNew();
        bool reachable;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, SshPort, cts.Token);
            reachable = true;
        }
        catch
        {
            reachable = false;
        }
        sw.Stop();

        return JsonSerializer.SerializeToElement(new
        {
            ip,
            resolved_from = resolvedFrom,
            port = SshPort,
            reachable,
            latency_ms = (int)sw.ElapsedMilliseconds,
        });
    }

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
