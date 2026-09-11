using System.Text.Json;
using nashira_backend.Services.Ai.RestExecutor;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Invokes one OpenAPI operation on the live API. The work lives in
// IRestOperationExecutor; this adapts the tool-call contract (JsonElement in/out).
public sealed class ExecuteOperationHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "operation_id":{"type":"string"},
          "path_params":{"type":"object","description":"REQUIRED for any operation whose path has {placeholders}. Keys must match the names between braces. TOP-LEVEL argument — never put these inside `body`.","additionalProperties":true,"default":{}},
          "query_params":{"type":"object","description":"Query string pairs (?key=value)","additionalProperties":true,"default":{}},
          "body":{"description":"JSON request body (object or array). Do NOT include path parameters here."}
        },"required":["operation_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly IRestOperationExecutor _executor;

    public ExecuteOperationHandler(IRestOperationExecutor executor) => _executor = executor;

    public string Name => "execute_operation";
    public string Description =>
        "Calls a REST operation on the live API. Always call operation_detail first so the " +
        "parameter shape is correct. Path template slots like {id} MUST be supplied via the " +
        "top-level `path_params` argument (not inside `body`). Use `query_params` for ?a=b " +
        "pairs and `body` for JSON bodies. Returns status, body, and headers.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetProperty("operation_id", out var opIdEl) || opIdEl.ValueKind != JsonValueKind.String)
            return JsonSerializer.SerializeToElement(new { error = "operation_id is required" });

        var operationId = opIdEl.GetString()!;
        var pathParams = GetOrEmptyObject(args, "path_params");
        var queryParams = GetOrEmptyObject(args, "query_params");
        var body = args.TryGetProperty("body", out var b) ? b : JsonDocument.Parse("null").RootElement;

        var result = await _executor.ExecuteAsync(operationId, pathParams, queryParams, body, ct);

        return JsonSerializer.SerializeToElement(new
        {
            status_code = result.StatusCode,
            success = result.Success,
            error = result.Error,
            // Present only when the refusal is explainable. Named `diagnosis` rather
            // than folded into `error` so it cannot be mistaken for what the upstream
            // itself said.
            diagnosis = result.Diagnosis,
            body = result.Body,
            headers = result.Headers,
        });
    }

    private static JsonElement GetOrEmptyObject(JsonElement args, string key)
        => args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : JsonDocument.Parse("{}").RootElement;
}
