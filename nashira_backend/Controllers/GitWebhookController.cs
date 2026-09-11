using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Git;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// Webhook registration for a repository. Reads are Viewer; creating, editing and
// rotating are Admin, matching repository registration itself — a webhook decides
// what a push from outside is allowed to start here, which is administration rather
// than authoring.
[ApiController]
[Route("api/git/repositories/{repoId:guid}/webhooks")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class GitWebhookController : ControllerBase
{
    private readonly IGitWebhookService _webhooks;

    public GitWebhookController(IGitWebhookService webhooks) => _webhooks = webhooks;

    [HttpGet]
    public Task<ListResponse<GitWebhookResponse>> List(Guid repoId, CancellationToken ct)
        => _webhooks.ListAsync(repoId, ct);

    [HttpGet("{id:guid}")]
    public Task<GitWebhookResponse> Get(Guid repoId, Guid id, CancellationToken ct)
        => _webhooks.GetAsync(repoId, id, ct);

    [HttpPost]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<GitWebhookResponse> Create(Guid repoId, [FromBody] CreateGitWebhook dto, CancellationToken ct)
        => _webhooks.CreateAsync(repoId, dto, ct);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<GitWebhookResponse> Update(
        Guid repoId, Guid id, [FromBody] UpdateGitWebhook dto, CancellationToken ct)
        => _webhooks.UpdateAsync(repoId, id, dto, ct);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<GitWebhookResponse> Delete(Guid repoId, Guid id, CancellationToken ct)
        => _webhooks.DeleteAsync(repoId, id, ct);

    [HttpPost("{id:guid}/rotate-secret")]
    [Authorize(Policy = "Admin")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public Task<GitWebhookResponse> RotateSecret(Guid repoId, Guid id, CancellationToken ct)
        => _webhooks.RotateSecretAsync(repoId, id, ct);

    [HttpGet("{id:guid}/deliveries")]
    public Task<ListResponse<GitWebhookDeliveryResponse>> Deliveries(
        Guid repoId, Guid id, [FromQuery] int limit = 50, CancellationToken ct = default)
        => _webhooks.ListDeliveriesAsync(repoId, id, limit, ct);

    // Answers what a push to `branch` would do, without a push. Read-only by
    // construction — it does not pull and it does not enqueue.
    [HttpGet("{id:guid}/dry-run")]
    public Task<GitWebhookTestResponse> DryRun(
        Guid repoId, Guid id, [FromQuery] string? branch, CancellationToken ct)
        => _webhooks.DryRunAsync(repoId, id, branch, ct);
}

// The public ingestion endpoint. This is the URL an operator pastes into GitHub, so
// it is anonymous by necessity: GitHub cannot hold a session. The signature is the
// authentication, and GitWebhookReceiver checks it before reading anything else.
[ApiController]
[Route("api/git/hooks")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingConfiguration.GitWebhookIngest)]
public class GitWebhookIngestController : ControllerBase
{
    // Bounded so an unauthenticated caller cannot stream an arbitrary body into
    // memory just to have its HMAC computed. GitHub's largest practical push payload
    // is far below this even for a hundred-commit batch.
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly GitWebhookReceiver _receiver;
    private readonly ILogger<GitWebhookIngestController> _logger;

    public GitWebhookIngestController(GitWebhookReceiver receiver, ILogger<GitWebhookIngestController> logger)
    {
        _receiver = receiver;
        _logger = logger;
    }

    [HttpPost("{route}")]
    [SkipAudit] // the run emits its own audit events; the delivery row is the hook's trail
    public async Task<IActionResult> Receive(string route, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct);
        if (body is null)
            return StatusCode(413, new GitWebhookIngestResponse { Ok = false, Message = "payload too large" });

        // Which header carries what depends on the provider, and the route does not
        // say which provider it belongs to until the row is loaded — so read all of
        // them and let the receiver pick by the row's provider.
        var evt = Header(GitHubEventHeader) ?? Header(GitLabEventHeader) ?? Header(GenericEventHeader);
        var signature = Header(GitWebhookSignature.GithubHeader)
                        ?? Header(GitWebhookSignature.GitlabHeader)
                        ?? Header(GitWebhookSignature.GenericHeader);
        var delivery = Header(GitHubDeliveryHeader) ?? Header(GenericDeliveryHeader);

        var outcome = await _receiver.ReceiveAsync(route, evt, signature, delivery, body, ct);

        _logger.LogInformation(
            "git.webhook.ingest status={Status} deduplicated={Dedup} message={Message}",
            outcome.StatusCode, outcome.Deduplicated, outcome.Message);

        return StatusCode(outcome.StatusCode, new GitWebhookIngestResponse
        {
            Ok = outcome.StatusCode is >= 200 and < 300,
            Message = outcome.Message,
            JobId = outcome.JobId,
            Deduplicated = outcome.Deduplicated,
        });
    }

    private const string GitHubEventHeader = "X-GitHub-Event";
    private const string GitLabEventHeader = "X-Gitlab-Event";
    private const string GenericEventHeader = "X-Nashira-Event";
    private const string GitHubDeliveryHeader = "X-GitHub-Delivery";
    private const string GenericDeliveryHeader = "X-Nashira-Delivery";

    private string? Header(string name) =>
        Request.Headers.TryGetValue(name, out var v) && v.ToString() is { Length: > 0 } s ? s : null;

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
}
