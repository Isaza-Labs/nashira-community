using System.Net;
using System.Text;
using System.Text.Json;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// Same job as OpenAiProviderStreamTests, against Anthropic's typed SSE events: the
// stream parser is the riskiest part of the agent loop, and this one also has to
// rebuild tool calls from JSON fragments and map the message list onto content
// blocks.
public class AnthropicProviderStreamTests
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
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private static async Task<List<ChatStreamEvent>> Collect(
        AnthropicProvider provider, List<LlmMessage>? messages = null, string model = "claude-opus-5")
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            messages ?? [new LlmMessage { Role = "user", Content = "hi" }], [], model, 0.0, CancellationToken.None))
        {
            events.Add(e);
        }
        return events;
    }

    [Fact]
    public async Task Stream_parses_text_deltas_a_tool_call_and_done()
    {
        var sse = string.Join("\n",
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hola\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" mundo\"}}",
            "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"device_ping\",\"input\":{}}}",
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"ip\\\":\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"1.1.1.1\\\"}\"}}",
            "data: {\"type\":\"content_block_stop\",\"index\":1}",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":7}}",
            "data: {\"type\":\"message_stop\"}",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var events = await Collect(new AnthropicProvider(http, "test-key"));

        Assert.Equal("Hola mundo", string.Concat(events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta)));

        var toolCall = events.SingleOrDefault(e => e.Type == "tool_call");
        Assert.NotNull(toolCall);
        Assert.Equal("toolu_1", toolCall!.ToolCall!.Id);
        Assert.Equal("device_ping", toolCall.ToolCall.Name);
        Assert.Equal("1.1.1.1", toolCall.ToolCall.Arguments.GetProperty("ip").GetString());

        var done = events.Single(e => e.Type == "done");
        Assert.Equal("tool_use", done.StopReason);
        Assert.Equal(5, done.InputTokens);
        Assert.Equal(7, done.OutputTokens);
    }

    // "max_tokens" is Anthropic's spelling of a cut-off answer, and the runner reads
    // it as one — so it has to arrive on the "done" event unchanged.
    [Fact]
    public async Task Stream_reports_stop_reason_max_tokens_on_done()
    {
        var sse = string.Join("\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Half an ans\"}}",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"},\"usage\":{\"output_tokens\":4096}}",
            "data: {\"type\":\"message_stop\"}",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var events = await Collect(new AnthropicProvider(http, "test-key"));

        var done = events.Single(e => e.Type == "done");
        Assert.Equal("max_tokens", done.StopReason);
        Assert.True(nashira_backend.Services.Ai.Conversation.AgentConversationRunner.OutputCutOff(done.StopReason));
        Assert.Equal("Half an ans", string.Concat(events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta)));
    }

    // Cut off mid-tool-call, the buffered fragments are not JSON. The call is dropped
    // and "done" still says why the stream ended.
    [Fact]
    public async Task Stream_drops_a_tool_call_cut_mid_arguments_instead_of_throwing()
    {
        var sse = string.Join("\n",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"execute_operation\"}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"operation_id\\\":\\\"endpo\"}}",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}",
            "data: {\"type\":\"message_stop\"}",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var events = await Collect(new AnthropicProvider(http, "test-key"));

        Assert.DoesNotContain(events, e => e.Type == "tool_call");
        Assert.Equal("max_tokens", events.Single(e => e.Type == "done").StopReason);
    }

    // A mid-stream error is HTTP 200, so only this throw ends the turn as an error
    // instead of as a silently short answer.
    [Fact]
    public async Task Stream_throws_on_a_mid_stream_error_event()
    {
        var sse = string.Join("\n",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"partial\"}}",
            "data: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var provider = new AnthropicProvider(http, "test-key");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Collect(provider));
        Assert.Contains("Overloaded", ex.Message);
    }

    // The system prompt is a top-level field here, not a message, and a tool round's
    // results have to arrive as content blocks of one user turn.
    [Fact]
    public void MapMessages_lifts_the_system_prompt_and_merges_a_parallel_tool_round()
    {
        var args = JsonDocument.Parse("{\"ip\":\"1.1.1.1\"}").RootElement;
        var (system, mapped) = AnthropicProvider.MapMessages(
        [
            new LlmMessage { Role = "system", Content = "you are an agent" },
            new LlmMessage { Role = "system", Content = "profile: net" },
            new LlmMessage { Role = "user", Content = "ping both" },
            new LlmMessage
            {
                Role = "assistant",
                Content = "on it",
                ToolCalls =
                [
                    new ToolCallResult { Id = "toolu_1", Name = "device_ping", Arguments = args },
                    new ToolCallResult { Id = "toolu_2", Name = "device_ping", Arguments = args },
                ],
            },
            new LlmMessage { Role = "tool", Content = "ok", ToolCallId = "toolu_1" },
            new LlmMessage { Role = "tool", Content = "ok", ToolCallId = "toolu_2" },
            // The runner slips a system message in behind a tool result when a skill
            // auto-loads; it must not reopen the top-level system field.
            new LlmMessage { Role = "system", Content = "skill: netbox" },
        ]);

        Assert.Equal("you are an agent\n\nprofile: net", system);
        Assert.Equal(3, mapped.Count);
        Assert.Equal("user", mapped[0]["role"]);
        Assert.Equal("assistant", mapped[1]["role"]);

        var assistantBlocks = (List<Dictionary<string, object?>>)mapped[1]["content"]!;
        Assert.Equal("text", assistantBlocks[0]["type"]);
        Assert.Equal("tool_use", assistantBlocks[1]["type"]);
        Assert.Equal("tool_use", assistantBlocks[2]["type"]);

        // Both results plus the trailing skill text, in one user turn, results first.
        var resultBlocks = (List<Dictionary<string, object?>>)mapped[2]["content"]!;
        Assert.Equal("user", mapped[2]["role"]);
        Assert.Equal(["tool_result", "tool_result", "text"], resultBlocks.Select(b => (string?)b["type"]));
    }

    // Sending `temperature` to a current model is a 400, not a warning; sending it to
    // an older one is what the tenant configured. Both directions matter.
    [Theory]
    [InlineData("claude-opus-5", false)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-3-5-sonnet-20241022", true)]
    public async Task Request_sends_temperature_only_to_models_that_accept_it(string model, bool expected)
    {
        var handler = new CannedHandler("data: {\"type\":\"message_stop\"}\n");
        using var http = new HttpClient(handler);
        await Collect(new AnthropicProvider(http, "test-key"), model: model);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(expected, body.RootElement.TryGetProperty("temperature", out _));
        // max_tokens is required on every request, and stays under a model's own cap.
        Assert.True(body.RootElement.GetProperty("max_tokens").GetInt32() > 0);
    }

    [Fact]
    public async Task Request_clamps_max_tokens_to_an_older_models_output_cap()
    {
        var handler = new CannedHandler("data: {\"type\":\"message_stop\"}\n");
        using var http = new HttpClient(handler);
        await Collect(new AnthropicProvider(http, "test-key"), model: "claude-3-5-sonnet-20241022");

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(8_192, body.RootElement.GetProperty("max_tokens").GetInt32());
    }
}
