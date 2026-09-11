using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Messaging;
using nashira_backend.Services.Security;

namespace nashira_backend.BackgroundServices;

// Slack Socket Mode connector. For each enabled Slack channel that has an
// App-Level Token configured, the backend dials OUT to Slack over a WebSocket
// and receives events on it — no public webhook / ingress required (ideal for
// VPN-only deployments). Events are fed into the same ingest pipeline as the
// HTTP path via ReceiveVerifiedAsync (the socket is already authenticated, so
// signature verification is skipped).
//
// A reconcile loop keeps one connection per channel in sync with the DB
// (start/stop/rotate). Each connection self-heals: on disconnect or error it
// reopens with backoff until cancelled. Multiple replicas opening connections is
// safe — Slack load-balances events across them (HA), and the inbound dedupe
// covers any overlap.
public sealed class SlackSocketModeHostedService : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private const string OpenUrl = "https://slack.com/api/apps.connections.open";

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SlackSocketModeHostedService> _logger;

    private readonly Dictionary<Guid, Connection> _connections = new();

    public SlackSocketModeHostedService(
        IServiceScopeFactory scopes,
        IHttpClientFactory httpFactory,
        ILogger<SlackSocketModeHostedService> logger)
    {
        _scopes = scopes;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private sealed record Connection(string Token, CancellationTokenSource Cts, Task Task);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "slack.socket.started reconcile={Seconds}s", (int)ReconcileInterval.TotalSeconds);

        // Same startup delay as the messaging worker: let migrations and the rest
        // of the host settle before the first DB read.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(ReconcileInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad pass must not kill the connector for the life of the process.
                _logger.LogError(ex, "slack.socket.reconcile_failed");
            }

            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }

        foreach (var conn in _connections.Values) conn.Cts.Cancel();
        _connections.Clear();
        _logger.LogInformation("slack.socket.stopped");
    }

    // Bring the set of live connections in line with the DB: start new ones, stop
    // removed/disabled ones, and restart on token rotation.
    private async Task ReconcileAsync(CancellationToken ct)
    {
        Dictionary<Guid, string> desired = new();
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

            var list = await db.MessagingChannels.AsNoTracking()
                .Where(c => c.IsActive
                            && c.Enabled
                            && c.Provider == MessagingChannel.ProviderSlack
                            && c.AppTokenEncrypted != null)
                .ToListAsync(ct);

            foreach (var c in list)
            {
                if (c.AppTokenEncrypted is not { Length: > 0 }) continue;
                var token = protector.Decrypt(c.AppTokenEncrypted);
                if (!string.IsNullOrWhiteSpace(token)) desired[c.MessagingChannelId] = token;
            }
        }

        // Stop connections that vanished, got disabled, or whose token rotated.
        foreach (var (id, conn) in _connections.ToList())
        {
            if (!desired.TryGetValue(id, out var token)
                || !string.Equals(token, conn.Token, StringComparison.Ordinal))
            {
                conn.Cts.Cancel();
                _connections.Remove(id);
                _logger.LogInformation("slack.socket.stop channel={Channel}", id);
            }
        }

        // Start connections that are newly desired.
        foreach (var (id, token) in desired)
        {
            if (_connections.ContainsKey(id)) continue;
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var task = RunConnectionAsync(id, token, cts.Token);
            _connections[id] = new Connection(token, cts, task);
            _logger.LogInformation("slack.socket.start channel={Channel}", id);
        }
    }

    // One channel's connection: open → receive until close/disconnect → reopen
    // with capped exponential backoff, until cancelled.
    private async Task RunConnectionAsync(Guid channelId, string appToken, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wssUrl = await OpenConnectionAsync(appToken, ct);
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(wssUrl), ct);
                attempt = 0;
                _logger.LogInformation("slack.socket.connected channel={Channel}", channelId);
                await ReceiveLoopAsync(channelId, ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "slack.socket.error channel={Channel} attempt={Attempt}", channelId, attempt);
            }

            if (ct.IsCancellationRequested) break;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));
            attempt++;
            try { await Task.Delay(delay, ct); } catch { break; }
        }

        _logger.LogInformation("slack.socket.closed channel={Channel}", channelId);
    }

    private async Task<string> OpenConnectionAsync(string appToken, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(MessagingHttpClients.Name);
        using var req = new HttpRequestMessage(HttpMethod.Post, OpenUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"apps.connections.open failed: {Truncate(body, 200)}");
        return root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString()!
            : throw new InvalidOperationException("apps.connections.open returned no url");
    }

    private async Task ReceiveLoopAsync(Guid channelId, ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            if (await HandleMessageAsync(channelId, ws, text, ct))
                return; // disconnect envelope → reopen
        }
    }

    // Returns true if the caller should reconnect (Slack asked us to disconnect).
    private async Task<bool> HandleMessageAsync(
        Guid channelId, ClientWebSocket ws, string text, CancellationToken ct)
    {
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(text); }
        catch (JsonException) { return false; }
        if (root.ValueKind != JsonValueKind.Object) return false;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "hello":
                return false;
            case "disconnect":
                _logger.LogInformation(
                    "slack.socket.disconnect channel={Channel} reason={Reason}",
                    channelId, root.TryGetProperty("reason", out var r) ? r.GetString() : "?");
                return true;
            case "events_api":
                await DispatchEventAsync(channelId, ws, root, ct);
                return false;
            default:
                return false; // slash_commands / interactive — not handled
        }
    }

    private async Task DispatchEventAsync(
        Guid channelId, ClientWebSocket ws, JsonElement envelope, CancellationToken ct)
    {
        var envelopeId = envelope.TryGetProperty("envelope_id", out var e) ? e.GetString() : null;

        if (envelope.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.GetRawText());
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var ingest = scope.ServiceProvider.GetRequiredService<IMessagingIngestService>();
                // ReceiveVerifiedAsync: the WebSocket itself is the trust boundary
                // here. Slack authenticated us with the App-Level Token when the
                // socket was opened and nothing else can inject into it, so there
                // is no HMAC to check — and no signing secret would even be sent.
                await ingest.ReceiveVerifiedAsync(MessagingChannel.ProviderSlack, channelId, bytes, ct);
            }
            catch (Exception ex)
            {
                // Don't ack on failure — Slack re-delivers, and the inbound dedupe
                // makes the retry a no-op once it succeeds.
                _logger.LogWarning(ex, "slack.socket.dispatch_failed channel={Channel}", channelId);
                return;
            }
        }

        if (envelopeId is not null)
        {
            var ack = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { envelope_id = envelopeId }));
            await ws.SendAsync(new ArraySegment<byte>(ack), WebSocketMessageType.Text, true, ct);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
