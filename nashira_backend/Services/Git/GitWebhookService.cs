using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Git;
using nashira_backend.Exceptions;
using nashira_backend.Services.Security;
using HookEntity = nashira_backend.Data.Models.GitWebhook;

namespace nashira_backend.Services.Git;

// Webhook registration for a repository. Everything here is authenticated CRUD; the
// anonymous receiving path lives in GitWebhookReceiver.
public sealed class GitWebhookService : IGitWebhookService
{
    public const string IngestPathPrefix = "/api/git/hooks/";

    // Deliveries are capped on read as well as by the retention sweep: a busy
    // repository can outrun a daily sweep, and nobody debugging a webhook reads past
    // the first screen anyway.
    private const int MaxDeliveryPageSize = 200;

    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ILogger<GitWebhookService> _logger;

    public GitWebhookService(AppDbContext db, ISecretProtector protector, ILogger<GitWebhookService> logger)
    {
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public async Task<ListResponse<GitWebhookResponse>> ListAsync(Guid repositoryId, CancellationToken ct)
    {
        await RequireRepositoryAsync(repositoryId, ct);
        var rows = await _db.GitWebhooks.AsNoTracking()
            .Where(w => w.GitRepositoryId == repositoryId && w.IsActive)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

        var names = await WorkflowNamesAsync(rows, ct);
        return new ListResponse<GitWebhookResponse>
        {
            Items = rows.Select(r => ToResponse(r, names)).ToList(),
            Total = rows.Count,
            Limit = rows.Count,
            Offset = 0,
        };
    }

    public async Task<GitWebhookResponse> GetAsync(Guid repositoryId, Guid webhookId, CancellationToken ct)
    {
        var row = await RequireAsync(repositoryId, webhookId, ct);
        return ToResponse(row, await WorkflowNamesAsync([row], ct));
    }

    public async Task<GitWebhookResponse> CreateAsync(Guid repositoryId, CreateGitWebhook dto, CancellationToken ct)
    {
        await RequireRepositoryAsync(repositoryId, ct);

        var name = (dto.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ValidationException("name is required");

        var provider = (dto.Provider ?? HookEntity.ProviderGithub).Trim().ToLowerInvariant();
        if (!HookEntity.Providers.Contains(provider))
            throw new ValidationException($"provider must be one of: {string.Join(", ", HookEntity.Providers)}");

        if (await _db.GitWebhooks.AnyAsync(
                w => w.GitRepositoryId == repositoryId && w.Name == name && w.IsActive, ct))
            throw new ConflictException("a webhook with that name already exists on this repository",
                "git_webhook_name_taken");

        await RequireWorkflowAsync(dto.OnPushWorkflowId, ct);

        // Random, not derived from the repository or the name: the route is half of
        // what authenticates a delivery, and a predictable one plus allow_unsigned
        // would be an open trigger for anyone who can guess a repository name.
        var secret = NewSecret();
        var now = DateTime.UtcNow;
        var row = new HookEntity
        {
            GitWebhookId = Guid.NewGuid(),
            GitRepositoryId = repositoryId,
            Name = name,
            Provider = provider,
            Route = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            EncryptedSecret = _protector.Encrypt(secret),
            OnPushWorkflowId = dto.OnPushWorkflowId,
            OnPushBranches = NormalizeBranches(dto.OnPushBranches),
            AutoPull = dto.AutoPull ?? true,
            Enabled = dto.Enabled ?? true,
            AllowUnsigned = dto.AllowUnsigned ?? false,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.GitWebhooks.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "git.webhook.created webhook_id={WebhookId} repo_id={RepoId} provider={Provider} workflow_id={WorkflowId}",
            row.GitWebhookId, repositoryId, provider, row.OnPushWorkflowId);

        var response = ToResponse(row, await WorkflowNamesAsync([row], ct));
        response.Secret = secret;
        return response;
    }

    public async Task<GitWebhookResponse> UpdateAsync(
        Guid repositoryId, Guid webhookId, UpdateGitWebhook dto, CancellationToken ct)
    {
        var row = await RequireAsync(repositoryId, webhookId, ct, tracking: true);

        if (dto.Name is { } n && n.Trim().Length > 0)
        {
            var name = n.Trim();
            if (await _db.GitWebhooks.AnyAsync(
                    w => w.GitRepositoryId == repositoryId && w.Name == name
                         && w.GitWebhookId != webhookId && w.IsActive, ct))
                throw new ConflictException("a webhook with that name already exists on this repository",
                    "git_webhook_name_taken");
            row.Name = name;
        }

        // Clearing and rebinding are different intents, and a nullable field cannot
        // tell them apart on its own — omitting the workflow must not silently unbind
        // the one that is already there.
        if (dto.ClearOnPushWorkflow == true)
        {
            row.OnPushWorkflowId = null;
        }
        else if (dto.OnPushWorkflowId is { } workflowId)
        {
            await RequireWorkflowAsync(workflowId, ct);
            row.OnPushWorkflowId = workflowId;
        }

        if (dto.OnPushBranches is not null) row.OnPushBranches = NormalizeBranches(dto.OnPushBranches);
        if (dto.AutoPull is { } pull) row.AutoPull = pull;
        if (dto.Enabled is { } enabled) row.Enabled = enabled;
        if (dto.AllowUnsigned is { } unsigned) row.AllowUnsigned = unsigned;

        // The provider is not editable: it decides which header the sender signs with,
        // so switching it would start rejecting every delivery from a hook that is
        // still configured the old way on the other side.

        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToResponse(row, await WorkflowNamesAsync([row], ct));
    }

    public async Task<GitWebhookResponse> DeleteAsync(Guid repositoryId, Guid webhookId, CancellationToken ct)
    {
        var row = await RequireAsync(repositoryId, webhookId, ct, tracking: true);
        row.IsActive = false;
        row.Enabled = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("git.webhook.deleted webhook_id={WebhookId} repo_id={RepoId}",
            webhookId, repositoryId);

        return ToResponse(row, await WorkflowNamesAsync([row], ct));
    }

    public async Task<GitWebhookResponse> RotateSecretAsync(Guid repositoryId, Guid webhookId, CancellationToken ct)
    {
        var row = await RequireAsync(repositoryId, webhookId, ct, tracking: true);
        var secret = NewSecret();
        row.EncryptedSecret = _protector.Encrypt(secret);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("git.webhook.secret_rotated webhook_id={WebhookId}", webhookId);

        var response = ToResponse(row, await WorkflowNamesAsync([row], ct));
        response.Secret = secret;
        return response;
    }

    public async Task<ListResponse<GitWebhookDeliveryResponse>> ListDeliveriesAsync(
        Guid repositoryId, Guid webhookId, int limit, CancellationToken ct)
    {
        await RequireAsync(repositoryId, webhookId, ct);
        var take = Math.Clamp(limit <= 0 ? 50 : limit, 1, MaxDeliveryPageSize);

        var rows = await _db.GitWebhookDeliveries.AsNoTracking()
            .Where(d => d.GitWebhookId == webhookId)
            .OrderByDescending(d => d.At)
            .Take(take)
            .ToListAsync(ct);

        return new ListResponse<GitWebhookDeliveryResponse>
        {
            Items = rows.Select(d => new GitWebhookDeliveryResponse
            {
                GitWebhookDeliveryId = d.GitWebhookDeliveryId,
                At = d.At,
                Status = d.Status,
                Event = d.Event,
                Branch = d.Branch,
                CommitSha = d.CommitSha,
                DeliveryKey = d.DeliveryKey,
                JobId = d.JobId,
                Error = d.Error,
            }).ToList(),
            Total = rows.Count,
            Limit = take,
            Offset = 0,
        };
    }

    public async Task<GitWebhookTestResponse> DryRunAsync(
        Guid repositoryId, Guid webhookId, string? branch, CancellationToken ct)
    {
        var row = await RequireAsync(repositoryId, webhookId, ct);
        var target = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();

        var refusal = GitWebhookDispatchRules.Refusal(row, target);
        return new GitWebhookTestResponse
        {
            WouldDispatch = refusal is null && row.OnPushWorkflowId is not null,
            Reason = refusal
                ?? (row.OnPushWorkflowId is null
                    ? "the delivery would verify and be recorded, but no workflow is bound so nothing runs"
                    : "a run of the bound workflow would be enqueued"
                      + (row.AutoPull ? ", after pulling the working copy" : "")),
            Branch = target,
            WorkflowId = row.OnPushWorkflowId,
            InputPreview = GitWebhookDispatchRules.RunInput(row, target, commitSha: null),
        };
    }

    // ─── internals ──────────────────────────────────────────────────

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // Trimmed, empties dropped, duplicates collapsed. The filter is compared with
    // ordinal equality against a ref name, so " main" would never match anything while
    // looking on screen exactly like a filter that works.
    private static List<string> NormalizeBranches(IEnumerable<string>? raw) =>
        (raw ?? [])
            .Select(b => b?.Trim() ?? string.Empty)
            .Where(b => b.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private async Task RequireRepositoryAsync(Guid repositoryId, CancellationToken ct)
    {
        if (!await _db.GitRepositories.AnyAsync(r => r.GitRepositoryId == repositoryId && r.IsActive, ct))
            throw new NotFoundException("repository not found");
    }

    private async Task RequireWorkflowAsync(Guid? workflowId, CancellationToken ct)
    {
        if (workflowId is not { } id) return;
        if (!await _db.Workflows.AnyAsync(w => w.WorkflowId == id && w.IsActive, ct))
            throw new ValidationException("on_push_workflow_id does not name a workflow that exists");
    }

    private async Task<HookEntity> RequireAsync(
        Guid repositoryId, Guid webhookId, CancellationToken ct, bool tracking = false)
    {
        var query = tracking ? _db.GitWebhooks : _db.GitWebhooks.AsNoTracking();
        return await query.FirstOrDefaultAsync(
                   w => w.GitWebhookId == webhookId && w.GitRepositoryId == repositoryId && w.IsActive, ct)
               ?? throw new NotFoundException("webhook not found");
    }

    // One query for every row's workflow name, so listing webhooks is two queries
    // rather than one per row.
    private async Task<Dictionary<Guid, string>> WorkflowNamesAsync(
        IReadOnlyCollection<HookEntity> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.OnPushWorkflowId is not null)
            .Select(r => r.OnPushWorkflowId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];

        return await _db.Workflows.AsNoTracking()
            .Where(w => ids.Contains(w.WorkflowId))
            .ToDictionaryAsync(w => w.WorkflowId, w => w.Name, ct);
    }

    private static GitWebhookResponse ToResponse(HookEntity row, Dictionary<Guid, string> workflowNames) => new()
    {
        GitWebhookId = row.GitWebhookId,
        GitRepositoryId = row.GitRepositoryId,
        Name = row.Name,
        Provider = row.Provider,
        Route = row.Route,
        IngestPath = IngestPathPrefix + row.Route,
        SignatureHeader = GitWebhookSignature.HeaderFor(row.Provider),
        HasSecret = row.EncryptedSecret is { Length: > 0 },
        OnPushWorkflowId = row.OnPushWorkflowId,
        OnPushWorkflowName = row.OnPushWorkflowId is { } id && workflowNames.TryGetValue(id, out var name)
            ? name
            : null,
        OnPushBranches = row.OnPushBranches,
        AutoPull = row.AutoPull,
        Enabled = row.Enabled,
        AllowUnsigned = row.AllowUnsigned,
        LastDeliveryAt = row.LastDeliveryAt,
        LastDeliveryStatus = row.LastDeliveryStatus,
        DeliveryCount = row.DeliveryCount,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
    };
}
