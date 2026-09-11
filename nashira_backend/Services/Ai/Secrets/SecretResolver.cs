using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Auth;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;
using nashira_backend.Services.Trace;

namespace nashira_backend.Services.Ai.Secrets;

// Ciphertext-aware resolver for ${secret:<source>:<id|name>:<field>}. The REST
// executor and the integration layer pass raw template text (URLs, headers, bodies)
// through SubstituteAsync right before hitting the wire, so secrets only live as
// plaintext inside a single request's HttpRequestMessage.
//
// Lookups hit the DB on every call — no cache, because rotation must take effect
// immediately. The load is small: templates rarely carry more than a couple of
// markers, and each is a single indexed lookup.
public sealed partial class SecretResolver : ISecretResolver
{
    // ${secret:<source>:<id|name>:<field>}
    // Square-bracket syntax is not supported: it keeps the grammar to one shape, and
    // every reference the platform stores is written by this codebase.
    [GeneratedRegex(@"\$\{secret:(?<source>[a-z_]+):(?<idOrName>[^:}]+):(?<field>[^}]+)\}",
        RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    /// <summary>
    /// One <c>${secret:…}</c> marker found in a piece of text. <see cref="Raw"/> is the
    /// marker exactly as written, which is what a bundle's <c>requires.secrets</c> carries.
    /// </summary>
    public sealed record SecretReference(string Raw, string Source, string IdOrName, string Field);

    /// <summary>
    /// Every marker in <paramref name="text"/>, in order, using the same grammar the
    /// resolver substitutes with — so what the bundle exporter lists as required is
    /// exactly what a run would try to resolve. Never resolves anything.
    /// </summary>
    public static IReadOnlyList<SecretReference> References(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("${secret:", StringComparison.Ordinal))
            return [];
        var found = new List<SecretReference>();
        foreach (Match m in TokenRegex().Matches(text))
            found.Add(new SecretReference(
                m.Value, m.Groups["source"].Value, m.Groups["idOrName"].Value, m.Groups["field"].Value));
        return found;
    }

    /// <summary>True when <paramref name="value"/> is exactly one marker and nothing else.</summary>
    public static bool IsSecretReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var m = TokenRegex().Match(value.Trim());
        return m.Success && m.Index == 0 && m.Length == value.Trim().Length;
    }

    // Reserved source name: it never reaches the secrets table.
    internal const string SessionSource = "session";
    internal const string SessionIdOrName = "current";
    internal const string SessionField = "jwt";

    // The reference the built-in na_* specs carry as their bearer.
    public const string SessionJwtRef = "${secret:session:current:jwt}";

    private readonly AppDbContext _db;
    private readonly ISecretProtector _crypto;
    private readonly ICurrentUser _user;
    private readonly IHttpContextAccessor _http;
    private readonly IJwtTokenService _jwt;
    private readonly ITraceLogger _trace;
    private readonly ILogger<SecretResolver> _logger;

    public SecretResolver(
        AppDbContext db, ISecretProtector crypto, ICurrentUser user,
        IHttpContextAccessor http, IJwtTokenService jwt, ITraceLogger trace,
        ILogger<SecretResolver> logger)
    {
        _db = db;
        _crypto = crypto;
        _user = user;
        _http = http;
        _jwt = jwt;
        _trace = trace;
        _logger = logger;
    }

    public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct)
        => ResolveCoreAsync(source, idOrName, field, allowSessionRefs: false, ct);

    // Exposed for the executor's own bookkeeping and for tests; the interface stays
    // session-free so a caller has to ask for it deliberately.
    internal Task<string?> ResolveAsync(
        string source, string idOrName, string field, bool allowSessionRefs, CancellationToken ct)
        => ResolveCoreAsync(source, idOrName, field, allowSessionRefs, ct);

    private async Task<string?> ResolveCoreAsync(
        string source, string idOrName, string field, bool allowSessionRefs, CancellationToken ct)
    {
        var src = source.Trim().ToLowerInvariant();
        var key = idOrName.Trim();
        var fld = field.Trim();

        Guid.TryParse(key, out var id);
        var isGuid = id != Guid.Empty;

        try
        {
            string? value;
            if (src == SessionSource)
            {
                if (!allowSessionRefs)
                {
                    _logger.LogWarning(
                        "secret.resolve.session_refused id_or_name={IdOrName} field={Field} — a session " +
                        "credential was requested by a caller that is not talking to this backend; the " +
                        "marker is left literal", key, fld);
                    return null;
                }
                value = ResolveSession(key, fld);
            }
            else
            {
                value = src switch
                {
                    "secret" => await ResolveSecretAsync(id, key, isGuid, fld, ct),
                    "credential" => await ResolveCredentialAsync(id, key, isGuid, fld, ct),
                    "ai_provider" => await ResolveAiProviderAsync(id, key, isGuid, fld, ct),
                    "integration" => await ResolveIntegrationAsync(id, key, isGuid, fld, ct),
                    _ => null,
                };
            }

            // Persisted access record. Without it, the only trace of a secret being
            // decrypted is the LogDebug below — off at the default level, and gone as
            // soon as the container is recycled. For a platform whose job is holding
            // device credentials, "which credential did that run use, and when" is the
            // central audit question and it needs an answer that survives.
            //
            // trace_events rather than audit_events on purpose: this is a read, it
            // happens once per step per device (so the volume is run-shaped, not
            // admin-shaped), and it wants the retention sweeper. The mutation trail for
            // the same secrets lives in audit_events.
            //
            // The tuple is safe to persist — `field` is a schema field name ("api_key",
            // "password"), never the value. `value_bytes` is recorded instead of the
            // value so a rotation is still visible as a length change without exposing
            // anything.
            _trace.Event(
                TraceEvent.CategorySystem, "secret.access",
                new { secret_source = src, id_or_name = key, field = fld, value_bytes = value?.Length },
                error: value is null ? "unresolved" : null);

            // Debug-only: the key tuple, never the value.
            if (value is null)
            {
                _logger.LogWarning(
                    "secret.resolve.miss source={Source} id_or_name={IdOrName} field={Field}",
                    src, key, fld);
            }
            else
            {
                _logger.LogDebug(
                    "secret.resolve.hit source={Source} id_or_name={IdOrName} field={Field} value_bytes={ValueBytes}",
                    src, key, fld, value.Length);
            }
            return value;
        }
        catch (Exception ex)
        {
            // Recorded too: a burst of failed resolutions is what a credential deleted
            // mid-run, or a probe for secrets that do not exist, looks like from outside.
            _trace.Event(
                TraceEvent.CategorySystem, "secret.access",
                new { secret_source = src, id_or_name = key, field = fld },
                error: ex.Message);

            _logger.LogWarning(ex,
                "secret.resolve.failed source={Source} id_or_name={IdOrName} field={Field}",
                src, key, fld);
            return null;
        }
    }

    public Task<string> SubstituteAsync(string template, CancellationToken ct)
        => SubstituteAsync(template, allowSessionRefs: false, ct);

    public async Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${secret:", StringComparison.Ordinal))
            return template;

        // Regex.Replace cannot await, so matches are extracted, resolved in sequence and
        // the string rebuilt. Sequential is fine: real templates carry two or three
        // markers, not hundreds.
        var matches = TokenRegex().Matches(template);
        if (matches.Count == 0) return template;

        var sb = new StringBuilder(template.Length);
        var cursor = 0;
        foreach (Match m in matches)
        {
            sb.Append(template, cursor, m.Index - cursor);
            var value = await ResolveCoreAsync(
                m.Groups["source"].Value, m.Groups["idOrName"].Value, m.Groups["field"].Value,
                allowSessionRefs, ct);

            // Leave the original marker in place when unresolved, so it shows up in logs
            // instead of silently shipping an empty string.
            sb.Append(value ?? m.Value);
            cursor = m.Index + m.Length;
        }
        sb.Append(template, cursor, template.Length - cursor);
        return sb.ToString();
    }

    // ─── per-source lookups ─────────────────────────────────────────────

    private async Task<string?> ResolveSecretAsync(
        Guid id, string name, bool isGuid, string field, CancellationToken ct)
    {
        if (!string.Equals(field, "value", StringComparison.OrdinalIgnoreCase))
            return null;

        var q = _db.Secrets.AsNoTracking().Where(s => s.IsActive);
        var row = isGuid
            ? await q.FirstOrDefaultAsync(s => s.SecretId == id, ct)
            : await q.FirstOrDefaultAsync(s => s.Name == name, ct);
        return row is null ? null : _crypto.Decrypt(row.EncryptedValue);
    }

    // Every material a Credential can carry, addressed by the same names the API uses.
    // A credential whose auth_method does not carry the requested material resolves to
    // null rather than to another field's value.
    private async Task<string?> ResolveCredentialAsync(
        Guid id, string name, bool isGuid, string field, CancellationToken ct)
    {
        var q = _db.Credentials.AsNoTracking().Where(c => c.IsActive);
        var row = isGuid
            ? await q.FirstOrDefaultAsync(c => c.CredentialId == id, ct)
            : await q.FirstOrDefaultAsync(c => c.Name == name, ct);
        if (row is null) return null;

        return field.ToLowerInvariant() switch
        {
            "username" => row.Username,
            "password" => _crypto.Decrypt(row.EncryptedPassword),
            "private_key" or "privatekey" => _crypto.Decrypt(row.EncryptedPrivateKey),
            "passphrase" or "key_passphrase" => _crypto.Decrypt(row.EncryptedKeyPassphrase),
            "token" or "api_key" or "apikey" => _crypto.Decrypt(row.EncryptedToken),
            "client_secret" or "clientsecret" => _crypto.Decrypt(row.EncryptedClientSecret),
            "client_id" or "clientid" => row.ClientId,
            _ => null,
        };
    }

    private async Task<string?> ResolveAiProviderAsync(
        Guid id, string name, bool isGuid, string field, CancellationToken ct)
    {
        if (!string.Equals(field, "api_key", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(field, "apikey", StringComparison.OrdinalIgnoreCase))
            return null;

        var q = _db.AIProviders.AsNoTracking().Where(p => p.IsActive);
        var row = isGuid
            ? await q.FirstOrDefaultAsync(p => p.AIProviderId == id, ct)
            : await q.FirstOrDefaultAsync(p => p.Name == name, ct);
        return row is null ? null : _crypto.Decrypt(row.EncryptedApiKey);
    }

    // Integration.AuthConfig is a free-form JSON object; the field is a dotted path
    // evaluated against it (e.g. "token", "basic.password"). That keeps the resolver
    // flexible enough for bearer, basic, apiKey and oauth2 configurations without a new
    // source type per shape.
    //
    // Values inside AuthConfig may themselves be ${secret:...} references — that is the
    // normal case — so the result is substituted once more before being returned. The
    // recursion is one level deep on purpose: an integration pointing at another
    // integration is a configuration mistake, not a feature, and unbounded expansion is
    // how a self-referencing row turns into a hang.
    private async Task<string?> ResolveIntegrationAsync(
        Guid id, string name, bool isGuid, string field, CancellationToken ct)
    {
        var q = _db.Integrations.AsNoTracking().Where(i => i.IsActive);
        var row = isGuid
            ? await q.FirstOrDefaultAsync(i => i.IntegrationId == id, ct)
            : await q.FirstOrDefaultAsync(i => i.Name == name, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.AuthConfig)) return null;

        JsonElement node;
        try
        {
            using var doc = JsonDocument.Parse(row.AuthConfig);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            node = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        foreach (var segment in field.Split('.'))
        {
            if (node.ValueKind != JsonValueKind.Object) return null;
            if (!node.TryGetProperty(segment, out var next)) return null;
            node = next;
        }

        var raw = node.ValueKind switch
        {
            JsonValueKind.String => node.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => node.GetRawText(),
            _ => null,
        };
        if (raw is null || !raw.Contains("${secret:", StringComparison.Ordinal)) return raw;

        // Session markers are never reachable through a stored config: the request that
        // is being authenticated is not necessarily the one whose bearer would be lent.
        return await SubstituteAsync(raw, allowSessionRefs: false, ct);
    }

    // The bearer the REST executor uses to call back into Nashira's own API. Accepts
    // exactly `current:jwt`; anything else under `session:` returns null and the marker
    // stays literal, so the namespace cannot grow by accident.
    //
    // The point of borrowing the caller's token rather than minting a service one is
    // that the agent then calls the API *as the user it is talking to*. Every per-user
    // permission check the platform already performs applies unchanged, and the agent
    // can never reach something the user could not reach themselves. A long-lived
    // service token would have quietly voided that.
    //
    // Two paths:
    //   - An HTTP request is in flight (web chat): reuse that request's own bearer, so
    //     the self-call is indistinguishable from the user making it.
    //   - No request (messaging channels, scheduled agent runs on the job queue): mint
    //     a short-lived token from whatever identity is bound on ICurrentUser. Those
    //     roles are the ones the caller actually holds, so the minted token is capped
    //     the same way. Nothing bound means genuinely anonymous, and we resolve to null
    //     rather than inventing an identity.
    private string? ResolveSession(string idOrName, string field)
    {
        if (!string.Equals(idOrName, SessionIdOrName, StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(field, SessionField, StringComparison.OrdinalIgnoreCase)) return null;

        var header = _http.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(header))
        {
            const string prefix = "Bearer ";
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var token = header[prefix.Length..].Trim();
            return token.Length > 0 ? token : null;
        }

        if (!_user.IsAuthenticated) return null;

        Guid userId;
        // ICurrentUser.UserId throws on a principal with no NameIdentifier claim. That
        // is a broken token, not a reason to fail the whole turn with a stack trace.
        try { userId = _user.UserId; }
        catch (InvalidOperationException) { return null; }
        if (userId == Guid.Empty) return null;

        return _jwt.CreateAccessToken(userId, _user.Username ?? string.Empty, _user.Roles, out _);
    }
}
