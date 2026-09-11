using System.Net;
using System.Text;
using System.Text.Json;
using nashira_backend.Data.DTos.Ai;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// Ollama streams NDJSON, not SSE, and hands over whole tool calls with no ids —
// the two places this parser can silently differ from the others.
public class OllamaProviderStreamTests
{
    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public string? LastUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-ndjson"),
            };
        }
    }

    private static async Task<List<ChatStreamEvent>> Collect(OllamaProvider provider, List<LlmMessage>? messages = null)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            messages ?? [new LlmMessage { Role = "user", Content = "hi" }], [], "llama3.1", 0.0, CancellationToken.None))
        {
            events.Add(e);
        }
        return events;
    }

    [Fact]
    public async Task Stream_parses_text_deltas_a_tool_call_and_done()
    {
        var ndjson = string.Join("\n",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Hola\"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\" mundo\"},\"done\":false}",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"device_ping\",\"arguments\":{\"ip\":\"1.1.1.1\"}}}]},\"done\":false}",
            "{\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":5,\"eval_count\":7}",
            "");

        using var http = new HttpClient(new CannedHandler(ndjson));
        var events = await Collect(new OllamaProvider(http, ""));

        Assert.Equal("Hola mundo", string.Concat(events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta)));

        var toolCall = events.Single(e => e.Type == "tool_call");
        Assert.Equal("device_ping", toolCall.ToolCall!.Name);
        Assert.Equal("1.1.1.1", toolCall.ToolCall.Arguments.GetProperty("ip").GetString());
        // Ollama issues no id; the runner needs one to tie the result back to the call.
        Assert.False(string.IsNullOrEmpty(toolCall.ToolCall.Id));

        var done = events.Single(e => e.Type == "done");
        Assert.Equal("stop", done.StopReason);
        Assert.Equal(5, done.InputTokens);
        Assert.Equal(7, done.OutputTokens);
    }

    // Ollama's own word for a cut-off answer happens to be OpenAI's, and the runner
    // reads it as one.
    [Fact]
    public async Task Stream_reports_done_reason_length_on_done()
    {
        var ndjson = string.Join("\n",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"Half an ans\"},\"done\":false}",
            "{\"done\":true,\"done_reason\":\"length\",\"prompt_eval_count\":5,\"eval_count\":4096}",
            "");

        using var http = new HttpClient(new CannedHandler(ndjson));
        var events = await Collect(new OllamaProvider(http, ""));

        var done = events.Single(e => e.Type == "done");
        Assert.Equal("length", done.StopReason);
        Assert.True(nashira_backend.Services.Ai.Conversation.AgentConversationRunner.OutputCutOff(done.StopReason));
    }

    // A model that was never pulled comes back as an error inside a 200 body.
    [Fact]
    public async Task Stream_throws_on_an_error_body()
    {
        using var http = new HttpClient(new CannedHandler("{\"error\":\"model 'llama3.1' not found\"}\n"));
        var provider = new OllamaProvider(http, "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Collect(provider));
        Assert.Contains("not found", ex.Message);
    }

    // A tool result is addressed by tool name here, so the id the stream invented has
    // to resolve back to the name it came from.
    [Fact]
    public void MapMessages_resolves_a_tool_result_back_to_its_tool_name()
    {
        var args = JsonDocument.Parse("{\"ip\":\"1.1.1.1\"}").RootElement;
        var mapped = OllamaProvider.MapMessages(
        [
            new LlmMessage { Role = "user", Content = "ping" },
            new LlmMessage
            {
                Role = "assistant",
                Content = "",
                ToolCalls = [new ToolCallResult { Id = "call_1", Name = "device_ping", Arguments = args }],
            },
            new LlmMessage { Role = "tool", Content = "ok", ToolCallId = "call_1" },
        ]);

        Assert.Equal(3, mapped.Count);
        Assert.Equal("tool", mapped[2]["role"]);
        Assert.Equal("device_ping", mapped[2]["tool_name"]);
    }

    [Fact]
    public async Task Request_puts_temperature_under_options()
    {
        var handler = new CannedHandler("{\"done\":true,\"done_reason\":\"stop\"}\n");
        using var http = new HttpClient(handler);
        await foreach (var _ in new OllamaProvider(http, "").ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "llama3.1", 0.4, CancellationToken.None)) { }

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.False(body.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal(0.4, body.RootElement.GetProperty("options").GetProperty("temperature").GetDouble(), 3);
    }

    // Without num_ctx the daemon applies the model's own (small) context and drops the
    // front of the prompt — the system prompt — without saying so.
    [Fact]
    public async Task Request_sends_num_ctx_so_the_prompt_is_not_silently_truncated()
    {
        var handler = new CannedHandler("{\"done\":true,\"done_reason\":\"stop\"}");
        using var http = new HttpClient(handler);
        await Collect(new OllamaProvider(http, ""));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(
            OllamaProvider.DefaultNumCtx,
            body.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    // Config.model_limits is the per-deployment override — the same key flow-weaver
    // uses — and it also sets num_predict, Ollama's output cap.
    [Fact]
    public async Task Request_honours_configured_model_limits()
    {
        var handler = new CannedHandler("{\"done\":true,\"done_reason\":\"stop\"}");
        using var http = new HttpClient(handler);
        var limits = ModelLimits.FromConfig(JsonDocument.Parse(
            "{\"model_limits\":{\"context_window\":8192,\"max_output_tokens\":1024}}").RootElement);

        await Collect(new OllamaProvider(http, "", null, null, limits));

        using var body = JsonDocument.Parse(handler.LastBody!);
        var options = body.RootElement.GetProperty("options");
        Assert.Equal(8_192, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(1_024, options.GetProperty("num_predict").GetInt32());
    }

    // "http://host/api" is a natural thing to paste; it must not become /api/api/chat.
    [Theory]
    [InlineData(null, "http://localhost:11434/api/chat")]
    [InlineData("http://ollama.internal:11434", "http://ollama.internal:11434/api/chat")]
    [InlineData("http://ollama.internal:11434/api", "http://ollama.internal:11434/api/chat")]
    public async Task Request_does_not_repeat_a_path_segment_the_base_url_already_has(
        string? baseUrl, string expected)
    {
        var handler = new CannedHandler("{\"done\":true,\"done_reason\":\"stop\"}");
        using var http = new HttpClient(handler);
        await Collect(new OllamaProvider(http, "", baseUrl));

        Assert.Equal(expected, handler.LastUrl);
    }
}
