using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Security;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Controllers;

// Public ingest for webhook triggers.
//
// Anonymous by design — the caller is a CI job or a monitoring system, not a user
// — so the shared secret IS the authentication. Everything about this endpoint
// follows from that:
//
//   - The body is verified with HMAC-SHA256 before anything else is read.
//   - Comparison is constant-time; a byte-by-byte compare leaks the signature.
//   - An unknown route and a bad signature return the same 401, so the endpoint
//     cannot be used to enumerate which routes exist.
//   - The run is enqueued through the same service a manual run uses, so device
//     environment gating still applies.
[ApiController]
[Route("api/hooks")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingConfiguration.WorkflowWebhookIngest)]
public class WorkflowWebhookController : ControllerBase
{
    public const string SignatureHeader = "X-Nashira-Signature";
    public const string TokenHeader = "X-Nashira-Token";

    // Optional delivery id. When the sender supplies one, a retried delivery gets
    // the SAME job back instead of enqueueing a second run.
    public const string DeliveryHeader = "X-Nashira-Delivery";

    // Bounded so an unauthenticated caller cannot stream an arbitrary body into
    // memory just to have its HMAC computed.
    private const int MaxBodyBytes = 256 * 1024;

    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly Services.Jobs.IJobQueue _queue;
    private readonly ILogger<WorkflowWebhookController> _logger;

    public WorkflowWebhookController(
        AppDbContext db, ISecretProtector protector, Services.Jobs.IJobQueue queue,
        ILogger<WorkflowWebhookController> logger)
    {
        _db = db;
        _protector = protector;
        _queue = queue;
        _logger = logger;
    }

    [HttpPost("{route}")]
    [SkipAudit] // the run emits its own audit events
    public async Task<IActionResult> Receive(string route, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct);
        if (body is null) return StatusCode(413, new { error = "payload too large" });

        var trigger = await _db.WorkflowTriggers.FirstOrDefaultAsync(
            t => t.IsActive && t.Enabled && t.Type == WorkflowTrigger.TypeWebhook && t.Route == route, ct);

        // Same response for "no such route" and "bad signature": distinguishing
        // them would let a caller enumerate valid routes without a secret.
        if (trigger is null || !IsAuthorized(trigger, body))
        {
            _logger.LogWarning("webhook.rejected route={Route}", route);
            return Unauthorized(new { error = "unauthorized" });
        }

        var workflow = await _db.Workflows.FirstOrDefaultAsync(
            w => w.WorkflowId == trigger.WorkflowId && w.IsActive, ct);
        if (workflow is null) return NotFound(new { error = "the workflow this hook points at no longer exists" });

        var payload = ParseObject(body);
        var input = Merge(ParseObject(trigger.InputDefaultsJson), payload);
        var targets = ResolveTargets(trigger, payload);

        // Enqueued, never run inline. The first version executed the workflow
        // inside this request and returned 202 AFTER it finished — so a run of a
        // few minutes outlived the sender's timeout, the sender retried, and the
        // same event ran twice. Now the 202 means what it says, and a retry that
        // carries X-Nashira-Delivery lands on the same job.
        var deliveryKey = Request.Headers.TryGetValue(DeliveryHeader, out var dk)
            ? dk.ToString().Trim() : null;

        var job = await _queue.EnqueueWorkflowRunAsync(
            trigger.WorkflowId, input, targets, trigger.WorkflowTriggerId, deliveryKey, ct, RunTrigger.Webhook);

        _logger.LogInformation(
            "webhook.enqueued trigger={Trigger} job={Job} dedup={Dedup}",
            trigger.Name, job.JobId, deliveryKey is not null);

        return Accepted(new { job_id = job.JobId, status = job.Status });
    }

    // HMAC over the raw body, or a plain shared-secret header for callers that
    // cannot sign. Both are constant-time compared.
    private bool IsAuthorized(WorkflowTrigger trigger, byte[] body)
    {
        var secret = _protector.Decrypt(trigger.EncryptedSecret);

        if (string.IsNullOrEmpty(secret))
            return trigger.AllowUnsigned;

        var keyBytes = Encoding.UTF8.GetBytes(secret);

        if (Request.Headers.TryGetValue(SignatureHeader, out var signature))
        {
            var provided = signature.ToString().Trim();
            // Accept the common "sha256=" prefix so a GitHub-style sender works
            // unmodified.
            if (provided.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase))
                provided = provided["sha256=".Length..];

            var expected = Convert.ToHexString(HMACSHA256.HashData(keyBytes, body)).ToLowerInvariant();
            return FixedTimeEquals(expected, provided.ToLowerInvariant());
        }

        if (Request.Headers.TryGetValue(TokenHeader, out var token))
            return FixedTimeEquals(secret, token.ToString());

        return false;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        // Length alone is not secret, and CryptographicOperations requires equal
        // spans; comparing hashes of unequal-length inputs would throw.
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    // Body-supplied targets can only NARROW the configured list, and only when the
    // trigger opts in. See WorkflowTrigger.AllowTargetOverride for why.
    private static List<Guid> ResolveTargets(WorkflowTrigger trigger, JsonElement? payload)
    {
        var configured = ParseGuids(trigger.TargetDevicesJson);
        if (!trigger.AllowTargetOverride || payload is null) return configured;

        if (!payload.Value.TryGetProperty("target_devices", out var raw) || raw.ValueKind != JsonValueKind.Array)
            return configured;

        var requested = raw.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out _))
            .Select(e => Guid.Parse(e.GetString()!))
            .ToList();
        if (requested.Count == 0) return configured;

        return configured.Count == 0 ? requested : requested.Intersect(configured).ToList();
    }

    // Trigger defaults first, delivery body second: the body may override a default
    // but cannot remove one.
    private static JsonElement? Merge(JsonElement? defaults, JsonElement? payload)
    {
        if (defaults is null) return payload;
        if (payload is null) return defaults;

        var merged = new Dictionary<string, JsonElement>();
        foreach (var p in defaults.Value.EnumerateObject()) merged[p.Name] = p.Value;
        foreach (var p in payload.Value.EnumerateObject()) merged[p.Name] = p.Value;
        return JsonSerializer.SerializeToElement(merged);
    }

    private async Task<byte[]?> ReadBodyAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static JsonElement? ParseObject(byte[] body)
    {
        if (body.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static List<Guid> ParseGuids(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && Guid.TryParse(e.GetString(), out _))
                .Select(e => Guid.Parse(e.GetString()!))
                .ToList();
        }
        catch (JsonException) { return []; }
    }
}
