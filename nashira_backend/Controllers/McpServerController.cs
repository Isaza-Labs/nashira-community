using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Mcp;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Common;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Security;
using McpServerEntity = nashira_backend.Data.Models.McpServer;
using Microsoft.AspNetCore.RateLimiting;

namespace nashira_backend.Controllers;

// Registered external MCP servers and their discovered tool catalogs.
//
// Registering a server means granting it the ability to run code on Nashira's
// behalf against whatever it is connected to, so writes are Admin-only. Reads are
// Viewer so the catalog is inspectable.
[ApiController]
[Route("api/mcp/servers")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class McpServerController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly ISecretProtector _protector;
    private readonly IMcpServerService _service;
    private readonly IAuditLogger _audit;

    public McpServerController(
        AppDbContext db, ICurrentUser user, ISecretProtector protector,
        IMcpServerService service, IAuditLogger audit)
    {
        _db = db;
        _user = user;
        _protector = protector;
        _service = service;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<ListResponse<McpServerResponse>>> Get(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        var q = _db.McpServers.AsNoTracking().Where(s => s.IsActive);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(s => s.Name).Skip(offset).Take(limit).ToListAsync(ct);
        var credentials = await CredentialRefsAsync(
            rows.Where(s => s.AuthCredentialId is not null).Select(s => s.AuthCredentialId!.Value), ct);

        return new OkObjectResult(new ListResponse<McpServerResponse>
        {
            Items = rows.Select(s => ToResponse(
                s, s.AuthCredentialId is { } cid ? credentials.GetValueOrDefault(cid) : null)).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<McpServerResponse>> GetById(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var credentials = await CredentialRefsAsync(row.AuthCredentialId is { } cid ? [cid] : [], ct);
        return ToResponse(row, row.AuthCredentialId is { } id2 ? credentials.GetValueOrDefault(id2) : null);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPost]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<McpServerResponse>> Post([FromBody] CreateMcpServer dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) throw new ValidationException("name is required");
        if (string.IsNullOrWhiteSpace(dto.Url)) throw new ValidationException("url is required");
        RequireAbsoluteHttpUrl(dto.Url);
        var authType = NormalizeAuthType(dto.AuthType);
        RequireJsonObject(dto.Headers, "headers");

        CredentialRef? credential = null;
        if (dto.AuthCredentialId is { } credentialId)
            credential = await RequireCredentialAsync(credentialId, ct);

        var name = dto.Name.Trim();
        if (await _db.McpServers.AnyAsync(s => s.Name == name && s.IsActive, ct))
            throw new ConflictException("an MCP server with this name already exists", "mcp_server_name_taken");

        var now = DateTime.UtcNow;
        var row = new McpServerEntity
        {
            McpServerId = Guid.NewGuid(),
            Name = name,
            Description = dto.Description,
            Url = dto.Url.Trim(),
            Transport = McpServerEntity.TransportHttp,
            AuthType = authType,
            AuthConfigEncrypted = EncryptAuth(dto.AuthConfig),
            AuthCredentialId = dto.AuthCredentialId,
            HeadersJson = dto.Headers,
            TlsSkipVerify = dto.TlsSkipVerify ?? false,
            AllowPrivateNetwork = dto.AllowPrivateNetwork ?? false,
            TrustToolHints = dto.TrustToolHints ?? false,
            Enabled = dto.Enabled ?? true,
            Status = nashira_backend.Data.Models.Integration.StatusUnknown,
            CreatedBy = _user.IsAuthenticated ? _user.UserId : null,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.McpServers.Add(row);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("mcp_server", row.McpServerId, "create", after: Snapshot(row), ct: ct);

        return new CreatedAtActionResult(nameof(GetById), "McpServer", new { id = row.McpServerId }, ToResponse(row));
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<McpServerResponse>> Update(
        Guid id, [FromBody] UpdateMcpServer dto, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);

        if (dto.Name is not null && dto.Name.Trim().Length > 0)
        {
            var name = dto.Name.Trim();
            if (await _db.McpServers.AnyAsync(s => s.Name == name && s.IsActive && s.McpServerId != id, ct))
                throw new ConflictException("an MCP server with this name already exists", "mcp_server_name_taken");
            row.Name = name;
        }
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Url is not null)
        {
            RequireAbsoluteHttpUrl(dto.Url);
            row.Url = dto.Url.Trim();
        }
        if (dto.AuthType is not null) row.AuthType = NormalizeAuthType(dto.AuthType);

        // Absent auth_config keeps the stored credentials: the API never returns them,
        // so the client cannot resubmit what it has, and treating "absent" as "clear"
        // would silently unauthenticate a server on every unrelated edit.
        if (dto.ClearAuthConfig == true) row.AuthConfigEncrypted = null;
        else if (dto.AuthConfig is not null) row.AuthConfigEncrypted = EncryptAuth(dto.AuthConfig);

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
        if (dto.TlsSkipVerify is not null) row.TlsSkipVerify = dto.TlsSkipVerify.Value;
        if (dto.AllowPrivateNetwork is not null) row.AllowPrivateNetwork = dto.AllowPrivateNetwork.Value;
        if (dto.TrustToolHints is not null) row.TrustToolHints = dto.TrustToolHints.Value;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // trust_tool_hints is the one to be able to point at afterwards: it decides
        // whether a remote server gets to tell us its tools are read-only, which in
        // turn decides whether calling them is confirmed with the user.
        await _audit.LogAsync("mcp_server", row.McpServerId, "update",
            before: before, after: Snapshot(row), ct: ct);

        return ToResponse(row);
    }

    [SkipAudit] // self-audits below, with a real before/after
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<McpServerResponse>> Delete(Guid id, CancellationToken ct)
    {
        var row = await Find(id, ct);
        var before = Snapshot(row);
        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync("mcp_server", row.McpServerId, "delete", before: before, ct: ct);

        return ToResponse(row);
    }

    // The endpoint and its trust settings. auth_config may hold material inline, so it
    // travels as a flag.
    private static object Snapshot(McpServerEntity m) => new
    {
        name = m.Name,
        url = m.Url,
        transport = m.Transport,
        auth_type = m.AuthType,
        auth_credential_id = m.AuthCredentialId,
        tls_skip_verify = m.TlsSkipVerify,
        allow_private_network = m.AllowPrivateNetwork,
        trust_tool_hints = m.TrustToolHints,
        enabled = m.Enabled,
    };

    // Begin the OAuth authorization-code flow: returns the URL the browser must
    // visit to consent. The callback (McpOAuthCallbackController) completes it.
    // Ported from FlowWeaver's McpServerController.OAuthStart.
    [HttpPost("{id:guid}/oauth/start")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<McpOAuthStartResponse>> OAuthStart(
        Guid id, [FromServices] IMcpOAuthFlowService oauth, CancellationToken ct)
    {
        var redirectUri = McpOAuthRedirect.CallbackUri(Request, id);
        var result = await oauth.StartAsync(id, redirectUri, ct);
        if (result.Error is not null)
            throw new ValidationException(result.Error);
        return new McpOAuthStartResponse { AuthorizationUrl = result.AuthorizationUrl! };
    }

    [HttpPost("{id:guid}/sync")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<McpSyncResponse>> Sync(Guid id, CancellationToken ct)
    {
        var r = await _service.SyncToolsAsync(id, ct);
        return new McpSyncResponse
        {
            Discovered = r.Discovered,
            Created = r.Created,
            Updated = r.Updated,
            Disappeared = r.Disappeared,
            Reappeared = r.Reappeared,
        };
    }

    [HttpPost("{id:guid}/check")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    [SkipAudit] // diagnostic, not a configuration mutation
    public async Task<ActionResult<McpHealthResponse>> Check(Guid id, CancellationToken ct)
    {
        var r = await _service.CheckAsync(id, ct);
        return new McpHealthResponse
        {
            Status = r.Status, ElapsedMs = r.ElapsedMs, Error = r.Error, ToolCount = r.ToolCount,
        };
    }

    [HttpGet("{id:guid}/tools")]
    public async Task<ActionResult<ListResponse<McpToolResponse>>> Tools(
        Guid id, bool includeRetired = false, int limit = 200, int offset = 0, CancellationToken ct = default)
    {
        (limit, offset) = Pagination.Clamp(limit, offset);
        await Find(id, ct);

        var q = _db.McpTools.AsNoTracking().Where(t => t.McpServerId == id && t.IsActive);
        if (!includeRetired) q = q.Where(t => t.DisappearedAt == null);

        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(t => t.Name).Skip(offset).Take(limit).ToListAsync(ct);
        return new OkObjectResult(new ListResponse<McpToolResponse>
        {
            Items = rows.Select(ToToolResponse).ToList(),
            Total = total,
            Limit = limit,
            Offset = offset,
        });
    }

    [HttpPut("{id:guid}/tools/{toolId:guid}")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Admin")]
    public async Task<ActionResult<McpToolResponse>> UpdateTool(
        Guid id, Guid toolId, [FromBody] UpdateMcpTool dto, CancellationToken ct)
    {
        var row = await _db.McpTools.FirstOrDefaultAsync(
            t => t.McpToolId == toolId && t.McpServerId == id && t.IsActive, ct);
        if (row is null) throw new NotFoundException("mcp tool not found");

        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToToolResponse(row);
    }

    // Manual invocation, for verifying a server works before a workflow depends on
    // it. Operator: it executes against a live external system.
    [HttpPost("{id:guid}/call")]
    [EnableRateLimiting(RateLimitingConfiguration.WriteNormal)]
    [Authorize(Policy = "Operator")]
    public async Task<ActionResult<McpCallResponse>> Call(
        Guid id, [FromBody] McpCallRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Tool)) throw new ValidationException("tool is required");
        var args = req.Arguments ?? EmptyArgs;
        if (args.ValueKind != JsonValueKind.Object)
            throw new ValidationException("arguments must be a JSON object");

        var result = await _service.CallAsync(id, req.Tool.Trim(), args, ct);
        return new McpCallResponse
        {
            Content = result.Content, Structured = result.Structured, IsError = result.IsError,
        };
    }

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    private byte[]? EncryptAuth(JsonElement? authConfig)
    {
        if (authConfig is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (el.ValueKind != JsonValueKind.Object)
            throw new ValidationException("auth_config must be a JSON object");

        McpAuthConfig? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<McpAuthConfig>(el.GetRawText());
        }
        catch (JsonException)
        {
            throw new ValidationException("auth_config does not match the expected shape");
        }
        return McpAuthConfigCodec.Encrypt(parsed, _protector);
    }

    private sealed record CredentialRef(string Name, string AuthMethod);

    // Same rule as Integration: the credential must exist, be active, and carry
    // material an HTTP request can actually send. An SSH key cannot.
    private async Task<CredentialRef> RequireCredentialAsync(Guid id, CancellationToken ct)
    {
        var row = await _db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CredentialId == id && c.IsActive, ct);
        if (row is null) throw new ValidationException("auth_credential_id not found");

        if (!McpCredentialAuth.IsHttpUsable(row.AuthMethod))
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

    private static string NormalizeAuthType(string? raw)
    {
        var v = (raw ?? McpServerEntity.AuthNone).Trim().ToLowerInvariant();
        if (!McpServerEntity.AuthTypes.Contains(v))
            throw new ValidationException($"auth_type must be one of: {string.Join(", ", McpServerEntity.AuthTypes)}");
        return v;
    }

    private static void RequireAbsoluteHttpUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ValidationException("url must be an absolute http(s) URL");
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

    private async Task<McpServerEntity> Find(Guid id, CancellationToken ct)
    {
        var row = await _db.McpServers.FirstOrDefaultAsync(s => s.McpServerId == id && s.IsActive, ct);
        if (row is null) throw new NotFoundException("mcp server not found");
        return row;
    }

    private static McpServerResponse ToResponse(McpServerEntity s, CredentialRef? credential = null) => new()
    {
        McpServerId = s.McpServerId,
        Name = s.Name,
        Description = s.Description,
        Url = s.Url,
        Transport = s.Transport,
        AuthType = s.AuthType,
        HasCredentials = s.AuthConfigEncrypted is { Length: > 0 } || s.AuthCredentialId is not null,
        AuthCredentialId = s.AuthCredentialId,
        AuthCredentialName = credential?.Name,
        TlsSkipVerify = s.TlsSkipVerify,
        AllowPrivateNetwork = s.AllowPrivateNetwork,
        TrustToolHints = s.TrustToolHints,
        Status = s.Status,
        LastCheckError = s.LastCheckError,
        LastCheckedAt = s.LastCheckedAt,
        ToolsSyncedAt = s.ToolsSyncedAt,
        ToolCount = s.ToolCount,
        Enabled = s.Enabled,
        IsActive = s.IsActive,
        CreatedAt = s.CreatedAt,
        UpdatedAt = s.UpdatedAt,
    };

    private static McpToolResponse ToToolResponse(nashira_backend.Data.Models.McpTool t) => new()
    {
        McpToolId = t.McpToolId,
        McpServerId = t.McpServerId,
        Name = t.Name,
        Title = t.Title,
        Description = t.Description,
        InputSchema = ParseSchema(t.InputSchemaJson),
        ReadOnlyHint = t.ReadOnlyHint,
        DisappearedAt = t.DisappearedAt,
        Enabled = t.Enabled,
        UpdatedAt = t.UpdatedAt,
    };

    private static JsonElement ParseSchema(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyArgs;
        }
    }
}
