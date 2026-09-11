using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Net;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.RestExecutor;

public sealed class RestExecutionResult
{
    public required int StatusCode { get; init; }
    public JsonElement Body { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// The HTTP verb the operation used. Carried so a caller can tell a read from a write
    /// without re-opening the spec — `rest_call` needs it to report whether the step changed
    /// anything, and the executor is the only party that already knows.
    /// </summary>
    public string? Method { get; init; }

    // A diagnosis attached to a refusal the caller would otherwise have to guess at.
    // Surfaced to the agent as well as the log: it is the agent that decides what to
    // try next, and "403" alone sent it reading the spec instead of the one field that
    // was wrong.
    public string? Diagnosis { get; init; }

    public bool Success => Error is null && StatusCode is >= 200 and < 400;
}

public interface IRestOperationExecutor
{
    /// <param name="source">
    /// The spec or integration slug the operation belongs to. Scopes the lookup, so an id
    /// defined by two catalogued specs cannot resolve to the wrong upstream — with that
    /// upstream's credentials — while the step reports a well-formed success. Null keeps the
    /// unscoped behaviour, so a caller that names no source runs exactly as it did.
    /// </param>
    Task<RestExecutionResult> ExecuteAsync(
        string operationId, JsonElement pathParams, JsonElement queryParams, JsonElement body,
        CancellationToken ct, string? source = null);
}

// Executes one operation against the live API. The target base URL + auth come
// from the AiApiSpec row (self-contained, admin-configured). ${secret:...} refs
// in the base URL, auth, and body are resolved right before the wire.
//
// One spec family has no host an admin could sensibly type: the built-in na_*
// documents describe *this* backend. Those rows are left with no base URL and fall
// back to Ai:SelfBaseUrl, which is also the only destination allowed to receive the
// caller's own session bearer.
public sealed class RestOperationExecutor : IRestOperationExecutor
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    internal const string SelfBaseUrlKey = "Ai:SelfBaseUrl";

    // The container's own Kestrel (ASPNETCORE_URLS is http://+:8080 in deploy/docker-compose.yml).
    // A default rather than an empty string is the whole fix: unset, the shipped specs were
    // discoverable but every execute_operation against them died on "has no base_url configured",
    // which reads as a broken catalog rather than as missing configuration.
    internal const string DefaultSelfBaseUrl = "http://localhost:8080";

    private readonly AppDbContext _db;
    private readonly IApiSpecIndex _index;
    private readonly ISecretResolver _secrets;
    private readonly IUrlGuard _urlGuard;
    private readonly ICurrentUser _user;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Services.Integration.IIntegrationAuthApplier _integrationAuth;
    private readonly string _selfBaseUrl;
    private readonly ILogger<RestOperationExecutor> _logger;

    public RestOperationExecutor(
        AppDbContext db, IApiSpecIndex index, ISecretResolver secrets, IUrlGuard urlGuard,
        ICurrentUser user, IHttpClientFactory httpFactory,
        Services.Integration.IIntegrationAuthApplier integrationAuth,
        IConfiguration config,
        ILogger<RestOperationExecutor> logger)
    {
        _selfBaseUrl = ResolveSelfBaseUrl(config);
        _db = db;
        _index = index;
        _secrets = secrets;
        _urlGuard = urlGuard;
        _user = user;
        _httpFactory = httpFactory;
        _integrationAuth = integrationAuth;
        _logger = logger;
    }

    public async Task<RestExecutionResult> ExecuteAsync(
        string operationId, JsonElement pathParams, JsonElement queryParams, JsonElement body,
        CancellationToken ct, string? source = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);

        await _index.EnsureLoadedAsync(cts.Token);
        var op = _index.GetByOperationId(operationId, source);
        if (op is null)
            // Naming both is the point: with a source given, "not found" means this source
            // does not define it, and falling back to another one would be the silent
            // mis-resolution the scope exists to prevent.
            return Fail(string.IsNullOrWhiteSpace(source)
                ? $"operation '{operationId}' not found"
                : $"operation '{operationId}' not found in source '{source}'");

        var spec = await _db.AiApiSpecs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Api == op.Api && s.IsActive, cts.Token);
        if (spec is null) return Fail($"spec '{op.Api}' not available");

        // A spec linked to an integration inherits that integration's base URL and
        // credentials, so NetBox is configured once instead of once per spec. The
        // spec's own values still win when it has them: a spec that was self-contained
        // before the link existed keeps behaving exactly as it did.
        Data.Models.Integration? integration = null;
        if (spec.IntegrationId is { } integrationId)
        {
            integration = await _db.Integrations.AsNoTracking()
                .FirstOrDefaultAsync(i => i.IntegrationId == integrationId && i.IsActive, cts.Token);
            if (integration is { Enabled: false })
                return Fail($"integration '{integration.Name}' is disabled");
        }

        var rawBaseUrl = string.IsNullOrWhiteSpace(spec.BaseUrl) ? integration?.BaseUrl : spec.BaseUrl;

        // A spec with nothing to inherit from is the built-in case: it describes this
        // process, so the host is a deployment fact rather than something to store on
        // the row. Resolving it here instead of stamping it at seed time also means a
        // change to Ai:SelfBaseUrl takes effect without re-seeding rows that already exist.
        var usedSelfBaseUrl = false;
        if (string.IsNullOrWhiteSpace(rawBaseUrl) && integration is null)
        {
            rawBaseUrl = _selfBaseUrl;
            usedSelfBaseUrl = true;
        }

        if (string.IsNullOrWhiteSpace(rawBaseUrl))
            return Fail($"neither spec '{op.Api}' nor integration '{integration!.Name}' has a base_url configured");

        var baseUrl = usedSelfBaseUrl
            ? rawBaseUrl
            : await _secrets.SubstituteAsync(rawBaseUrl, cts.Token);

        // Whether this request lands on Nashira itself. Two ways to get there: the
        // fallback above, or an admin who typed our own address into the spec — both
        // are legitimately self-calls, so the test is on the resolved origin.
        var isSelfCall = usedSelfBaseUrl || IsSameOrigin(baseUrl, _selfBaseUrl);
        if (isSelfCall) baseUrl = StripTrailingApiSegment(baseUrl);

        // Whichever row supplied the URL supplies its TLS policy too — pairing a
        // spec's verify_ssl with an integration's host would be arbitrary. The self
        // fallback is the spec's own row for this purpose; there is no integration to
        // ask, and dereferencing one would be a null reference.
        var fromIntegration = !usedSelfBaseUrl && string.IsNullOrWhiteSpace(spec.BaseUrl);
        var verifySsl = fromIntegration ? integration!.VerifySsl : spec.VerifySsl;

        var (path, query) = BuildPathAndQuery(op.Path, pathParams, queryParams);
        var unresolved = FindUnresolvedPathPlaceholders(path);
        if (unresolved.Count > 0)
            return Fail(
                $"operation '{operationId}' requires path_params for " +
                $"{string.Join(", ", unresolved.Select(p => "'" + p + "'"))}. Pass them via the `path_params` argument.");

        var fullUrl = CombineUrl(baseUrl, path, query);

        // Whichever row supplied the base URL supplies its SSRF policy, same
        // pairing rule as verify_ssl. The hardcoded `allowPrivate: true` this
        // replaces was a standing weakness: every spec could reach RFC-1918
        // regardless of configuration.
        var allowPrivate = fromIntegration ? integration!.AllowPrivateNetwork : spec.AllowPrivateNetwork;

        // The guard exists to stop a spec from being pointed at somewhere it should not
        // reach. Our own address is the one destination that is never a surprise: it is
        // this process, named by the operator's own configuration, and it is loopback in
        // every default deployment — which UrlGuard rejects outright, allowPrivate or not.
        // Skipping it here is narrower than relaxing the policy, which would have opened
        // loopback and RFC-1918 to every spec in the catalog.
        if (!isSelfCall)
        {
            try { _urlGuard.EnsureSafe(fullUrl, allowPrivate); }
            catch (InvalidOperationException ex) { return Fail(ex.Message); }
        }

        using var msg = new HttpRequestMessage(new HttpMethod(op.Method), fullUrl);
        if (body.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            var bodyText = await _secrets.SubstituteAsync(body.GetRawText(), isSelfCall, cts.Token);
            msg.Content = new StringContent(bodyText, Encoding.UTF8, "application/json");
        }
        // The spec's own auth wins when it declares one AND actually carries the
        // material for it. Declaring `auth_type: token` with an empty auth_config used
        // to win the contest and then send nothing: the request went out anonymous
        // while the linked integration sat there holding working credentials, and the
        // upstream answered "authentication credentials were not provided" — a message
        // that describes the request rather than the misconfiguration behind it.
        var specDeclaresAuth = !string.Equals(
            (spec.AuthType ?? "none").Trim(), "none", StringComparison.OrdinalIgnoreCase);
        var applied = specDeclaresAuth && await ApplyAuthAsync(msg, spec, isSelfCall, cts.Token);

        if (!applied && integration is not null)
        {
            if (specDeclaresAuth)
                _logger.LogWarning(
                    "rest.executor.spec_auth_empty api={Api} auth_type={AuthType} integration={Integration} " +
                    "— the spec declares an auth type but carries no material; falling back to the integration",
                    op.Api, spec.AuthType, integration.Name);
            await _integrationAuth.ApplyAsync(msg, integration, cts.Token);
        }
        else if (!applied && specDeclaresAuth)
        {
            // Nothing to fall back to. Still worth a line: the alternative is diagnosing
            // it from the upstream's 401 alone.
            _logger.LogWarning(
                "rest.executor.spec_auth_empty api={Api} auth_type={AuthType} — the spec declares an auth type " +
                "but carries no material and is not linked to an integration; the request goes out anonymous",
                op.Api, spec.AuthType);
        }

        var client = _httpFactory.CreateClient(verifySsl ? "rest_call" : "rest_call_insecure");

        var host = string.Empty;
        try { host = new Uri(fullUrl).Host; } catch { /* best effort */ }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation(
            "rest.executor.begin operation_id={OperationId} method={Method} host={Host} self_call={SelfCall}",
            operationId, op.Method, host, isSelfCall);

        try
        {
            using var resp = await client.SendAsync(msg, cts.Token);
            var raw = await resp.Content.ReadAsStringAsync(cts.Token);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
            foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);

            _logger.LogInformation(
                "rest.executor.end operation_id={OperationId} status={Status} elapsed_ms={Elapsed} bytes={Bytes}",
                operationId, (int)resp.StatusCode, sw.ElapsedMilliseconds, raw.Length);

            // A refusal that says "credentials were not provided" AFTER we attached an
            // Authorization header is not a missing credential — it is a scheme the
            // upstream does not recognise, which it cannot distinguish from anonymous.
            string? diagnosis = null;
            if (Services.Integration.IntegrationTypeProfile.LooksLikeUnrecognisedScheme(
                    (int)resp.StatusCode, msg.Headers.Authorization is not null, raw))
            {
                diagnosis =
                    $"The request carried an Authorization header using the '{msg.Headers.Authorization!.Scheme}' "
                    + "scheme and the upstream answered as though no credential was sent. That is what an "
                    + "unrecognised scheme looks like, not a bad secret — check the integration's auth method "
                    + "rather than the token. NetBox and other Django REST Framework APIs require 'Token'.";
                _logger.LogWarning(
                    "rest.executor.auth_scheme_suspect operation_id={OperationId} host={Host} status={Status} scheme={Scheme}",
                    operationId, host, (int)resp.StatusCode, msg.Headers.Authorization!.Scheme);
            }

            return new RestExecutionResult
            {
                Method = op.Method,
                StatusCode = (int)resp.StatusCode,
                Body = TryParseJson(raw),
                Headers = headers,
                Diagnosis = diagnosis,
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Fail($"request timed out after {RequestTimeout.TotalSeconds:n0}s");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "rest.executor.http_error operation_id={OperationId} host={Host}", operationId, host);
            return Fail($"http error: {ex.Message}");
        }
    }

    // True when a credential was actually attached to the request. The caller needs the
    // distinction: "the spec authenticates this call" and "the spec means to, but has
    // nothing to send" are the same code path here and completely different faults.
    private async Task<bool> ApplyAuthAsync(
        HttpRequestMessage msg, AiApiSpec spec, bool isSelfCall, CancellationToken ct)
    {
        var type = (spec.AuthType ?? "none").Trim().ToLowerInvariant();
        if (type == "none") return false;

        var cfg = ParseAuthConfig(spec.AuthConfig);
        async Task<string?> Resolve(string key) =>
            cfg.TryGetValue(key, out var v) ? await _secrets.SubstituteAsync(v, isSelfCall, ct) : null;

        switch (type)
        {
            case "bearer":
                if (await Resolve("value") is { Length: > 0 } bearer)
                {
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    return true;
                }
                break;
            case "token":
                if (await Resolve("value") is { Length: > 0 } token)
                {
                    msg.Headers.TryAddWithoutValidation(
                        cfg.GetValueOrDefault("header", "Authorization"),
                        cfg.GetValueOrDefault("prefix", "Token ") + token);
                    return true;
                }
                break;
            case "basic":
                var user = await Resolve("username");
                var pass = await Resolve("password");
                if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass))
                {
                    msg.Headers.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
                    return true;
                }
                break;
            case "header":
                if (await Resolve("value") is { Length: > 0 } headerVal)
                {
                    msg.Headers.TryAddWithoutValidation(cfg.GetValueOrDefault("header", "X-API-Key"), headerVal);
                    return true;
                }
                break;
        }
        return false;
    }

    private static Dictionary<string, string> ParseAuthConfig(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String)
                        result[p.Name] = p.Value.GetString() ?? string.Empty;
        }
        catch { /* malformed auth_config -> anonymous */ }
        return result;
    }

    private static (string path, List<KeyValuePair<string, string>> query) BuildPathAndQuery(
        string template, JsonElement pathParams, JsonElement queryParams)
    {
        var path = template;
        if (pathParams.ValueKind == JsonValueKind.Object)
            foreach (var prop in pathParams.EnumerateObject())
            {
                var encoded = Uri.EscapeDataString(prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText());
                path = path.Replace("{" + prop.Name + "}", encoded, StringComparison.Ordinal);
            }

        var query = new List<KeyValuePair<string, string>>();
        if (queryParams.ValueKind == JsonValueKind.Object)
            foreach (var prop in queryParams.EnumerateObject())
            {
                if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? string.Empty
                    : prop.Value.GetRawText();
                query.Add(new KeyValuePair<string, string>(prop.Name, value));
            }
        return (path, query);
    }

    private static List<string> FindUnresolvedPathPlaceholders(string path)
    {
        var names = new List<string>();
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] != '{') continue;
            var end = path.IndexOf('}', i + 1);
            if (end < 0) break;
            var inner = path.Substring(i + 1, end - i - 1);
            if (inner.Length > 0 && inner.All(c => char.IsLetterOrDigit(c) || c is '_' or '-'))
                names.Add(inner);
            i = end;
        }
        return names;
    }

    // Reads Ai:SelfBaseUrl once. Empty means "wherever this container answers itself",
    // which is the default and the right answer for every stock deployment; the setting
    // exists for installs where the backend is only reachable through a proxy.
    internal static string ResolveSelfBaseUrl(IConfiguration config)
    {
        var configured = config[SelfBaseUrlKey]?.Trim();
        var value = StripTrailingApiSegment(
            string.IsNullOrWhiteSpace(configured) ? DefaultSelfBaseUrl : configured);
        return value.Length > 0 ? value : DefaultSelfBaseUrl;
    }

    // The shipped na_* documents carry absolute paths that already begin with /api —
    // unlike flow-weaver's, which lean on a relative `servers: - url: /api`. Anyone
    // reading "base URL of the API" naturally writes https://host/api, and the result
    // would be https://host/api/api/workflows and a 404 that names nothing useful.
    internal static string StripTrailingApiSegment(string url)
    {
        var value = url.TrimEnd('/');
        return value.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? value[..^4].TrimEnd('/')
            : value;
    }

    // Scheme + host + port. Path is deliberately not compared: a proxy-prefixed self URL
    // and a bare one are the same destination for the purpose of "may this request carry
    // the caller's own token".
    internal static bool IsSameOrigin(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua)
        && Uri.TryCreate(b, UriKind.Absolute, out var ub)
        && string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
        && ua.Port == ub.Port;

    private static string CombineUrl(string baseUrl, string path, List<KeyValuePair<string, string>> query)
    {
        var joined = baseUrl.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path);
        if (query.Count == 0) return joined;
        var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return joined.Contains('?', StringComparison.Ordinal) ? joined + "&" + qs : joined + "?" + qs;
    }

    private static JsonElement TryParseJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return JsonDocument.Parse("null").RootElement.Clone();
        try { return JsonDocument.Parse(raw).RootElement.Clone(); }
        catch { return JsonSerializer.SerializeToElement(new { raw }); }
    }

    private static RestExecutionResult Fail(string error) => new()
    {
        StatusCode = 0,
        Body = JsonDocument.Parse("null").RootElement.Clone(),
        Error = error,
    };
}
