using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using nashira_backend.Data.DTos.Ai;

namespace nashira_backend.Services.Ai.Providers;

// Direct HTTP implementation against the Anthropic Messages API
// ({baseUrl}/v1/messages). The wire format is not OpenAI's: the system prompt is a
// top-level field rather than a message, tool calls and tool results are content
// blocks inside user/assistant turns, and the stream is a typed SSE event sequence
// instead of choice deltas. All of it is mapped to the shared LlmMessage /
// ChatStreamEvent shapes here, so the agent loop sees one provider.
//
// Not mapped: extended thinking. Thinking blocks have to be replayed verbatim on
// every later request of the same turn, and LlmMessage has nowhere to carry them —
// a tool-calling loop that drops them is worse than one that never asked for them,
// so requests here leave `thinking` unset and the model answers without it.
public sealed class AnthropicProvider : IStreamingToolCallingLlmProvider
{
    public string ProviderType => "anthropic";

    // Required on every request. It pins the wire format, not the model.
    private const string ApiVersion = "2023-06-01";

    // Dumps every incoming SSE line at Debug when AI_LOG_PAYLOADS=true. Off by
    // default so the steady-state log stream stays manageable.
    private static readonly bool LogPayloads =
        string.Equals(Environment.GetEnvironmentVariable("AI_LOG_PAYLOADS"),
            "true", StringComparison.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ILogger<AnthropicProvider>? _logger;
    private readonly string _messagesUrl;
    private readonly ModelLimits? _limits;
    private readonly string? _workspaceId;

    // Provider Config key holding the workspace this deployment acts in. An
    // identity-linked API key belongs to a person who may have access to several
    // workspaces, so the key alone does not say which one to bill and scope the
    // request to: without the header the API answers 400
    // "anthropic-workspace-id is required when authenticating with an
    // identity-linked API key". A plain (workspace-scoped) key carries its own
    // workspace and needs none of this, which is why it is optional.
    public const string WorkspaceIdConfigKey = "workspace_id";

    public AnthropicProvider(
        HttpClient http, string apiKey, string? baseUrl = null, ILogger<AnthropicProvider>? logger = null,
        ModelLimits? limits = null, string? workspaceId = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        _logger = logger;
        _limits = limits;
        _workspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId.Trim();
        // A base URL pasted with the version on it ("…/v1") would otherwise become
        // /v1/v1/messages. See LlmHttp.CombineUrl.
        _messagesUrl = LlmHttp.CombineUrl(_baseUrl, "/v1/messages");
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

        // A tool call arrives as a content block that opens with its id and name and
        // is then filled by input_json_delta fragments. Keyed by block index, held
        // until the stream ends so the flush below can report a truncated one next to
        // the stop reason that explains it.
        var toolBlocks = new Dictionary<int, (string id, string name, StringBuilder args)>();
        string? stopReason = null;
        var inputTokens = 0;
        var outputTokens = 0;

        _logger?.LogDebug(
            "anthropic.stream.connected model={Model} messages={MessageCount} tools={ToolCount}",
            model, messages.Count, tools.Count);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break; // end of stream
            // The `event:` line repeats the payload's own "type" field, so only the
            // data is read; blank lines separate events.
            if (line.Length == 0 || !line.StartsWith("data: ")) continue;
            var data = line[6..];

            if (LogPayloads)
                _logger?.LogDebug("anthropic.stream.raw_chunk {Data}", data.Length > 500 ? data[..500] + "…" : data);

            JsonElement evt;
            try { evt = JsonDocument.Parse(data).RootElement; }
            catch { continue; }

            var type = evt.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            if (type == "error")
            {
                // A mid-stream error is HTTP 200 with an error event, so nothing above
                // has raised. Throwing here is what makes the turn end as an error
                // carrying the upstream's own words, instead of as a short answer.
                var message = evt.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.Object
                    && err.TryGetProperty("message", out var em)
                    && em.ValueKind == JsonValueKind.String
                        ? em.GetString()
                        : data;
                throw new InvalidOperationException($"anthropic stream error: {message}");
            }

            switch (type)
            {
                case "message_start":
                    if (evt.TryGetProperty("message", out var msg)
                        && msg.ValueKind == JsonValueKind.Object
                        && msg.TryGetProperty("usage", out var u0)
                        && u0.ValueKind == JsonValueKind.Object
                        && u0.TryGetProperty("input_tokens", out var it0)
                        && it0.ValueKind == JsonValueKind.Number)
                        inputTokens = it0.GetInt32();
                    break;

                case "content_block_start":
                {
                    if (!evt.TryGetProperty("content_block", out var block)
                        || block.ValueKind != JsonValueKind.Object) break;
                    if (!block.TryGetProperty("type", out var bt) || bt.GetString() != "tool_use") break;
                    var idx = evt.TryGetProperty("index", out var ie) && ie.ValueKind == JsonValueKind.Number
                        ? ie.GetInt32() : 0;
                    toolBlocks[idx] = (
                        block.TryGetProperty("id", out var bid) && bid.ValueKind == JsonValueKind.String
                            ? bid.GetString()! : "",
                        block.TryGetProperty("name", out var bn) && bn.ValueKind == JsonValueKind.String
                            ? bn.GetString()! : "",
                        new StringBuilder());
                    break;
                }

                case "content_block_delta":
                {
                    if (!evt.TryGetProperty("delta", out var delta)
                        || delta.ValueKind != JsonValueKind.Object) break;
                    var dt = delta.TryGetProperty("type", out var dte) && dte.ValueKind == JsonValueKind.String
                        ? dte.GetString() : null;

                    if (dt == "text_delta"
                        && delta.TryGetProperty("text", out var txt)
                        && txt.ValueKind == JsonValueKind.String)
                    {
                        var text = txt.GetString();
                        if (!string.IsNullOrEmpty(text))
                            yield return new ChatStreamEvent { Type = "text_delta", TextDelta = text };
                    }
                    else if (dt == "input_json_delta"
                        && delta.TryGetProperty("partial_json", out var pj)
                        && pj.ValueKind == JsonValueKind.String)
                    {
                        var idx = evt.TryGetProperty("index", out var ie) && ie.ValueKind == JsonValueKind.Number
                            ? ie.GetInt32() : 0;
                        if (toolBlocks.TryGetValue(idx, out var prog)) prog.args.Append(pj.GetString());
                    }
                    // thinking_delta / signature_delta are ignored: thinking is never
                    // requested, and there is nothing here to replay it into.
                    break;
                }

                case "message_delta":
                    if (evt.TryGetProperty("delta", out var md)
                        && md.ValueKind == JsonValueKind.Object
                        && md.TryGetProperty("stop_reason", out var sr)
                        && sr.ValueKind == JsonValueKind.String)
                        stopReason = sr.GetString();
                    // Output tokens are cumulative on this event; the last one wins.
                    if (evt.TryGetProperty("usage", out var u1)
                        && u1.ValueKind == JsonValueKind.Object
                        && u1.TryGetProperty("output_tokens", out var ot1)
                        && ot1.ValueKind == JsonValueKind.Number)
                        outputTokens = ot1.GetInt32();
                    break;
            }

            if (type == "message_stop") break;
        }

        foreach (var (_, (id, name, args)) in toolBlocks)
        {
            JsonElement argsJson;
            try
            {
                argsJson = args.Length > 0
                    ? JsonDocument.Parse(args.ToString()).RootElement
                    : JsonDocument.Parse("{}").RootElement;
            }
            catch (JsonException ex)
            {
                // Arguments that do not parse are a stream that ended mid-call —
                // stop_reason "max_tokens" — not a tool the model meant to run with
                // those bytes. Dropping it here lets the "done" event carry the stop
                // reason instead of the whole turn dying on a parse error.
                _logger?.LogWarning(
                    "anthropic.stream.tool_call_unparseable name={Name} stop_reason={StopReason} error={Error}",
                    name, stopReason, ex.Message);
                continue;
            }
            yield return new ChatStreamEvent
            {
                Type = "tool_call",
                ToolCall = new ToolCallResult { Id = id, Name = name, Arguments = argsJson },
            };
        }

        yield return new ChatStreamEvent
        {
            Type = "done",
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            // Reported as the API words it: the runner already reads Anthropic's
            // "max_tokens" as a cut-off alongside OpenAI's "length".
            StopReason = stopReason,
        };
    }

    // Anthropic requires max_tokens on every request — there is no "as much as the
    // model will give" — so a number has to be chosen here. Non-streaming stays well
    // under the transport timeout; a stream has no such ceiling and gets room for a
    // long answer.
    private const int MaxTokensNonStreaming = 16_000;
    private const int MaxTokensStreaming = 64_000;

    // A request above a model's own output cap is a 400, so the smaller of the two
    // wins. Only these families cap below the numbers above; every current model is
    // unlisted and takes the default.
    //
    // Opus 4 and 4.1 are the entries worth naming: they are not old enough to look
    // suspicious, they are the only 4.x models that stop at 32k, and at the streaming
    // default they would have failed every request outright.
    private static int CapFor(string model) =>
        model.StartsWith("claude-opus-4-0", StringComparison.OrdinalIgnoreCase) ? 32_000 :
        model.StartsWith("claude-opus-4-1", StringComparison.OrdinalIgnoreCase) ? 32_000 :
        model.StartsWith("claude-3-7", StringComparison.OrdinalIgnoreCase) ? 64_000 :
        model.StartsWith("claude-3-5", StringComparison.OrdinalIgnoreCase) ? 8_192 :
        model.StartsWith("claude-3", StringComparison.OrdinalIgnoreCase) ? 4_096 :
        int.MaxValue;

    // Config.model_limits wins over the defaults above, and the model's own cap wins
    // over both: a hand-typed number in Config would otherwise 400 every request.
    private int MaxTokensFor(string model, bool stream)
        => Math.Min(
            _limits?.MaxOutputTokens ?? (stream ? MaxTokensStreaming : MaxTokensNonStreaming),
            CapFor(model));

    // Sampling parameters were removed on the current model families (Opus 4.7 and
    // later, Sonnet 5, Fable): `temperature` there is a 400, not a warning. This list
    // is of the models that still ACCEPT it, so a model released after this code —
    // the case that keeps recurring — runs at the provider default rather than
    // failing outright.
    private static readonly string[] SamplingModelPrefixes =
    [
        "claude-3",
        "claude-opus-4-0", "claude-opus-4-1", "claude-opus-4-5", "claude-opus-4-6",
        "claude-sonnet-4-0", "claude-sonnet-4-5", "claude-sonnet-4-6",
        "claude-haiku-4-5",
    ];

    private static bool AcceptsTemperature(string model)
        => SamplingModelPrefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private string BuildBody(
        List<LlmMessage> messages, List<ToolDefinition> tools,
        string model, double temperature, bool stream)
    {
        var (system, mapped) = MapMessages(messages);

        // The clamp is what keeps the request legal, but it also shortens every answer
        // this model can give, and nothing downstream would say why: a reply cut at 8k
        // reads like a reply the model chose to end. So it is named here, once per
        // request, only when a cap actually bit.
        var maxTokens = MaxTokensFor(model, stream);
        var want = _limits?.MaxOutputTokens ?? (stream ? MaxTokensStreaming : MaxTokensNonStreaming);
        if (maxTokens < want)
            _logger?.LogWarning(
                "anthropic.max_tokens.clamped model={Model} requested={Requested} model_cap={Cap} "
                + "(answers from this model are limited to its own output ceiling)",
                model, want, maxTokens);

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = mapped,
            ["stream"] = stream,
        };
        if (!string.IsNullOrWhiteSpace(system)) body["system"] = system;
        if (AcceptsTemperature(model)) body["temperature"] = temperature;
        if (tools.Count > 0)
            body["tools"] = tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = t.ParametersSchema,
            }).ToList();

        return JsonSerializer.Serialize(body);
    }

    // Maps the shared message list onto Anthropic's shape: the leading system
    // messages become the top-level `system` field, everything else becomes
    // user/assistant turns made of content blocks.
    //
    // Two things this has to get right. Roles alternate, so blocks are appended to
    // the open turn whenever the role repeats — which is also what puts a parallel
    // tool round's results into a single user message, the shape the API requires.
    // And a system message that arrives mid-conversation (the runner adds one behind
    // a tool result when a skill auto-loads) is rendered as user text: only some
    // models accept a system entry inside `messages`, and the tenant picks the model.
    internal static (string? System, List<Dictionary<string, object?>> Messages) MapMessages(
        List<LlmMessage> messages)
    {
        var system = new StringBuilder();
        var mapped = new List<Dictionary<string, object?>>();
        var blocks = new List<Dictionary<string, object?>>();
        string? openRole = null;

        void Flush()
        {
            // A turn that ends up with no blocks (an assistant message that was only an
            // empty string) is dropped: the API rejects an empty content array.
            if (openRole is not null && blocks.Count > 0)
                mapped.Add(new Dictionary<string, object?> { ["role"] = openRole, ["content"] = blocks });
            blocks = [];
            openRole = null;
        }

        void Open(string role)
        {
            if (openRole == role) return;
            Flush();
            openRole = role;
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case "system" when mapped.Count == 0 && openRole is null:
                    if (!string.IsNullOrWhiteSpace(m.Content))
                    {
                        if (system.Length > 0) system.Append("\n\n");
                        system.Append(m.Content);
                    }
                    break;

                case "system": // mid-conversation — see the note above
                case "user":
                    if (string.IsNullOrEmpty(m.Content)) break;
                    Open("user");
                    blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = m.Content });
                    break;

                case "tool":
                    Open("user");
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = m.ToolCallId,
                        ["content"] = m.Content ?? string.Empty,
                    });
                    break;

                case "assistant":
                    // An assistant message with nothing in it is skipped WITHOUT
                    // opening a turn. Opening one and letting Flush drop it for being
                    // empty is what produced the bug this guards: the open turn
                    // flushes the user turn before it, the empty assistant turn is
                    // then dropped, and the user turns on either side end up adjacent
                    // — which Anthropic rejects for the same reason it rejects the
                    // empty turn. Skipping instead merges the two user turns, which
                    // is honest: the assistant did not say anything between them.
                    //
                    // Empty is not hypothetical. A turn that stops to ask for tool
                    // confirmation produces no text and is not an error, so "" is what
                    // gets persisted; every later turn in that conversation replayed it
                    // and got a 400, permanently.
                    if (string.IsNullOrEmpty(m.Content) && (m.ToolCalls is null || m.ToolCalls.Count == 0))
                        break;
                    Open("assistant");
                    if (!string.IsNullOrEmpty(m.Content))
                        blocks.Add(new Dictionary<string, object?> { ["type"] = "text", ["text"] = m.Content });
                    foreach (var tc in m.ToolCalls ?? [])
                        blocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id,
                            ["name"] = tc.Name,
                            ["input"] = tc.Arguments,
                        });
                    break;
            }
        }
        Flush();

        // The conversation has to open on a user turn. The runner always builds one,
        // but a windowed history could in principle start on an assistant turn, and
        // that is a 400 rather than a degraded answer.
        if (mapped.Count > 0 && (string?)mapped[0]["role"] == "assistant")
            mapped.Insert(0, new Dictionary<string, object?>
            {
                ["role"] = "user",
                ["content"] = new List<Dictionary<string, object?>>
                {
                    new() { ["type"] = "text", ["text"] = "(continuing the earlier conversation)" },
                },
            });

        return (system.Length > 0 ? system.ToString() : null, mapped);
    }

    private Task<HttpResponseMessage> PostAsync(string body, CancellationToken ct)
        => LlmHttp.PostAsync(_http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _messagesUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            // Anthropic authenticates on its own header, not Authorization: Bearer.
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", ApiVersion);
            // Only when configured: sending it with a workspace-scoped key is not
            // an error, but an empty or wrong value is, and every deployment that
            // uses a plain key would then have to carry one.
            if (_workspaceId is not null)
                req.Headers.Add("anthropic-workspace-id", _workspaceId);
            return req;
        }, "anthropic", _logger, ct);

    private static ChatResult ParseResponse(JsonElement root)
    {
        var text = new StringBuilder();
        var toolCalls = new List<ToolCallResult>();

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var type = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var bx) && bx.ValueKind == JsonValueKind.String)
                {
                    text.Append(bx.GetString());
                }
                else if (type == "tool_use")
                {
                    toolCalls.Add(new ToolCallResult
                    {
                        Id = block.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        // Cloned: the input belongs to the response document, which the
                        // caller disposes as soon as this returns.
                        Arguments = block.TryGetProperty("input", out var inp)
                            ? inp.Clone()
                            : JsonDocument.Parse("{}").RootElement,
                    });
                }
            }
        }

        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var hasUsage = usage.ValueKind == JsonValueKind.Object;

        return new ChatResult
        {
            Content = text.ToString(),
            ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            InputTokens = hasUsage && usage.TryGetProperty("input_tokens", out var it)
                && it.ValueKind == JsonValueKind.Number ? it.GetInt32() : 0,
            OutputTokens = hasUsage && usage.TryGetProperty("output_tokens", out var ot)
                && ot.ValueKind == JsonValueKind.Number ? ot.GetInt32() : 0,
            StopReason = root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String
                ? sr.GetString() : null,
        };
    }
}
