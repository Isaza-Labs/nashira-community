using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using nashira_backend.Data.DTos.Ai;

namespace nashira_backend.Services.Ai.Providers;

// Direct HTTP implementation against Ollama's native chat API
// ({baseUrl}/api/chat) — the endpoint a local Ollama actually serves, rather than
// its OpenAI-compatibility shim, so tool calls and token counts come from the
// source instead of through a translation layer.
//
// Two differences from the other providers shape this file. The stream is NDJSON,
// one complete JSON object per line, not SSE: there is no `data: ` prefix, no
// [DONE] sentinel, and a tool call arrives whole rather than as argument
// fragments. And Ollama does not issue tool call ids — it matches a result to a
// call by tool name — so ids are synthesised here and resolved back to names when
// the conversation is replayed.
public sealed class OllamaProvider : IStreamingToolCallingLlmProvider
{
    public string ProviderType => "ollama";

    // Dumps every incoming NDJSON line at Debug when AI_LOG_PAYLOADS=true. Off by
    // default so the steady-state log stream stays manageable.
    private static readonly bool LogPayloads =
        string.Equals(Environment.GetEnvironmentVariable("AI_LOG_PAYLOADS"),
            "true", StringComparison.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ILogger<OllamaProvider>? _logger;
    private readonly string _chatUrl;
    private readonly ModelLimits? _limits;

    // Ollama does not reject a prompt that does not fit its context: it drops the
    // front of it, which is exactly where the system prompt is, and answers as if
    // nothing happened. An agent that has silently lost its instructions is the
    // hardest failure here to attribute to a provider setting.
    //
    // The default the daemon applies (the model's Modelfile, commonly a few thousand
    // tokens) is far below what a turn here needs: the system prompt alone runs to
    // tens of thousands of tokens, plus AiChatOptions' 60k characters of history and
    // up to 100k characters per tool result. 32k fits the prompt with room for
    // several tool rounds and still loads on a 12-16 GB GPU for a quantised 7-8B
    // model; a deployment with less VRAM, or a model whose real context is shorter,
    // overrides it per provider with Config.model_limits (see ModelLimits), which is
    // the same key flow-weaver uses for the same knob.
    public const int DefaultNumCtx = 32_768;

    public OllamaProvider(
        HttpClient http, string apiKey, string? baseUrl = null, ILogger<OllamaProvider>? logger = null,
        ModelLimits? limits = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "http://localhost:11434").TrimEnd('/');
        _logger = logger;
        _limits = limits;
        // A base URL pasted as "http://host/api" would otherwise become /api/api/chat.
        _chatUrl = LlmHttp.CombineUrl(_baseUrl, "/api/chat");
    }

    public async Task<ChatResult> ChatAsync(
        List<LlmMessage> messages, string model, double temperature, CancellationToken ct)
        => await ChatWithToolsAsync(messages, new(), model, temperature, ct);

    public async Task<ChatResult> ChatWithToolsAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, CancellationToken ct)
    {
        var body = BuildBody(messages, tools, model, temperature, stream: false);
        using var response = await PostAsync(body, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return ParseResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<ChatStreamEvent> ChatWithToolsStreamAsync(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(messages, tools, model, temperature, stream: true);
        using var response = await PostAsync(body, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var toolCallSeq = 0;

        _logger?.LogDebug(
            "ollama.stream.connected model={Model} messages={MessageCount} tools={ToolCount} num_ctx={NumCtx}",
            model, messages.Count, tools.Count, _limits?.ContextWindowTokens ?? DefaultNumCtx);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break; // end of stream
            if (line.Length == 0) continue;

            if (LogPayloads)
                _logger?.LogDebug("ollama.stream.raw_chunk {Data}", line.Length > 500 ? line[..500] + "…" : line);

            JsonElement chunk;
            try { chunk = JsonDocument.Parse(line).RootElement; }
            catch { continue; }

            // Ollama reports a failure inside a 200 body (a model that is not pulled,
            // a context that will not fit). Throwing ends the turn with the daemon's
            // own words instead of with an empty answer.
            if (chunk.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException($"ollama error: {errEl.GetString()}");

            if (chunk.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                if (msg.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    var text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                        yield return new ChatStreamEvent { Type = "text_delta", TextDelta = text };
                }

                // Whole calls, not fragments: nothing to buffer and nothing that can be
                // cut mid-arguments, so each one is reported as it lands.
                if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        if (ReadToolCall(tc, ++toolCallSeq) is not { } call) continue;
                        yield return new ChatStreamEvent { Type = "tool_call", ToolCall = call };
                    }
                }
            }

            if (chunk.TryGetProperty("done", out var done)
                && done.ValueKind == JsonValueKind.True)
            {
                yield return new ChatStreamEvent
                {
                    Type = "done",
                    InputTokens = ReadCount(chunk, "prompt_eval_count"),
                    OutputTokens = ReadCount(chunk, "eval_count"),
                    // Ollama's own vocabulary: "stop", "length" (the context or the
                    // prediction limit ran out — the runner reads that as a cut-off),
                    // or "load" when the response was only a model load.
                    StopReason = chunk.TryGetProperty("done_reason", out var dr)
                        && dr.ValueKind == JsonValueKind.String ? dr.GetString() : null,
                };
                break;
            }
        }
    }

    private static int ReadCount(JsonElement chunk, string name)
        => chunk.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : 0;

    // A tool call as Ollama writes it: no id, and `arguments` is a JSON object
    // rather than the encoded string OpenAI sends (some builds still send the
    // string, so both are accepted).
    private static ToolCallResult? ReadToolCall(JsonElement tc, int seq)
    {
        if (tc.ValueKind != JsonValueKind.Object) return null;
        if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) return null;
        var name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (string.IsNullOrEmpty(name)) return null;

        JsonElement args;
        if (!fn.TryGetProperty("arguments", out var argsEl))
            args = JsonDocument.Parse("{}").RootElement;
        else if (argsEl.ValueKind == JsonValueKind.String)
        {
            try { args = JsonDocument.Parse(argsEl.GetString() ?? "{}").RootElement; }
            catch (JsonException) { return null; }
        }
        else args = argsEl.Clone();

        // The id is this provider's invention — the runner needs one to tie the
        // result back to the call, and MapMessages() turns it back into the tool
        // name on the way out.
        return new ToolCallResult { Id = $"call_{seq}", Name = name, Arguments = args };
    }

    private string BuildBody(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, bool stream)
    {
        // Sampling and the context length live under `options`, alongside the rest of
        // the modelfile parameters — not at the top level as they do elsewhere.
        var options = new Dictionary<string, object?>
        {
            ["temperature"] = temperature,
            // Configured window, or the default that keeps a real prompt intact.
            ["num_ctx"] = _limits?.ContextWindowTokens ?? DefaultNumCtx,
        };
        if (_limits is not null) options["num_predict"] = _limits.MaxOutputTokens;

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = MapMessages(messages),
            ["stream"] = stream,
            ["options"] = options,
        };
        if (tools.Count > 0)
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema },
            }).ToList();

        return JsonSerializer.Serialize(body);
    }

    // Ollama keeps the four roles, so the mapping is nearly one-to-one. The one
    // translation: a tool result is addressed by tool name, not by call id, so each
    // `tool` message's ToolCallId is resolved against the ids handed out by the
    // assistant turn that asked for it.
    internal static List<Dictionary<string, object?>> MapMessages(List<LlmMessage> messages)
    {
        var namesById = new Dictionary<string, string>();
        foreach (var m in messages)
            foreach (var tc in m.ToolCalls ?? [])
                namesById[tc.Id] = tc.Name;

        var mapped = new List<Dictionary<string, object?>>();
        foreach (var m in messages)
        {
            if (m.Role == "tool")
            {
                var entry = new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["content"] = m.Content ?? string.Empty,
                };
                if (m.ToolCallId is { } id && namesById.TryGetValue(id, out var toolName))
                    entry["tool_name"] = toolName;
                mapped.Add(entry);
                continue;
            }

            var msg = new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Content ?? string.Empty,
            };
            if (m.ToolCalls is { Count: > 0 })
                msg["tool_calls"] = m.ToolCalls.Select(tc => new
                {
                    function = new { name = tc.Name, arguments = tc.Arguments },
                }).ToList();
            mapped.Add(msg);
        }
        return mapped;
    }

    private Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct)
        => LlmHttp.PostAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _chatUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            // A local Ollama needs no credential; the key is sent only when one is
            // configured, for the proxies people put in front of a shared instance.
            if (!string.IsNullOrEmpty(_apiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return req;
        }, "ollama", _logger, ct);

    private static ChatResult ParseResponse(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            throw new InvalidOperationException($"ollama error: {errEl.GetString()}");

        var content = "";
        var toolCalls = new List<ToolCallResult>();

        if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                content = c.GetString() ?? "";
            if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                var seq = 0;
                foreach (var tc in tcs.EnumerateArray())
                    if (ReadToolCall(tc, ++seq) is { } call)
                        toolCalls.Add(call);
            }
        }

        return new ChatResult
        {
            Content = content,
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            InputTokens = ReadCount(root, "prompt_eval_count"),
            OutputTokens = ReadCount(root, "eval_count"),
            StopReason = root.TryGetProperty("done_reason", out var dr) && dr.ValueKind == JsonValueKind.String
                ? dr.GetString() : null,
        };
    }
}
