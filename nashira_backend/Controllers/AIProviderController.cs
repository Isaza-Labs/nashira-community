using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Ai;
using nashira_backend.Data.DTos.AIProvider;
using nashira_backend.Exceptions;
using nashira_backend.Services.Common;
using nashira_backend.Services.Security;
using nashira_backend.Services.Identity;
using AIProviderEntity = nashira_backend.Data.Models.AIProvider;
using nashira_backend.Services.Audit;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Controllers;

// Admin-only CRUD for a tenant's LLM providers. The API key is encrypted at
// rest and never returned.
[ApiController]
[Route("api/ai/providers")]
[Authorize(Policy = "Admin")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AIProviderController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ISecretProtector _crypto;
    private readonly ICurrentUser _user;
    private readonly IAuditLogger _audit;

    public AIProviderController(
        AppDbContext db, ISecretProtector crypto, ICurrentUser user, IAuditLogger audit)
    {
        _db = db;
        _crypto = crypto;
        _user = user;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<AIProviderResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.AIProviders.AsNoTracking().Where(p => p.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(p => p.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<AIProviderResponse>
        {
            Items = rows.Select(ToResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AIProviderResponse>> GetById(Guid id, CancellationToken ct)
        => ToResponse(await Find(id, ct));

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AIProviderResponse>> Post([FromBody] CreateAIProvider dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        var type = (dto.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (!LlmProviderCatalog.IsSupported(type))
            throw new ValidationException($"type must be one of: {string.Join(", ", LlmProviderCatalog.Types)}");
        if (string.IsNullOrWhiteSpace(dto.DefaultModel)) throw new ValidationException("default_model is required");
        // Every other type falls back to its vendor endpoint; a custom one has no
        // endpoint to fall back to, and the failure would otherwise surface as a
        // broken turn rather than as a rejected form.
        if (LlmProviderCatalog.RequiresBaseUrl(type) && string.IsNullOrWhiteSpace(dto.BaseUrl))
            throw new ValidationException("base_url is required for a custom provider");

        if (await _db.AIProviders.AnyAsync(p => p.Name == dto.Name.Trim() && p.IsActive, ct))
            throw new ConflictException("a provider with this name already exists", "provider_name_taken");

        var now = DateTime.UtcNow;
        var row = new AIProviderEntity
        {
            AIProviderId = Guid.NewGuid(),
            Name = dto.Name.Trim(),
            Type = type,
            BaseURL = dto.BaseUrl,
            DefaultModel = dto.DefaultModel.Trim(),
            EncryptedApiKey = _crypto.Encrypt(dto.ApiKey),
            Enabled = dto.Enabled ?? true,
            Config = NormalizeConfig(dto.Config),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.AIProviders.Add(row);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("ai_provider", row.AIProviderId, "create", after: Snapshot(row), ct: ct);

        return new CreatedAtActionResult(nameof(GetById), "AIProvider", new { id = row.AIProviderId }, ToResponse(row));
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AIProviderResponse>> Update(Guid id, [FromBody] UpdateAIProvider dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        if (dto.Name is not null) row.Name = dto.Name.Trim();
        if (dto.BaseUrl is not null)
        {
            if (LlmProviderCatalog.RequiresBaseUrl(row.Type) && string.IsNullOrWhiteSpace(dto.BaseUrl))
                throw new ValidationException("base_url is required for a custom provider");
            row.BaseURL = dto.BaseUrl;
        }
        if (dto.DefaultModel is not null) row.DefaultModel = dto.DefaultModel.Trim();
        if (dto.ApiKey is not null) row.EncryptedApiKey = _crypto.Encrypt(dto.ApiKey); // "" clears the key
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        if (dto.Config is not null) row.Config = NormalizeConfig(dto.Config);
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // base_url is the interesting field: repointing a provider sends every prompt
        // this deployment produces somewhere else.
        await _audit.LogAsync("ai_provider", row.AIProviderId, "update",
            before: before, after: Snapshot(row), ct: ct);

        return ToResponse(row);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    public async Task<ActionResult<AIProviderResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("ai_provider", row.AIProviderId, "delete", before: before, ct: ct);

        return ToResponse(row);
    }

    // The key never travels — only whether one is set.
    private static object Snapshot(AIProviderEntity p) => new
    {
        name = p.Name,
        type = p.Type,
        base_url = p.BaseURL,
        default_model = p.DefaultModel,
        has_api_key = p.EncryptedApiKey is not null,
        enabled = p.Enabled,
    };

    private async Task<AIProviderEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.AIProviders
            .FirstOrDefaultAsync(p => p.AIProviderId == id && p.IsActive, ct);
        if (row is null) throw new NotFoundException("ai provider not found");
        return row;
    }

    private static AIProviderResponse ToResponse(AIProviderEntity p) => new()
    {
        AIProviderId = p.AIProviderId,
        Name = p.Name,
        Type = p.Type,
        BaseUrl = p.BaseURL,
        DefaultModel = p.DefaultModel,
        HasApiKey = p.EncryptedApiKey is { Length: > 0 },
        Enabled = p.Enabled,
        Config = p.Config,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };

    // Validates the two keys anything reads and passes the rest through untouched.
    //
    // A wrong shape here is not a runtime error, it is silence: AiModelsController
    // ignores a `models` that is not an array of strings, and ModelLimits.FromConfig
    // returns null for a malformed `model_limits`. The admin then sees a saved
    // provider whose extra models never appear in the chat's picker and no
    // explanation anywhere. Rejecting the form is the only place that can say why.
    private static JsonElement NormalizeConfig(JsonElement? config)
    {
        if (config is not { } value || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
            return EmptyConfig;
        if (value.ValueKind != JsonValueKind.Object)
            throw new ValidationException("config must be a JSON object");

        if (value.TryGetProperty("models", out var models))
        {
            if (models.ValueKind != JsonValueKind.Array)
                throw new ValidationException("config.models must be an array of model ids");
            foreach (var m in models.EnumerateArray())
                if (m.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(m.GetString()))
                    throw new ValidationException("config.models must contain only non-empty model ids");
        }

        if (value.TryGetProperty(AnthropicProvider.WorkspaceIdConfigKey, out var workspace)
            && (workspace.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(workspace.GetString())))
            throw new ValidationException(
                $"config.{AnthropicProvider.WorkspaceIdConfigKey} must be a non-empty workspace id");

        if (value.TryGetProperty(ModelLimits.ConfigKey, out var limits))
        {
            if (limits.ValueKind != JsonValueKind.Object)
                throw new ValidationException($"config.{ModelLimits.ConfigKey} must be an object");
            foreach (var field in new[] { "context_window", "max_output_tokens" })
                if (limits.TryGetProperty(field, out var n)
                    && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var v) || v <= 0))
                    throw new ValidationException(
                        $"config.{ModelLimits.ConfigKey}.{field} must be a positive whole number");
        }

        // Cloned: the JsonElement handed in by the model binder is backed by the
        // request's buffer, which is recycled once the response is written.
        return value.Clone();
    }

    private static readonly JsonElement EmptyConfig = JsonDocument.Parse("{}").RootElement;
}
