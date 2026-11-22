using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Net;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Services.Integration;

public interface IIntegrationOAuthTokenService
{
    // The access token is secret-derived material: only a caller who may resolve stored
    // secrets (an authenticated admin) gets it, whether it is freshly granted or served
    // from the cache, and every use marks the current execution as holding resolved
    // material so the step's output is withheld. A denied caller gets a
    // ForbiddenException (secret_access_denied) and must stop, not retry.
    Task<string> GetAccessTokenAsync(
        IntegrationEntity integration, IntegrationAuthConfig auth, ICurrentUser caller, CancellationToken ct);
}

// Obtains and caches OAuth2 client-credentials access tokens, one entry per
// integration.
//
// Singleton with an in-memory cache: these tokens are short-lived and re-obtainable
// from credentials the admin already stored, so persisting them would add a second
// place for a bearer token to leak while buying only the first request after a
// restart. Refresh tokens — which cannot be re-derived — are a different problem and
// live encrypted on McpServer.
public sealed class IntegrationOAuthTokenService : IIntegrationOAuthTokenService
{
    // Renew this far before the reported expiry, so a token cannot expire in flight
    // between the check and the upstream receiving it.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private sealed record CacheEntry(string Token, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();
    // One grant in flight per integration: a burst of calls on a cold cache would
    // otherwise fire N identical token requests, which some providers rate-limit.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    private readonly IServiceScopeFactory _scopes;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IntegrationOAuthTokenService> _logger;

    public IntegrationOAuthTokenService(
        IServiceScopeFactory scopes, IUrlGuard urlGuard,
        IHttpClientFactory httpFactory, ILogger<IntegrationOAuthTokenService> logger)
    {
        _scopes = scopes;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(
        IntegrationEntity integration, IntegrationAuthConfig auth, ICurrentUser caller, CancellationToken ct)
    {
        // Authorization comes before the cache is even looked at: a token an admin's
        // grant put there must not be usable by whoever happens to ask next.
        if (!SecretAccessPolicy.MayResolveStored(caller))
        {
            _logger.LogWarning("integration.oauth.denied integration={Integration}", integration.Name);
            throw new ForbiddenException(SecretAccessPolicy.DeniedMessage, SecretResolver.AccessDeniedCode);
        }

        var token = await GetOrGrantAsync(integration, auth, caller, ct);
        // Cache hits resolve nothing, so the resolver never marks them; the token is
        // exactly as sensitive as the credentials it was granted from.
        SecretResolutionScope.MarkResolved();
        return token;
    }

    private async Task<string> GetOrGrantAsync(
        IntegrationEntity integration, IntegrationAuthConfig auth, ICurrentUser caller, CancellationToken ct)
    {
        if (TryGetCached(integration.IntegrationId, out var cached)) return cached;

        var gate = _gates.GetOrAdd(integration.IntegrationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Re-check: another caller may have obtained one while we waited.
            if (TryGetCached(integration.IntegrationId, out cached)) return cached;

            var (token, lifetime) = await GrantAsync(integration, auth, caller, ct);
            _cache[integration.IntegrationId] = new CacheEntry(token, DateTime.UtcNow.Add(lifetime));
            return token;
        }
        finally
        {
            gate.Release();
        }
    }

    // Drops a cached token, so rotating an integration's credentials takes effect on
    // the next call instead of whenever the old token happens to expire.
    public void Invalidate(Guid integrationId) => _cache.TryRemove(integrationId, out _);

    private bool TryGetCached(Guid id, out string token)
    {
        token = string.Empty;
        if (!_cache.TryGetValue(id, out var entry)) return false;
        if (entry.ExpiresAtUtc <= DateTime.UtcNow) return false;
        token = entry.Token;
        return true;
    }

    private async Task<(string Token, TimeSpan Lifetime)> GrantAsync(
        IntegrationEntity integration, IntegrationAuthConfig auth, ICurrentUser caller, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(auth.TokenUrl))
            throw new InvalidOperationException(
                $"integration '{integration.Name}' uses oauth2_client_credentials but auth_config has no token_url");

        using var scope = _scopes.CreateScope();
        // A fresh scope has no HTTP context, so without this a background run would
        // resolve the client credentials as an anonymous caller. The caller was already
        // authorized above; the resolver still applies its own check to every lookup.
        scope.ServiceProvider.GetRequiredService<MutableCurrentUser>()
            .Bind(caller.UserId, caller.Username, caller.Roles);
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretResolver>();

        var tokenUrl = await secrets.SubstituteAsync(auth.TokenUrl!, ct);
        var clientId = await Resolve(secrets, auth.ClientId, ct);
        var clientSecret = await Resolve(secrets, auth.ClientSecret, ct);

        // The token endpoint receives the client secret, so it is exactly as much of
        // an SSRF target as the API itself.
        _urlGuard.EnsureSafe(tokenUrl, allowPrivate: integration.AllowPrivateNetwork);

        var client = _httpFactory.CreateClient(
            integration.VerifySsl ? IntegrationHttpClients.Secure : IntegrationHttpClients.Insecure);

        // client_secret_basic first: it keeps the secret out of the body, and every
        // compliant provider must support it. Some providers (e.g. Action1) reject
        // Basic outright, so a 400/401 gets one retry with the credentials in the
        // form body (client_secret_post).
        var (status, body) = await SendTokenRequestAsync(
            client, tokenUrl, auth, clientId, clientSecret, useBasicAuth: true, ct);
        if (status is 400 or 401)
        {
            _logger.LogInformation(
                "integration.oauth.basic_rejected integration={Integration} status={Status}; retrying with client_secret_post",
                integration.Name, status);
            (status, body) = await SendTokenRequestAsync(
                client, tokenUrl, auth, clientId, clientSecret, useBasicAuth: false, ct);
        }

        if (status is < 200 or >= 300)
        {
            _logger.LogWarning("integration.oauth.failed integration={Integration} status={Status}",
                integration.Name, status);
            // The body can echo the client_secret back on some providers, so it is
            // deliberately not included in the message.
            throw new InvalidOperationException(
                $"OAuth token request for '{integration.Name}' failed with HTTP {status}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(
                $"OAuth token response for '{integration.Name}' has no access_token");

        var seconds = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var s) ? s : 3600;
        var lifetime = TimeSpan.FromSeconds(Math.Max(30, seconds)) - ExpiryMargin;
        if (lifetime <= TimeSpan.Zero) lifetime = TimeSpan.FromSeconds(30);

        return (at.GetString()!, lifetime);
    }

    private async Task<(int Status, string Body)> SendTokenRequestAsync(
        HttpClient client, string tokenUrl, IntegrationAuthConfig auth,
        string? clientId, string? clientSecret, bool useBasicAuth, CancellationToken ct)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (!string.IsNullOrEmpty(auth.Scope)) form["scope"] = auth.Scope!;
        if (!useBasicAuth)
        {
            form["client_id"] = clientId ?? string.Empty;
            form["client_secret"] = clientSecret ?? string.Empty;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        if (useBasicAuth)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        using var response = await client.SendAsync(request, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        return ((int)response.StatusCode, body);
    }

    private static async Task<string?> Resolve(ISecretResolver secrets, string? value, CancellationToken ct) =>
        string.IsNullOrEmpty(value) ? value : await secrets.SubstituteAsync(value, ct);
}

// Named clients for outbound integration traffic. The insecure variant accepts any
// TLS certificate (VerifySsl=false), which self-hosted appliances with private CAs
// need; it is opt-in per integration and never the default.
public static class IntegrationHttpClients
{
    public const string Secure = "integration";
    public const string Insecure = "integration-insecure";
}
