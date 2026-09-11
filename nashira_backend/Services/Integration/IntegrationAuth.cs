using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Security;
using CredentialEntity = nashira_backend.Data.Models.Credential;
using IntegrationEntity = nashira_backend.Data.Models.Integration;

namespace nashira_backend.Services.Integration;

// Parsed shape of Integration.AuthConfig. Every string field may arrive as a
// ${secret:secret:<name>:value} reference; IntegrationAuthApplier resolves them right
// before the request goes out, so plaintext exists only inside one call.
public sealed class IntegrationAuthConfig
{
    public const string MethodNone = "none";
    public const string MethodToken = "token";
    public const string MethodBearer = "bearer";
    public const string MethodBasic = "basic";
    public const string MethodApiKey = "api_key";
    public const string MethodOAuthClientCredentials = "oauth2_client_credentials";

    public static readonly string[] Methods =
    [
        MethodNone, MethodToken, MethodBearer, MethodBasic, MethodApiKey, MethodOAuthClientCredentials,
    ];

    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }

    // `token` method only: the Authorization scheme. NetBox wants "Token", most
    // others want "Bearer" — hence a field rather than a hardcoded prefix.
    [JsonPropertyName("prefix")] public string? Prefix { get; set; }

    // `api_key` method only: which header carries the key.
    [JsonPropertyName("header")] public string? Header { get; set; }

    // oauth2_client_credentials.
    [JsonPropertyName("token_url")] public string? TokenUrl { get; set; }
    [JsonPropertyName("client_id")] public string? ClientId { get; set; }
    [JsonPropertyName("client_secret")] public string? ClientSecret { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }

    public static IntegrationAuthConfig? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<IntegrationAuthConfig>(json);
        }
        catch (JsonException)
        {
            // A malformed config must not read as "no auth" — an anonymous request
            // to an authenticated API returns a confusing upstream 401 instead of
            // naming the real problem.
            throw new Exceptions.ValidationException("integration auth_config is not valid JSON");
        }
    }

    // True when the config carries secret MATERIAL rather than only the shape
    // (method / header / scheme prefix). That distinction is what decides whether a
    // linked credential has any effect: inline material wins, so an integration
    // created with a token baked into auth_config quietly ignores the credential it
    // is pointed at. Surfaced on the response as has_inline_credentials so that is
    // visible instead of being deduced from a 401.
    public bool HasInlineMaterial() =>
        !string.IsNullOrWhiteSpace(Token)
        || !string.IsNullOrWhiteSpace(Password)
        || !string.IsNullOrWhiteSpace(ClientSecret);

    // An empty method is inferred from what is filled in, so a config that only
    // carries a token does not additionally have to declare that it is a token.
    public string ResolvedMethod()
    {
        if (!string.IsNullOrWhiteSpace(Method)) return Method.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(Token)) return MethodToken;
        if (!string.IsNullOrEmpty(Username)) return MethodBasic;
        return MethodNone;
    }
}

// Bridges a stored Credential into the integration auth shape.
//
// The split is deliberate: AuthConfig describes the SHAPE of the authentication
// (which method, which header, which scheme prefix) and the credential supplies the
// MATERIAL. So the same NetBox token is entered once in /admin/credentials and reused
// by every integration pointing at it, while each integration keeps its own knobs —
// which is what makes NetBox's "Token" prefix expressible with a generic token
// credential.
public static class IntegrationCredentialAuth
{
    // Which integration method a credential's auth_method implies. Only consulted when
    // the config does not declare one, so an explicit method always wins.
    public static string MethodFor(string? credentialAuthMethod) =>
        (credentialAuthMethod ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            CredentialEntity.AuthMethodPassword => IntegrationAuthConfig.MethodBasic,
            CredentialEntity.AuthMethodToken => IntegrationAuthConfig.MethodBearer,
            CredentialEntity.AuthMethodApiKey => IntegrationAuthConfig.MethodApiKey,
            CredentialEntity.AuthMethodOAuth2 => IntegrationAuthConfig.MethodOAuthClientCredentials,
            // `key` is an SSH private key. There is no HTTP scheme that carries one,
            // so such a credential authenticates nothing here.
            _ => IntegrationAuthConfig.MethodNone,
        };

    public static bool IsHttpUsable(string? credentialAuthMethod) =>
        MethodFor(credentialAuthMethod) != IntegrationAuthConfig.MethodNone;

    // Copies the credential's material into the fields the config left empty. Values
    // already present in the config win, so an integration can override anything
    // non-secret without detaching from the credential.
    public static IntegrationAuthConfig Merge(
        IntegrationAuthConfig? shape, CredentialEntity credential, ISecretProtector protector)
    {
        var cfg = new IntegrationAuthConfig
        {
            Method = shape?.Method,
            Token = shape?.Token,
            Username = shape?.Username,
            Password = shape?.Password,
            Prefix = shape?.Prefix,
            Header = shape?.Header,
            TokenUrl = shape?.TokenUrl,
            ClientId = shape?.ClientId,
            ClientSecret = shape?.ClientSecret,
            Scope = shape?.Scope,
        };

        if (string.IsNullOrWhiteSpace(cfg.Method))
            cfg.Method = MethodFor(credential.AuthMethod);

        switch ((credential.AuthMethod ?? string.Empty).Trim().ToLowerInvariant())
        {
            case CredentialEntity.AuthMethodPassword:
                cfg.Username = Fallback(cfg.Username, credential.Username);
                cfg.Password = Fallback(cfg.Password, protector.Decrypt(credential.EncryptedPassword));
                break;

            case CredentialEntity.AuthMethodToken:
                cfg.Token = Fallback(cfg.Token, protector.Decrypt(credential.EncryptedToken));
                cfg.Username = Fallback(cfg.Username, credential.Username);
                break;

            case CredentialEntity.AuthMethodApiKey:
                cfg.Token = Fallback(cfg.Token, protector.Decrypt(credential.EncryptedToken));
                cfg.Header = Fallback(cfg.Header, credential.ApiKeyHeader);
                break;

            case CredentialEntity.AuthMethodOAuth2:
                cfg.ClientId = Fallback(cfg.ClientId, credential.ClientId);
                cfg.ClientSecret = Fallback(cfg.ClientSecret, protector.Decrypt(credential.EncryptedClientSecret));
                cfg.TokenUrl = Fallback(cfg.TokenUrl, credential.TokenUrl);
                cfg.Scope = Fallback(cfg.Scope, credential.Scopes);
                break;

            // `key`: nothing to contribute to an HTTP request.
        }

        return cfg;
    }

    private static string? Fallback(string? preferred, string? candidate) =>
        string.IsNullOrWhiteSpace(preferred) ? candidate : preferred;
}

public interface IIntegrationAuthApplier
{
    // Applies the integration's static headers and credentials to the request.
    // Throws when an OAuth grant fails, so the caller reports "credentials broken"
    // rather than silently sending an anonymous request.
    Task ApplyAsync(HttpRequestMessage request, IntegrationEntity integration, CancellationToken ct);
}

public sealed class IntegrationAuthApplier : IIntegrationAuthApplier
{
    private readonly ISecretResolver _secrets;
    private readonly IIntegrationOAuthTokenService _oauth;
    private readonly AppDbContext _db;
    private readonly ISecretProtector _protector;
    private readonly ILogger<IntegrationAuthApplier> _logger;

    public IntegrationAuthApplier(
        ISecretResolver secrets, IIntegrationOAuthTokenService oauth, AppDbContext db,
        ISecretProtector protector, ILogger<IntegrationAuthApplier> logger)
    {
        _secrets = secrets;
        _oauth = oauth;
        _db = db;
        _protector = protector;
        _logger = logger;
    }

    public async Task ApplyAsync(HttpRequestMessage request, IntegrationEntity integration, CancellationToken ct)
    {
        ApplyStaticHeaders(request, integration.HeadersJson);

        var auth = IntegrationAuthConfig.TryParse(integration.AuthConfig);

        if (integration.AuthCredentialId is { } credentialId)
        {
            var credential = await _db.Credentials.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CredentialId == credentialId && c.IsActive, ct);

            if (credential is null)
            {
                // Degrade to whatever the config alone provides rather than throwing, so a
                // deleted credential surfaces as an upstream 401 in the health check
                // instead of taking the integration down with an exception. Same call as
                // GitService, but loud in the log.
                _logger.LogWarning(
                    "integration.auth.credential_missing integration={Integration} credential={Credential}",
                    integration.Name, credentialId);
            }
            else
            {
                // Config material wins over the credential's (see Merge). That is
                // deliberate, but it silently turns the link into a no-op, so say so
                // once per request rather than leaving the operator to infer it from
                // an upstream 401 that names the wrong problem.
                if (auth?.HasInlineMaterial() == true)
                    _logger.LogWarning(
                        "integration.auth.inline_overrides_credential integration={Integration} credential={Credential} " +
                        "— auth_config carries its own secret material, so the linked credential is not used; " +
                        "clear those fields from auth_config to fall back to the credential",
                        integration.Name, credentialId);

                auth = IntegrationCredentialAuth.Merge(auth, credential, _protector);
            }
        }

        if (auth is null) return;

        var method = auth.ResolvedMethod();
        // The method is safe to log; nothing else on this object is.
        _logger.LogDebug("integration.auth.apply integration={Integration} method={Method}",
            integration.Name, method);

        switch (method)
        {
            case IntegrationAuthConfig.MethodToken:
            {
                var token = await Resolve(auth.Token, ct);
                if (!string.IsNullOrEmpty(token))
                {
                    var prefix = string.IsNullOrWhiteSpace(auth.Prefix) ? "Token" : auth.Prefix!;
                    request.Headers.Authorization = new AuthenticationHeaderValue(prefix, token);
                }
                break;
            }

            case IntegrationAuthConfig.MethodBearer:
            {
                var token = await Resolve(auth.Token, ct);
                if (!string.IsNullOrEmpty(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            }

            case IntegrationAuthConfig.MethodBasic:
            {
                var user = await Resolve(auth.Username, ct);
                var pass = await Resolve(auth.Password, ct);
                if (!string.IsNullOrEmpty(user))
                {
                    var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
                }
                break;
            }

            case IntegrationAuthConfig.MethodApiKey:
            {
                var token = await Resolve(auth.Token, ct);
                if (!string.IsNullOrEmpty(token))
                {
                    var header = string.IsNullOrWhiteSpace(auth.Header) ? "X-API-Key" : auth.Header!;
                    request.Headers.TryAddWithoutValidation(header, token);
                }
                break;
            }

            case IntegrationAuthConfig.MethodOAuthClientCredentials:
            {
                var token = await _oauth.GetAccessTokenAsync(integration, auth, ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                break;
            }

            // MethodNone and anything unrecognised send no credentials. The upstream
            // then decides, and its 401 shows up plainly in the health check.
        }
    }

    private async Task<string?> Resolve(string? value, CancellationToken ct) =>
        string.IsNullOrEmpty(value) ? value : await _secrets.SubstituteAsync(value, ct);

    private static void ApplyStaticHeaders(HttpRequestMessage request, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(headersJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return; // headers are cosmetic; a broken blob must not fail the request
        }
        if (root.ValueKind != JsonValueKind.Object) return;

        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            var value = p.Value.GetString();
            if (string.IsNullOrEmpty(value)) continue;
            // Authorization belongs to auth_config alone; a static header must not be
            // able to quietly override the configured credentials.
            if (p.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(p.Name, value);
        }
    }
}
