using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DevLab.JmesPath;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.RestExecutor;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Mcp;
using nashira_backend.Services.Net;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Services.Worker.Handlers;

// ─── ping ────────────────────────────────────────────────────────────────

// TCP reachability probe. Reads nothing and changes nothing → idempotent.
//
// The output carries FlowWeaver's field set — the oracle for this type — because
// the shape workflow.v1 originally specified for `ping`
// (`{reachable, latency_ms, method, host}`) was invented in the document and was
// never what FW emitted. FW's `PingHandler` emits `success`, `rtt_avg_ms`,
// `packets_sent`, `packets_received` and `raw_output`, so those are what a portable
// template may address here. It also computes `packet_loss` but only logs it — it
// is emitted here, because it is derivable from the pair and a reader looking for
// the oracle's vocabulary expects it. Nashira's original
// `host`/`port`/`reachable`/`latency_ms`/`method` ride along as extensions, which
// snippets/SPEC.md rule 5 allows and which anything already reading them needs.
//
// The probe method stays reported in `method` — always "tcp" here — so a
// downstream template can tell an ICMP echo from a TCP connect; see the mapping
// note on the output below for why a TCP prober reports exactly one packet.
// `count` is an echo count, which a TCP prober has no use for — it is accepted and
// ignored; `timeout_ms` is honoured.
public sealed class PingSnippetHandler : ISnippetHandler
{
    private const int DefaultPort = 22;

    private readonly AppDbContext _db;
    private readonly ILogger<PingSnippetHandler> _logger;

    public PingSnippetHandler(AppDbContext db, ILogger<PingSnippetHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string Type => Data.Models.Snippet.TypePing;
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var host = Str(request.Input, "host") ?? Str(request.Input, "device");
        if (string.IsNullOrWhiteSpace(host))
            return SnippetResult.Fail("ping needs a `host` (IP) or `device` (inventory name)", "bad_input");

        var port = Int(request.Input, "port", DefaultPort);
        var target = host!.Trim();

        // A name resolves through inventory; a literal IP is used as-is, so a
        // workflow can probe something not yet registered.
        if (!IPAddress.TryParse(target, out _))
        {
            var device = await _db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.IsActive && d.DeviceName == target, ct);
            if (device is null)
                return SnippetResult.Fail($"device '{target}' is not in inventory and is not an IP", "not_found");
            target = device.IpAddress;
        }

        // `timeout_ms` is per probe (the contract's key); the snippet timeout is the
        // ceiling when the payload says nothing.
        var timeout = Int(request.Input, "timeout_ms", 0) is > 0 and var ms
            ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, 60_000))
            : TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 60));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var sw = Stopwatch.StartNew();
        bool reachable;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(target, port, cts.Token);
            reachable = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "workflow.ping.unreachable host={Host} port={Port}", target, port);
            reachable = false;
        }
        sw.Stop();

        // One line, the same text the step logs — FW's `raw_output` is its per-echo
        // transcript, and this is the whole transcript a single connect produces.
        var raw = reachable
            ? $"{target}:{port} answered in {sw.ElapsedMilliseconds}ms"
            : $"{target}:{port} did not answer within {(int)timeout.TotalMilliseconds}ms";

        // Unreachable is a *result*, not a step failure: the whole point of a ping
        // node is to branch on it, and a failed step would skip the branch instead.
        // FlowWeaver fails the step instead, which is why `success` below mirrors
        // reachability rather than the step's own outcome — a template reading
        // `success` gets the oracle's meaning either way.
        return SnippetResult.Ok(new
        {
            // ── FlowWeaver's field set (the oracle) ─────────────────────────
            // FW sends `count` ICMP echoes and reports how many came back. Nashira
            // probes with one TCP connect instead — deliberately, because ICMP needs
            // raw-socket privileges a worker should not hold — so there is exactly one
            // attempt to report: one connection attempt is one "packet", a completed
            // handshake is its reply, and `rtt_avg_ms` is the connect time (an average
            // of a single sample). Someone comparing a Nashira run to a FW run should
            // read these as the same fields measuring different things: an echo times
            // a round trip to the host, a connect times a three-way handshake to one
            // port, and only one of them was attempted here. `method` below is what
            // says which probe produced the numbers.
            success = reachable,
            rtt_avg_ms = reachable ? Math.Round((double)sw.ElapsedMilliseconds, 2) : 0d,
            packets_sent = 1,
            packets_received = reachable ? 1 : 0,
            // Derived from the pair above, as FW derives it — 0 or 1 here and never a
            // fraction, because a single attempt cannot lose part of itself.
            packet_loss = reachable ? 0d : 1d,
            raw_output = raw,

            // ── Nashira's own fields, kept as extensions (SPEC.md rule 5) ────────
            host = target,
            port,
            reachable,
            // Null when nothing answered: the elapsed time of a timeout is not a
            // latency, and a template averaging latencies must not count it.
            // `rtt_avg_ms` reports 0 in that case instead, because 0 is what the
            // oracle reports when no reply arrives.
            latency_ms = reachable ? (int?)sw.ElapsedMilliseconds : null,
            method = "tcp",
        }, StepChange.Unchanged, logs: raw);   // a connect probe reads; it never mutates
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int Int(JsonElement e, string k, int def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : def;
}

// Whether an HTTP verb can have changed anything on the other side. GET, HEAD and OPTIONS are
// defined as safe; everything else is assumed to mutate, which is the right way round — a verb
// nobody recognises should not be read as harmless.
internal static class HttpVerbs
{
    public static bool Mutating(string? method) =>
        !string.IsNullOrWhiteSpace(method)
        && !method.Trim().Equals("GET", StringComparison.OrdinalIgnoreCase)
        && !method.Trim().Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        && !method.Trim().Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);
}

// ─── transform ───────────────────────────────────────────────────────────

// Reshapes a JSON value with a JMESPath expression (workflow.v1 snippets/SPEC.md,
// decided 2026-08-27, D1). Pure — no I/O, so it cannot fail in a way worth
// retrying.
//
// Two ways to say what to compute, both accepted:
//   - `expression` (canonical): a JMESPath expression, in the payload or on the
//     snippet's `code`. `language`, when present, must be `jmespath`.
//   - `mapping` (Nashira's original, now an alias): `{ "<out>": "<path>" }`,
//     equivalent to the JMESPath multiselect hash `{ out: path }`. Evaluated
//     through the resolver's path grammar rather than translated, so a key with a
//     hyphen or a dot keeps working without quoting rules an author never wrote.
//
// The document is `input` when given, else the whole payload minus the keys that
// describe the transform itself (`source` is Nashira's older name for `input`).
public sealed class TransformSnippetHandler : ISnippetHandler
{
    private static readonly HashSet<string> ControlKeys =
        new(StringComparer.Ordinal) { "expression", "language", "mapping", "input", "source" };

    public string Type => Data.Models.Snippet.TypeTransform;
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.Idempotent;

    public Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        if (request.Input.ValueKind != JsonValueKind.Object)
            return Task.FromResult(SnippetResult.Fail("transform needs an object input", "bad_input"));

        var language = Str(request.Input, "language") ?? request.ScriptLanguage;
        if (!string.IsNullOrWhiteSpace(language)
            && !string.Equals(language.Trim(), "jmespath", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(SnippetResult.Fail(
                $"unsupported transform language '{language}' (only jmespath is supported)", "bad_input"));

        var document = Document(request.Input);

        // `expression` is the CANONICAL key and `mapping` is the alias
        // (snippets/SPEC.md, `transform`), so the canonical one wins when a node
        // carries both — which is exactly what an importer that translated a Nashira
        // node while preserving the original produces. This read used to be the other
        // way round, so such a node silently ran the alias and ignored the JMESPath
        // expression the author had written.
        //
        // Order after that: inline expression, then the snippet's code — which may be
        // either a JSON object mapping (Nashira's reusable snippets) or a JMESPath
        // expression.
        var expression = Str(request.Input, "expression");
        if (string.IsNullOrWhiteSpace(expression)
            && request.Input.TryGetProperty("mapping", out var inline)
            && inline.ValueKind == JsonValueKind.Object)
            return Task.FromResult(ApplyMapping(inline, document));

        if (string.IsNullOrWhiteSpace(expression) && !string.IsNullOrWhiteSpace(request.Code))
        {
            var code = request.Code!.Trim();
            if (code.StartsWith('{') && TryParseObject(code, out var codeMapping))
                return Task.FromResult(ApplyMapping(codeMapping, document));
            expression = code;
        }

        if (string.IsNullOrWhiteSpace(expression))
        {
            // Nothing to compute: pass the document through. Useful as a rename-free
            // fan-in point, and better than erroring on a legitimate no-op.
            return Task.FromResult(new SnippetResult
            {
                // Pure: JMESPath over a document, no I/O anywhere in this handler.
                Success = true, Output = document.Clone(), Change = StepChange.Unchanged,
            });
        }

        try
        {
            var result = new JmesPath().Transform(document.GetRawText(), expression);
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(result) ? "null" : result);
            return Task.FromResult(new SnippetResult
            {
                Success = true,
                Output = doc.RootElement.Clone(),
                Change = StepChange.Unchanged,
                Logs = $"jmespath: {expression}",
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(SnippetResult.Fail($"jmespath transform failed: {ex.Message}", "bad_expression"));
        }
    }

    // The value to transform. `input` is the contract's key; `source` is what
    // Nashira's transform read before; otherwise the payload itself, minus the keys
    // that configure the transform — an author who writes `{ "expression": "a" }`
    // does not mean to select from an object containing `expression`.
    private static JsonElement Document(JsonElement input)
    {
        if (input.TryGetProperty("input", out var explicitDoc)) return explicitDoc;
        if (input.TryGetProperty("source", out var source)) return source;

        var rest = new Dictionary<string, JsonElement>();
        foreach (var p in input.EnumerateObject())
            if (!ControlKeys.Contains(p.Name)) rest[p.Name] = p.Value;
        return JsonSerializer.SerializeToElement(rest);
    }

    private static SnippetResult ApplyMapping(JsonElement mapping, JsonElement source)
    {
        var result = new Dictionary<string, JsonElement>();
        var missing = new List<string>();
        foreach (var entry in mapping.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                // A non-string mapping value is a literal to emit as-is.
                result[entry.Name] = entry.Value.Clone();
                continue;
            }
            var path = entry.Value.GetString() ?? string.Empty;
            var value = VariableResolver.TryResolvePath(source, path.StartsWith('.') || path.StartsWith('[') ? path : "." + path);
            if (value is null) missing.Add($"{entry.Name} <- {path}");
            else result[entry.Name] = value.Value.Clone();
        }

        // A mapping that resolved nothing is an authoring error, not an empty
        // result — reporting success with `{}` would push the mistake downstream.
        if (result.Count == 0 && missing.Count > 0)
            return SnippetResult.Fail(
                $"none of the mapped paths exist in the source: {string.Join(", ", missing)}", "unresolved");

        return new SnippetResult
        {
            Success = true,
            Output = JsonSerializer.SerializeToElement(result),
            Error = missing.Count > 0 ? $"unresolved: {string.Join(", ", missing)}" : string.Empty,
            Change = StepChange.Unchanged,
        };
    }

    private static bool TryParseObject(string json, out JsonElement element)
    {
        element = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

// ─── rest_call ───────────────────────────────────────────────────────────

// One type, two forms (workflow.v1 snippets/SPEC.md), told apart by which key is
// present:
//
//   Catalogued (Nashira): `operation_id` names an operation in the spec index; the
//   REST executor resolves the spec, its base URL, the linked integration's
//   credentials and ${secret:…} references.
//
//   Raw (Flow Weaver): `url`, `method`, `headers`, `body`, `query`. Nothing is
//   catalogued, so this handler does what the executor does for the other form:
//   resolves ${secret:…} in the URL, headers and body right before the wire and
//   runs the URL through the same outbound guard every integration call passes —
//   a portable workflow must not be a way around the SSRF policy.
//
// RequiresCompensation by default because the operation may be a POST/DELETE and
// the handler cannot know. A snippet wrapping a verified read declares itself
// idempotent — that is exactly what Snippet.Idempotency is for.
public sealed class RestCallSnippetHandler : ISnippetHandler
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(120);
    private const int MaxBodyChars = 8000;

    private readonly IRestOperationExecutor _executor;
    private readonly IUrlGuard _urlGuard;
    private readonly ISecretResolver _secrets;
    private readonly IHttpClientFactory _httpFactory;

    public RestCallSnippetHandler(
        IRestOperationExecutor executor, IUrlGuard urlGuard, ISecretResolver secrets, IHttpClientFactory httpFactory)
    {
        _executor = executor;
        _urlGuard = urlGuard;
        _secrets = secrets;
        _httpFactory = httpFactory;
    }

    public string Type => Data.Models.Snippet.TypeRestCall;
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var url = Str(request.Input, "url");
        var operationId = Str(request.Input, "operation_id");

        if (!string.IsNullOrWhiteSpace(url))
            return await RawAsync(request, url!.Trim(), ct);
        if (!string.IsNullOrWhiteSpace(operationId))
            return await CataloguedAsync(request, operationId!, ct);

        return SnippetResult.Fail(
            "rest_call needs either a `url` (raw request) or an `operation_id` (catalogued operation)", "bad_input");
    }

    private async Task<SnippetResult> CataloguedAsync(SnippetRequest request, string operationId, CancellationToken ct)
    {
        var pathParams = Obj(request.Input, "path_params");
        var queryParams = Obj(request.Input, "query_params");
        var body = request.Input.TryGetProperty("body", out var b) ? b : default;

        // `source` names the spec the operation belongs to (snippets/SPEC.md `rest_call`,
        // catalogued form). It SCOPES the lookup rather than merely annotating it: two
        // catalogued specs sharing an id otherwise resolve to whichever one the index kept,
        // with that upstream's credentials, and the step succeeds either way.
        var result = await _executor.ExecuteAsync(
            operationId, pathParams, queryParams, body, ct, source: Str(request.Input, "source"));

        if (result.Error is { Length: > 0 })
            return SnippetResult.Fail(result.Error, "rest_error", retryable: true);

        var ok = result.Success;
        return new SnippetResult
        {
            Success = ok,
            // The verb decides, and the executor now carries it: a read cannot have changed
            // anything, and a write that did not succeed did not either. Before the verb was
            // exposed this had to defer to the author, which for a catalogued operation is a
            // question the catalogue already answers.
            Change = ok && HttpVerbs.Mutating(result.Method) ? StepChange.Changed : StepChange.Unchanged,
            Output = JsonSerializer.SerializeToElement(new
            {
                status_code = result.StatusCode,
                success = ok,
                body = result.Body,
                headers = result.Headers ?? new Dictionary<string, string>(),
            }),
            Error = ok ? string.Empty : $"HTTP {result.StatusCode}",
            ErrorCode = ok ? null : "http_" + result.StatusCode,
            // 5xx is worth retrying; a 4xx means the request itself is wrong.
            Retryable = result.StatusCode >= 500,
        };
    }

    private async Task<SnippetResult> RawAsync(SnippetRequest request, string rawUrl, CancellationToken ct)
    {
        var method = (Str(request.Input, "method") ?? "GET").Trim().ToUpperInvariant();

        // Secrets resolve last, right before the wire, and never earlier: the
        // resolved URL must not end up in the stored input snapshot.
        var url = await _secrets.SubstituteAsync(rawUrl, ct);
        var query = Query(Obj(request.Input, "query"));
        if (query.Length > 0)
            url += (url.Contains('?') ? "&" : "?") + query;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return SnippetResult.Fail($"`url` must be an absolute http(s) URL", "bad_input");

        try
        {
            _urlGuard.EnsureSafe(url, allowPrivate: Bool(request.Input, "allow_private_network", false));
        }
        catch (InvalidOperationException ex)
        {
            return SnippetResult.Fail(ex.Message, "blocked");
        }

        using var message = new HttpRequestMessage(new HttpMethod(method), uri);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        string? contentType = null;
        var headers = Obj(request.Input, "headers");
        if (headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var h in headers.EnumerateObject())
            {
                var value = h.Value.ValueKind == JsonValueKind.String ? h.Value.GetString() ?? string.Empty : h.Value.ToString();
                value = await _secrets.SubstituteAsync(value, ct);
                if (string.Equals(h.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = value;
                    continue;
                }
                message.Headers.TryAddWithoutValidation(h.Name, value);
            }
        }

        if (request.Input.TryGetProperty("body", out var body)
            && body.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            var text = body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty : body.GetRawText();
            text = await _secrets.SubstituteAsync(text, ct);
            var mediaType = contentType ?? (body.ValueKind == JsonValueKind.String ? "text/plain" : "application/json");
            message.Content = new StringContent(text, Encoding.UTF8, mediaType);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, (int)MaxTimeout.TotalSeconds)));

        var client = _httpFactory.CreateClient(Bool(request.Input, "verify_ssl", true) ? "rest_call" : "rest_call_insecure");

        try
        {
            using var response = await client.SendAsync(message, cts.Token);
            var raw = await response.Content.ReadAsStringAsync(cts.Token);
            var code = (int)response.StatusCode;
            var ok = code is >= 200 and < 400;

            var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers) responseHeaders[h.Key] = string.Join(", ", h.Value);
            foreach (var h in response.Content.Headers) responseHeaders[h.Key] = string.Join(", ", h.Value);

            return new SnippetResult
            {
                Success = ok,
                // The author wrote the verb into this node, so the handler can read it.
                Change = ok && HttpVerbs.Mutating(method) ? StepChange.Changed : StepChange.Unchanged,
                Output = JsonSerializer.SerializeToElement(new
                {
                    status_code = code,
                    success = ok,
                    body = TryParse(raw),
                    headers = responseHeaders,
                }),
                Error = ok ? string.Empty : $"HTTP {code}",
                ErrorCode = ok ? null : "http_" + code,
                Retryable = code >= 500 || code == 429,
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
        return string.Join("&", parts);
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
            return JsonSerializer.SerializeToElement(new { text = raw.Length > MaxBodyChars ? raw[..MaxBodyChars] : raw });
        }
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool Bool(JsonElement e, string k, bool def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    private static JsonElement Obj(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Object
            ? v : default;
}

// ─── mcp_call ────────────────────────────────────────────────────────────

// Invokes a tool on a registered MCP server. NonReversible by default: the server
// is an arbitrary external program and nothing in the protocol says whether a tool
// reads or writes, so the executor must not assume a rollback exists.
//
// `tool` is the contract's key; `tool_name` (Flow Weaver) is accepted as its alias.
public sealed class McpCallSnippetHandler : ISnippetHandler
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly IMcpServerService _mcp;

    public McpCallSnippetHandler(AppDbContext db, IMcpServerService mcp)
    {
        _db = db;
        _mcp = mcp;
    }

    public string Type => Data.Models.Snippet.TypeMcpCall;
    public IdempotencyKind DefaultIdempotency => IdempotencyKind.RequiresCompensation;
    // The contract's default. MCP says nothing about whether a tool writes, so NonReversible
    // was the strictest available guess — and because that tier is absolute, it silently
    // removed the author's ability to declare the tier the contract grants. A tool that
    // genuinely cannot be undone is still declarable on the snippet.

    public async Task<SnippetResult> ExecuteAsync(SnippetRequest request, CancellationToken ct)
    {
        var serverName = Str(request.Input, "server");
        var toolName = Str(request.Input, "tool") ?? Str(request.Input, "tool_name");
        if (string.IsNullOrWhiteSpace(serverName)) return SnippetResult.Fail("mcp_call needs a `server`", "bad_input");
        if (string.IsNullOrWhiteSpace(toolName)) return SnippetResult.Fail("mcp_call needs a `tool`", "bad_input");

        var server = await _db.McpServers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsActive && s.Name == serverName, ct);
        if (server is null)
            return SnippetResult.Fail($"no MCP server named '{serverName}' is registered", "not_found");

        var arguments = request.Input.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a : EmptyArgs;

        try
        {
            var result = await _mcp.CallAsync(server.McpServerId, toolName!.Trim(), arguments, ct);
            return new SnippetResult
            {
                Success = !result.IsError,
                // MCP says nothing about whether a tool mutates — a name and a schema, and no
                // verb. The registered server knows, and modelling it on the synced tool
                // catalogue is a change of its own; until then the node that picked the tool
                // is the party that can say.
                Change = StepChange.AuthorDecides,
                Output = JsonSerializer.SerializeToElement(new
                {
                    server = server.Name,
                    tool = toolName,
                    // The contract's portable trio beside Nashira's own fields.
                    ok = !result.IsError,
                    result = result.Structured is { } structured ? structured : (object?)result.Content,
                    content = result.Content,
                    structured = result.Structured,
                    is_error = result.IsError,
                    error = result.IsError ? result.Content : null,
                }),
                Error = result.IsError ? result.Content : string.Empty,
                ErrorCode = result.IsError ? "tool_error" : null,
            };
        }
        catch (Exceptions.DomainException ex)
        {
            return SnippetResult.Fail(ex.Message, "catalog_error");
        }
        catch (Exception ex)
        {
            return SnippetResult.Fail($"MCP call failed: {ex.Message}", "mcp_error", retryable: true);
        }
    }

    private static string? Str(JsonElement e, string k) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
