using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Integration;
using nashira_backend.Services.Net;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// Calls one catalogued operation on an Integration.
//
// The difference from rest_call: that one addresses an operation in the *spec*
// index by operationId. This one addresses an IntegrationAction row, which is the
// curated catalog an admin can rename, recategorise and disable. A workflow
// referencing an action therefore keeps working when the underlying spec is
// re-imported, and stops working when an admin withdraws the action — which is the
// point of having a catalog at all.
//
// RequiresCompensation by default: the action may be a POST or a DELETE and the
// handler cannot tell. An action the sync marked read_only, or a snippet whose
// author declared it idempotent, is treated as a read.
public sealed class IntegrationActionSnippetHandler : ISnippetHandler
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(120);

    private readonly AppDbContext _db;
    private readonly IIntegrationAuthApplier _auth;
    private readonly ISecretResolver _secrets;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<IntegrationActionSnippetHandler> _logger;

    public IntegrationActionSnippetHandler(
        AppDbContext db, IIntegrationAuthApplier auth, ISecretResolver secrets, IUrlGuard urlGuard,
        IHttpClientFactory httpFactory, ILogger<IntegrationActionSnippetHandler> logger)
    {
        _db = db;
        _auth = auth;
        _secrets = secrets;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypeIntegrationAction;
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var integrationRef = Str(request.Input, "integration")?.Trim();
        var actionRef = Str(request.Input, "action") ?? Str(request.Input, "operation_id");
        if (string.IsNullOrWhiteSpace(integrationRef))
            return SnippetResult.Fail("integration_action needs an `integration` slug", "bad_input");
        if (string.IsNullOrWhiteSpace(actionRef))
            return SnippetResult.Fail("integration_action needs an `action` (name or operation id)", "bad_input");

        // workflow.v1 snippets/SPEC.md: `integration` is the integration SLUG, with
        // the display name accepted on resolution. The slug is what a bundle carries
        // because it is the stable identifier — a name is free text an admin renames
        // — so it is tried first; matching the name only afterwards means a rename
        // that happens to collide with another integration's slug cannot silently
        // redirect a workflow to the wrong system.
        var integration = await ResolveIntegrationAsync(integrationRef!, ct);
        if (integration is null)
            return SnippetResult.Fail($"no integration with the slug or name '{integrationRef}'", "not_found");
        if (!integration.Enabled)
            return SnippetResult.Fail($"integration '{integration.Name}' is disabled", "disabled");

        var action = await _db.IntegrationActions.AsNoTracking().FirstOrDefaultAsync(
            a => a.IntegrationId == integration.IntegrationId && a.IsActive
                 && (a.OperationId == actionRef || a.Name == actionRef), ct);
        if (action is null)
            return SnippetResult.Fail(
                $"'{integration.Name}' has no active action '{actionRef}' — sync its catalog first", "not_found");
        if (!action.Enabled)
            return SnippetResult.Fail($"action '{action.Name}' is disabled on '{integration.Name}'", "disabled");

        // Build the URL from the catalogued path.
        var baseUrl = (await _secrets.SubstituteAsync(integration.BaseUrl ?? string.Empty, ct)).TrimEnd('/');
        if (baseUrl.Length == 0)
            return SnippetResult.Fail($"integration '{integration.Name}' has no base_url", "not_configured");

        // `params` / `query` are the contract's keys (workflow.v1 snippets/SPEC.md);
        // `path_params` / `query_params` are Nashira's, kept as aliases. Canonical
        // wins when both are present.
        var pathParams = Obj(request.Input, "params") is { ValueKind: JsonValueKind.Object } p
            ? p : Obj(request.Input, "path_params");
        var queryParams = Obj(request.Input, "query") is { ValueKind: JsonValueKind.Object } q
            ? q : Obj(request.Input, "query_params");

        var (path, unresolved) = Substitute(action.Path, pathParams);
        if (unresolved.Count > 0)
            return SnippetResult.Fail(
                $"action '{action.Name}' needs params for {string.Join(", ", unresolved)}", "bad_input");

        var url = baseUrl + (path.StartsWith('/') ? path : "/" + path) + Query(queryParams);

        try
        {
            _urlGuard.EnsureSafe(url, allowPrivate: integration.AllowPrivateNetwork);
        }
        catch (InvalidOperationException ex)
        {
            return SnippetResult.Fail(ex.Message, "blocked");
        }

        using var message = new HttpRequestMessage(new HttpMethod(action.Method), url);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (request.Input.TryGetProperty("body", out var body)
            && body.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            // Resolve refs in the body too: a payload may legitimately carry a
            // ${secret:...} for a field the API expects.
            var text = await _secrets.SubstituteAsync(body.GetRawText(), ct);
            message.Content = new StringContent(text, Encoding.UTF8, "application/json");
        }

        try
        {
            await _auth.ApplyAsync(message, integration, ct);
        }
        catch (Exception ex)
        {
            // A broken grant is a credentials problem, and saying so beats letting
            // the upstream answer 401 to an anonymous request.
            return SnippetResult.Fail($"authentication failed: {ex.Message}", "auth_failed", retryable: true);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, (int)MaxTimeout.TotalSeconds)));

        var client = _httpFactory.CreateClient(
            integration.VerifySsl ? IntegrationHttpClients.Secure : IntegrationHttpClients.Insecure);

        try
        {
            using var response = await client.SendAsync(message, cts.Token);
            var raw = await response.Content.ReadAsStringAsync(cts.Token);
            var code = (int)response.StatusCode;
            var ok = code is >= 200 and < 400;

            _logger.LogInformation(
                "workflow.integration_action integration={Integration} action={Action} status={Status}",
                integration.Name, action.Name, code);

            return new SnippetResult
            {
                Success = ok,
                Output = JsonSerializer.SerializeToElement(new
                {
                    integration = integration.Name,
                    action = action.Name,
                    method = action.Method,
                    status_code = code,
                    success = ok,
                    body = TryParse(raw),
                    headers = ResponseHeaders(response),
                }),
                Error = ok ? string.Empty : $"HTTP {code}",
                ErrorCode = ok ? null : "http_" + code,
                Retryable = code >= 500 || code == 429,
                // A catalogued read never counts as a change, whatever the tier says — that
                // is what read_only is for. Beyond that the ACTION's verb decides, and it is
                // right here on the row: this used to fall through to null, which the executor
                // then resolved from the idempotency tier, so a catalogued GET that nobody had
                // marked read_only was recorded as having changed something.
                Change = ok && !action.ReadOnly && HttpVerbs.Mutating(action.Method)
                    ? StepChange.Changed
                    : StepChange.Unchanged,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SnippetResult.Fail($"timed out after {request.TimeoutSeconds}s", "timeout", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            return SnippetResult.Fail($"http error: {ex.Message}", "http_error", retryable: true);
        }
    }

    // Replaces {placeholders} in the catalogued path, reporting the ones with no
    // value rather than sending a literal "{id}" upstream.
    private static (string Path, List<string> Unresolved) Substitute(string path, JsonElement pathParams)
    {
        var unresolved = new List<string>();
        var sb = new StringBuilder();
        var i = 0;

        while (i < path.Length)
        {
            if (path[i] != '{') { sb.Append(path[i++]); continue; }

            var close = path.IndexOf('}', i);
            if (close < 0) { sb.Append(path[i..]); break; }

            var name = path[(i + 1)..close];
            if (pathParams.ValueKind == JsonValueKind.Object
                && pathParams.TryGetProperty(name, out var v))
            {
                sb.Append(Uri.EscapeDataString(
                    v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString()));
            }
            else
            {
                unresolved.Add($"'{name}'");
            }
            i = close + 1;
        }

        return (sb.ToString(), unresolved);
    }

    // Slug first, then exact name — both case-insensitively, because neither is
    // case-significant to an author writing `"integration": "NetBox"`.
    private async Task<Data.Models.Integration?> ResolveIntegrationAsync(string reference, CancellationToken ct)
    {
        var live = _db.Integrations.AsNoTracking().Where(i => i.IsActive);
        var lowered = reference.ToLowerInvariant();
        return await live.FirstOrDefaultAsync(i => i.Slug.ToLower() == lowered, ct)
               ?? await live.FirstOrDefaultAsync(i => i.Name.ToLower() == lowered, ct);
    }

    private static string Query(JsonElement queryParams)
    {
        if (queryParams.ValueKind != JsonValueKind.Object) return string.Empty;
        var parts = new List<string>();
        foreach (var p in queryParams.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
            parts.Add($"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(value ?? string.Empty)}");
        }
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    // The contract's portable output carries the response headers beside the body.
    private static Dictionary<string, string> ResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers) headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in response.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
        return headers;
    }

    private static JsonElement TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return JsonSerializer.SerializeToElement(new { });
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not JSON (an HTML error page, a plain-text body). Keep it addressable
            // instead of discarding the only diagnostic the upstream gave.
            return JsonSerializer.SerializeToElement(new { text = raw.Length > 8000 ? raw[..8000] : raw });
        }
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static JsonElement Obj(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : default;
}
