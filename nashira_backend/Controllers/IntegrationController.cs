using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Integration;
using nashira_backend.Data.DTos.Skill;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Integration;
using IntegrationEntity = nashira_backend.Data.Models.Integration;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// External systems Nashira calls: base URL, credentials, and the action catalog
// synced from the OpenAPI specs linked to them. Reads are Viewer; writes are Admin,
// because an integration holds credentials and an SSRF opt-out.
[ApiController]
[Route("api/integrations")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class IntegrationController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IIntegrationHealthChecker _health;
    private readonly IIntegrationActionSync _sync;
    private readonly IIntegrationCatalog _catalog;
    private readonly IAuditLogger _audit;

    public IntegrationController(
        AppDbContext db, ICurrentUser user, IIntegrationHealthChecker health,
        IIntegrationActionSync sync, IIntegrationCatalog catalog, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _health = health;
        _sync = sync;
        _catalog = catalog;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<IntegrationResponse>>> Get(
        string? type = null, int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = IntegrationQuery.FilterByType(_db.Integrations.AsNoTracking().Where(i => i.IsActive), type);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(i => i.Name).Skip(offset).Take(limit).ToListAsync(ct);
        var ids = rows.Select(r => r.IntegrationId).ToList();

        // Two grouped counts rather than N+1 per row.
        var specCounts = await _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.IsActive && s.IntegrationId != null && ids.Contains(s.IntegrationId!.Value))
            .GroupBy(s => s.IntegrationId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var actionCounts = await _db.IntegrationActions.AsNoTracking()
            .Where(a => a.IsActive && ids.Contains(a.IntegrationId))
            .GroupBy(a => a.IntegrationId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var credentials = await CredentialRefsAsync(
            rows.Where(r => r.AuthCredentialId is not null).Select(r => r.AuthCredentialId!.Value), ct);

        return new OkObjectResult(new ListResponse<IntegrationResponse>
        {
            Items = rows.Select(r => ToResponse(
                r, specCounts.GetValueOrDefault(r.IntegrationId), actionCounts.GetValueOrDefault(r.IntegrationId),
                r.AuthCredentialId is { } cid ? credentials.GetValueOrDefault(cid) : null)).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<IntegrationResponse>> GetById(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var specs = await _db.AiApiSpecs.CountAsync(s => s.IsActive && s.IntegrationId == id, ct);
        var actions = await _db.IntegrationActions.CountAsync(a => a.IsActive && a.IntegrationId == id, ct);
        var credentials = await CredentialRefsAsync(
            row.AuthCredentialId is { } cid ? [cid] : [], ct);
        return ToResponse(row, specs, actions,
            row.AuthCredentialId is { } id2 ? credentials.GetValueOrDefault(id2) : null);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<IntegrationResponse>> Post(
        [FromBody] CreateIntegration dto, CancellationToken ct)
    {
        var (row, credential) = await BuildIntegrationAsync(dto, ct);
        _db.Integrations.Add(row);
        await SaveTranslatingNameCollisionAsync(row.Name, ct);

        await _audit.LogAsync("integration", row.IntegrationId, "create", after: Snapshot(row), ct: ct);

        return new CreatedAtActionResult(
            nameof(GetById), "Integration", new { id = row.IntegrationId }, ToResponse(row, 0, 0, credential));
    }

    // Create an integration together with the skills and specs that belong to it.
    //
    // An integration on its own is a base URL: it does nothing until a spec gives it
    // operations. Creating those in three separate calls means any failure leaves a
    // half-configured system behind that somebody has to notice and clean up — so the
    // whole bundle lands in one transaction, or none of it does.
    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost("bundle")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<CreateIntegrationBundleResult>> PostBundle(
        [FromBody] CreateIntegrationBundle dto, CancellationToken ct)
    {
        var (row, credential) = await BuildIntegrationAsync(dto.Integration, ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        _db.Integrations.Add(row);
        // Saved first because the skills and specs reference its id.
        await SaveTranslatingNameCollisionAsync(row.Name, ct);

        var attached = await _catalog.AttachBundleAsync(row.IntegrationId, dto.Skills, dto.Specs, ct);

        await tx.CommitAsync(ct);

        // AFTER the commit, never inside it. AuditLogger reserves Sequence = MAX + 1
        // under a process-wide gate and releases the gate once it has saved — but a save
        // inside an open transaction is not visible to anyone else, so the next writer
        // reads the same MAX, picks the same sequence, and blocks on the unique index
        // until this transaction commits, then fails with a duplicate key. Writing here
        // costs the guarantee that a rolled-back bundle leaves no audit row; a chain
        // that deadlocks under concurrency costs more.
        await _audit.LogAsync("integration", row.IntegrationId, "create",
            after: new
            {
                integration = Snapshot(row),
                skills_attached = attached.SkillsAttached,
                specs_attached = attached.SpecsAttached,
                actions_created = attached.ActionsCreated,
            },
            ct: ct);

        // After the commit, never before: reloading the spec index from inside an
        // uncommitted transaction would populate it from the old state and nothing
        // would tell it to try again.
        await _catalog.RefreshCachesAsync(ct);

        var specCount = await _db.AiApiSpecs
            .CountAsync(s => s.IsActive && s.IntegrationId == row.IntegrationId, ct);
        var actionCount = await _db.IntegrationActions
            .CountAsync(a => a.IsActive && a.IntegrationId == row.IntegrationId, ct);

        return new CreatedAtActionResult(
            nameof(GetById), "Integration", new { id = row.IntegrationId },
            new CreateIntegrationBundleResult
            {
                Integration = ToResponse(row, specCount, actionCount, credential),
                SkillsCreated = attached.SkillsAttached,
                SpecsCreated = attached.SpecsAttached,
                ActionsCreated = attached.ActionsCreated,
            });
    }

    // Validated construction of the integration row, shared by the plain create and
    // the bundle. Neither adds it to the context — the bundle has to control when
    // that happens relative to its transaction.
    private async Task<(IntegrationEntity Row, CredentialRef? Credential)> BuildIntegrationAsync(
        CreateIntegration dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        if (string.IsNullOrWhiteSpace(dto.BaseUrl)) throw new ValidationException("base_url is required");
        RequireAbsoluteHttpUrl(dto.BaseUrl);
        RejectDerivedAuthMethod(dto.AuthMethod);
        // Parse eagerly so a malformed config is a 400 here rather than a confusing
        // upstream 401 on the first real call.
        IntegrationAuthConfig.TryParse(dto.AuthConfig);
        RejectAuthSchemeTheTypeCannotUse(dto.Type, dto.AuthConfig);
        RequireJsonObject(dto.Headers, "headers");

        CredentialRef? credential = null;
        if (dto.AuthCredentialId is { } credentialId)
            credential = await RequireCredentialAsync(credentialId, ct);

        var name = dto.Name.Trim();
        if (await _db.Integrations.AnyAsync(i => i.Name == name && i.IsActive, ct))
            throw new ConflictException("an integration with this name already exists", "integration_name_taken");

        var takenSlugs = await _db.Integrations.Select(i => i.Slug).ToListAsync(ct);
        var taken = new HashSet<string>(takenSlugs, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var row = new IntegrationEntity
        {
            IntegrationId = Guid.NewGuid(),
            Name = name,
            Slug = Slug.Unique(name, taken.Contains),
            Type = (dto.Type ?? string.Empty).Trim().ToLowerInvariant(),
            Description = dto.Description,
            BaseUrl = dto.BaseUrl.Trim().TrimEnd('/'),
            AuthConfig = dto.AuthConfig,
            AuthCredentialId = dto.AuthCredentialId,
            HeadersJson = dto.Headers,
            VerifySsl = dto.VerifySsl ?? true,
            AllowPrivateNetwork = dto.AllowPrivateNetwork ?? false,
            HealthCheckPath = dto.HealthCheckPath,
            Enabled = dto.Enabled ?? true,
            Status = IntegrationEntity.StatusUnknown,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return (row, credential);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<IntegrationResponse>> Update(
        Guid id, [FromBody] UpdateIntegration dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        RejectDerivedAuthMethod(dto.AuthMethod);

        if (dto.Name is not null && dto.Name.Trim().Length > 0)
        {
            var name = dto.Name.Trim();
            if (await _db.Integrations.AnyAsync(i => i.Name == name && i.IsActive && i.IntegrationId != id, ct))
                throw new ConflictException("an integration with this name already exists", "integration_name_taken");
            // Slug is deliberately not recomputed — see Integration.Slug.
            row.Name = name;
        }
        if (dto.Type is not null) row.Type = dto.Type.Trim().ToLowerInvariant();
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.BaseUrl is not null)
        {
            RequireAbsoluteHttpUrl(dto.BaseUrl);
            row.BaseUrl = dto.BaseUrl.Trim().TrimEnd('/');
        }
        if (dto.AuthConfig is not null)
        {
            IntegrationAuthConfig.TryParse(dto.AuthConfig);
            RejectAuthSchemeTheTypeCannotUse(row.Type, dto.AuthConfig);
            row.AuthConfig = dto.AuthConfig;
        }
        // Tri-state: Undefined (absent) leaves the link alone, null detaches, a guid relinks.
        switch (dto.AuthCredentialId.ValueKind)
        {
            case JsonValueKind.Undefined:
                break;
            case JsonValueKind.Null:
                row.AuthCredentialId = null;
                break;
            default:
                if (!dto.AuthCredentialId.TryGetGuid(out var credentialId))
                    throw new ValidationException("auth_credential_id must be a GUID or null");
                await RequireCredentialAsync(credentialId, ct);
                row.AuthCredentialId = credentialId;
                break;
        }
        if (dto.Headers is not null)
        {
            RequireJsonObject(dto.Headers, "headers");
            row.HeadersJson = dto.Headers;
        }
        if (dto.VerifySsl is not null) row.VerifySsl = dto.VerifySsl.Value;
        if (dto.AllowPrivateNetwork is not null) row.AllowPrivateNetwork = dto.AllowPrivateNetwork.Value;
        if (dto.HealthCheckPath is not null) row.HealthCheckPath = dto.HealthCheckPath;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // allow_private_network and verify_ssl are the two that matter most here: one
        // opts out of the SSRF guard, the other out of certificate checking, and both
        // are the kind of change nobody remembers making.
        await _audit.LogAsync("integration", row.IntegrationId, "update",
            before: before, after: Snapshot(row), ct: ct);

        return await GetById(id, ct);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<IntegrationResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        var now = DateTime.UtcNow;
        row.IsActive = false;
        row.UpdatedAt = now;

        // Everything the integration owns goes with it. Flipping only this row left its
        // specs and actions active and still pointing at something nobody could open:
        // the spec kept the api name reserved forever, and the only advice the catalog
        // could offer was to unlink it from an integration that no longer appears in
        // any list. The operations were dead either way — the executor resolves the
        // owning integration with an `IsActive` filter, so every call fell through to
        // "no base_url configured" — so this removes a tombstone, not a capability.
        var specs = await _db.AiApiSpecs
            .Where(x => x.IntegrationId == id && x.IsActive)
            .ToListAsync(ct);
        foreach (var spec in specs) { spec.IsActive = false; spec.UpdatedAt = now; }

        var actions = await _db.IntegrationActions
            .Where(x => x.IntegrationId == id && x.IsActive)
            .ToListAsync(ct);
        foreach (var action in actions) { action.IsActive = false; action.UpdatedAt = now; }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("integration", row.IntegrationId, "delete",
            before: before,
            after: new { specs_released = specs.Count, actions_released = actions.Count },
            ct: ct);

        // The agent's catalog is built from the spec index; without this the operations
        // of a deleted integration stay offered until the next restart.
        await _catalog.RefreshCachesAsync(ct);

        return ToResponse(row, 0, 0);
    }

    // Configuration, never credentials. auth_config can carry material inline, so it
    // is reported as a flag rather than copied into an append-only table.
    private static object Snapshot(IntegrationEntity i) => new
    {
        name = i.Name,
        slug = i.Slug,
        type = i.Type,
        base_url = i.BaseUrl,
        auth_credential_id = i.AuthCredentialId,
        has_auth_config = !string.IsNullOrWhiteSpace(i.AuthConfig),
        verify_ssl = i.VerifySsl,
        allow_private_network = i.AllowPrivateNetwork,
        health_check_path = i.HealthCheckPath,
        enabled = i.Enabled,
    };

    // Authenticated probe against the live system. Operator rather than Admin: this
    // is the diagnostic an on-call operator needs, and it changes nothing but the
    // recorded status.
    [HttpPost("{id:guid}/check")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    [SkipAudit] // diagnostic, not a configuration mutation
    public async Task<ActionResult<IntegrationHealthResponse>> Check(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var result = await _health.CheckAsync(row, ct);

        row.Status = result.Status;
        row.LastCheckError = result.Error is { Length: > 500 } ? result.Error[..500] : result.Error;
        row.LastCheckedAt = DateTime.UtcNow;
        row.UpdatedAt = row.LastCheckedAt.Value;
        await _db.SaveChangesAsync(ct);

        return new IntegrationHealthResponse
        {
            Status = result.Status,
            StatusCode = result.StatusCode,
            ElapsedMs = result.ElapsedMs,
            Error = result.Error,
        };
    }

    // Rebuilds the action catalog from the specs linked to this integration.
    [HttpPost("{id:guid}/sync-actions")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<ActionSyncResponse>> SyncActions(Guid id, CancellationToken ct)
    {
        await Find(id, ct);
        var r = await _sync.SyncAsync(id, ct);
        return new ActionSyncResponse
        {
            Created = r.Created,
            Updated = r.Updated,
            Unchanged = r.Unchanged,
            Disappeared = r.Disappeared,
            SpecCount = r.SpecCount,
        };
    }

    // The skills and specs scoped to this integration, so the integrations screen can
    // show what it owns instead of sending an admin to two other screens to find out.
    [HttpGet("{id:guid}/bundle")]
    public Task<IntegrationBundleView> GetBundle(Guid id, CancellationToken ct)
        => _catalog.GetBundleAsync(id, ct);

    // Attach or replace an OpenAPI spec and re-materialise the actions in the same
    // call. Uploading a spec and then forgetting to sync produced an integration whose
    // catalog silently did not match its spec; here that cannot happen, and the
    // response says how many actions moved.
    [HttpPost("{id:guid}/specs")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public Task<AttachSpecResult> AttachSpec(
        Guid id, [FromBody] AttachSpecRequest dto, CancellationToken ct)
        => _catalog.AttachSpecAsync(id, dto, ct);

    // Attach or replace a prompt skill scoped to this integration. It joins the agent
    // prompt on the next turn — no restart, and no visit to Admin → Skills.
    [HttpPost("{id:guid}/skills")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public Task<AiPromptSkillResponse> AttachSkill(
        Guid id, [FromBody] AttachSkillRequest dto, CancellationToken ct)
        => _catalog.AttachSkillAsync(id, dto, ct);

    // auth_method is computed from auth_config (or from the linked credential), so
    // accepting it silently produced a PUT that echoed back a different value than the
    // one sent — the field looked settable and was not.
    private static void RejectDerivedAuthMethod(string? authMethod)
    {
        if (string.IsNullOrWhiteSpace(authMethod)) return;
        throw new ValidationException(
            "auth_method is derived, not stored: set it inside auth_config " +
            $"(e.g. {{\"method\":\"{authMethod.Trim().ToLowerInvariant()}\"}}) or change the linked credential");
    }

    // Refused at write time rather than logged, because the alternative is what
    // actually happened: the integration saved, reported healthy, and every call it
    // made came back "authentication credentials were not provided" — a message about
    // the request that says nothing about the one field that was wrong.
    //
    // The trade is that an integration typed `netbox` sitting behind a gateway that
    // does accept Bearer is now refused. That is rare enough, and recoverable by
    // typing a different `type`, to be worth catching every ordinary case of this.
    // The check in BuildIntegrationAsync catches the ordinary case and gives a better
    // message than this can. This is what stands between a lost race — or any future
    // path that forgets to check — and a 500 carrying a stack trace, which is what a
    // raw DbUpdateException looked like from the outside.
    //
    // Only 23505 is translated, and only when it names an index on this table: a
    // duplicate anywhere else is a different fault and swallowing it as a conflict
    // would hide it.
    private async Task SaveTranslatingNameCollisionAsync(string name, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
                  && pg.ConstraintName?.StartsWith("IX_integrations_", StringComparison.Ordinal) == true)
        {
            throw new ConflictException(
                $"an integration named '{name}' already exists", "integration_name_taken");
        }
    }

    private static void RejectAuthSchemeTheTypeCannotUse(string? type, string? authConfig)
    {
        if (string.IsNullOrWhiteSpace(authConfig)) return;
        var parsed = IntegrationAuthConfig.TryParse(authConfig);
        if (IntegrationTypeProfile.AuthMismatch(type, parsed?.Method) is { } hint)
            throw new ValidationException(hint, code: "integration_auth_scheme_mismatch");
    }

    private static void RequireAbsoluteHttpUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ValidationException("base_url must be an absolute http(s) URL");
    }

    private static void RequireJsonObject(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ValidationException($"{field} must be a JSON object");
        }
        catch (JsonException)
        {
            throw new ValidationException($"{field} is not valid JSON");
        }
    }

    private async Task<IntegrationEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.Integrations.FirstOrDefaultAsync(i => i.IntegrationId == id && i.IsActive, ct);
        if (row is null) throw new NotFoundException("integration not found");
        return row;
    }

    // The name and auth_method of a linked credential, so the response can say which
    // credential is in use and which method it implies without a second round trip.
    private sealed record CredentialRef(string Name, string AuthMethod);

    // Rejects a credential that does not exist, is deleted, or cannot authenticate an
    // HTTP request at all — an SSH private key has no scheme to travel in, and silently
    // accepting one produces an integration that sends no credentials.
    private async Task<CredentialRef> RequireCredentialAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CredentialId == id && c.IsActive, ct);
        if (row is null) throw new ValidationException("auth_credential_id not found");

        if (!IntegrationCredentialAuth.IsHttpUsable(row.AuthMethod))
            throw new ValidationException(
                $"credential '{row.Name}' has auth_method '{row.AuthMethod}', which cannot authenticate " +
                "an HTTP request; use password, token, api_key or oauth2");

        return new CredentialRef(row.Name, row.AuthMethod);
    }

    private async Task<Dictionary<Guid, CredentialRef>> CredentialRefsAsync(
        IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];

        return await _db.Credentials.AsNoTracking()
            .Where(c => wanted.Contains(c.CredentialId))
            .ToDictionaryAsync(c => c.CredentialId, c => new CredentialRef(c.Name, c.AuthMethod), ct);
    }

    // Everything about the configured auth except the material. A ${secret:...}
    // reference is material for this purpose — it names the secret store's layout — so
    // any value carrying one is dropped rather than echoed.
    private static IntegrationAuthShape? ShapeOf(IntegrationAuthConfig? auth, string method)
    {
        if (auth is null) return null;
        return new IntegrationAuthShape
        {
            Method = method,
            Prefix = NonSecret(auth.Prefix),
            Header = NonSecret(auth.Header),
            Username = NonSecret(auth.Username),
            TokenUrl = NonSecret(auth.TokenUrl),
            ClientId = NonSecret(auth.ClientId),
            Scope = NonSecret(auth.Scope),
        };
    }

    private static string? NonSecret(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Contains("${secret:", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    private static IntegrationResponse ToResponse(
        IntegrationEntity i, int specCount, int actionCount, CredentialRef? credential = null)
    {
        // Malformed stored config must not break the list; report it as "none".
        IntegrationAuthConfig? auth = null;
        try { auth = IntegrationAuthConfig.TryParse(i.AuthConfig); } catch (ValidationException) { }

        // A linked credential supplies the method when the config does not declare one,
        // so the badge in the list matches what the request will actually send.
        var method = auth?.ResolvedMethod() ?? IntegrationAuthConfig.MethodNone;
        if (credential is not null && method == IntegrationAuthConfig.MethodNone)
            method = IntegrationCredentialAuth.MethodFor(credential.AuthMethod);

        // "Has credentials" has to mean "this integration will send something", not
        // "some auth-shaped row exists": a config that resolves to `none` and no linked
        // credential is an anonymous integration, and reporting it as credentialed is
        // what makes a 401 unexplainable.
        var hasCredentials = i.AuthCredentialId is not null || method != IntegrationAuthConfig.MethodNone;

        return new IntegrationResponse
        {
            IntegrationId = i.IntegrationId,
            Name = i.Name,
            Slug = i.Slug,
            Type = i.Type,
            Description = i.Description,
            BaseUrl = i.BaseUrl,
            AuthMethod = method,
            HasCredentials = hasCredentials,
            HasInlineCredentials = auth?.HasInlineMaterial() ?? false,
            AuthShape = ShapeOf(auth, method),
            Headers = i.HeadersJson,
            AuthCredentialId = i.AuthCredentialId,
            AuthCredentialName = credential?.Name,
            VerifySsl = i.VerifySsl,
            AllowPrivateNetwork = i.AllowPrivateNetwork,
            HealthCheckPath = i.HealthCheckPath,
            Status = i.Status,
            LastCheckError = i.LastCheckError,
            LastCheckedAt = i.LastCheckedAt,
            Enabled = i.Enabled,
            SpecCount = specCount,
            ActionCount = actionCount,
            IsActive = i.IsActive,
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt,
        };
    }
}
