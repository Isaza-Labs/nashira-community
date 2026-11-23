using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// The "progress" event a provider sends when the model starts producing output it
// does not hand over yet, and the cache counts Gemini reports. Both feed the
// runner's per-round log only; nothing here changes what a turn does.
public class StreamProgressAndCacheTests
{
    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
    }

    private static async Task<List<ChatStreamEvent>> Collect(ILlmProvider provider)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in ((IStreamingToolCallingLlmProvider)provider).ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "m", 0.0, CancellationToken.None))
            events.Add(e);
        return events;
    }

    private static string Sse(params string[] lines) => string.Join("\n", lines) + "\n";

    // The shape a thinking model sends before it calls a tool: a role-only opener,
    // reasoning, then the tool call, which is handed over only at [DONE].
    [Fact]
    public async Task OpenAi_sends_one_progress_event_when_reasoning_starts_not_on_the_role_only_opener()
    {
        var sse = Sse(
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Need the list.\"}}]}",
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\" Calling it.\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"list_workflows\",\"arguments\":\"{}\"}}]}}]}",
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":4}}",
            "data: [DONE]");
        using var http = new HttpClient(new CannedHandler(sse));

        var types = (await Collect(new OpenAiProvider(http, "k"))).Select(e => e.Type).ToList();

        Assert.Equal("progress", types[0]);
        Assert.Single(types, t => t == "progress");
        Assert.True(types.IndexOf("progress") < types.IndexOf("tool_call"));
    }

    [Fact]
    public async Task OpenAi_sends_progress_ahead_of_the_first_text_and_only_once()
    {
        var sse = Sse(
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hola\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\" mundo\"}}]}",
            "data: [DONE]");
        using var http = new HttpClient(new CannedHandler(sse));

        var events = await Collect(new OpenAiProvider(http, "k"));

        Assert.Equal(["progress", "text_delta", "text_delta", "done"], events.Select(e => e.Type).ToArray());
        Assert.Equal("Hola mundo", string.Concat(events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta)));
    }

    [Fact]
    public async Task OpenAi_sends_no_progress_when_the_model_produces_nothing()
    {
        var sse = Sse(
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]");
        using var http = new HttpClient(new CannedHandler(sse));

        var events = await Collect(new OpenAiProvider(http, "k"));

        Assert.DoesNotContain(events, e => e.Type == "progress");
    }

    // Reasoning alone is enough: a round that only thinks still reports when it
    // started, so the progress above cannot be coming from the tool-call fragment.
    [Fact]
    public async Task OpenAi_sends_progress_for_a_round_that_only_reasons()
    {
        var sse = Sse(
            "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Thinking.\"}}]}",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]");
        using var http = new HttpClient(new CannedHandler(sse));

        var types = (await Collect(new OpenAiProvider(http, "k"))).Select(e => e.Type).ToList();

        Assert.Equal(["progress", "done"], types.ToArray());
    }

    [Theory]
    [InlineData("""{"role":"assistant"}""", false)]
    [InlineData("""{"content":""}""", false)]
    [InlineData("""{"content":null,"tool_calls":[]}""", false)]
    [InlineData("""{"content":"x"}""", true)]
    [InlineData("""{"reasoning_content":"x"}""", true)]
    [InlineData("""{"reasoning":"x"}""", true)]
    [InlineData("""{"tool_calls":[{"index":0}]}""", true)]
    public void OpenAi_counts_only_generated_output_as_progress(string delta, bool expected)
        => Assert.Equal(expected, OpenAiProvider.HasGeneratedContent(JsonDocument.Parse(delta).RootElement));

    [Fact]
    public async Task Anthropic_sends_progress_when_a_tool_call_opens_ahead_of_the_buffered_call()
    {
        var sse = Sse(
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"a\",\"input\":{}}}",
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}",
            "data: {\"type\":\"content_block_stop\",\"index\":0}",
            "data: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"t2\",\"name\":\"b\",\"input\":{}}}",
            "data: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{}\"}}",
            "data: {\"type\":\"content_block_stop\",\"index\":1}",
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\"},\"usage\":{\"output_tokens\":7}}",
            "data: {\"type\":\"message_stop\"}");
        using var http = new HttpClient(new CannedHandler(sse));

        var types = (await Collect(new AnthropicProvider(http, "k"))).Select(e => e.Type).ToList();

        Assert.Equal("progress", types[0]);
        Assert.Single(types, t => t == "progress");
        Assert.Equal(2, types.Count(t => t == "tool_call"));
    }

    private static GeminiProvider Gemini(string body)
        => new(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, body, "text/event-stream"), disposeHandler: false),
            "AIza-test", null, NullLogger<GeminiProvider>.Instance);

    [Fact]
    public async Task Gemini_stream_reports_the_cached_part_of_the_prompt()
    {
        var body =
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hi\"}]}}],\"usageMetadata\":{\"promptTokenCount\":1200,\"candidatesTokenCount\":1}}\n\n" +
            "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"!\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":1200,\"cachedContentTokenCount\":1024,\"candidatesTokenCount\":2}}\n\n";

        var done = (await Collect(Gemini(body))).Single(e => e.Type == "done");

        Assert.Equal(1200, done.InputTokens);
        Assert.Equal(1024, done.CacheReadTokens);
    }

    [Fact]
    public async Task Gemini_stream_without_a_cache_figure_reports_none()
    {
        var body = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hi\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":12,\"candidatesTokenCount\":1}}\n\n";

        var done = (await Collect(Gemini(body))).Single(e => e.Type == "done");

        Assert.Null(done.CacheReadTokens);
    }

    [Fact]
    public async Task Gemini_reply_reports_the_cached_part_of_the_prompt()
    {
        var body = """{"candidates":[{"content":{"parts":[{"text":"Hi"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":1200,"cachedContentTokenCount":1024,"candidatesTokenCount":2}}""";
        var provider = new GeminiProvider(
            new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, body, "application/json"), disposeHandler: false),
            "AIza-test", null, NullLogger<GeminiProvider>.Instance);

        var result = await provider.ChatAsync([new LlmMessage { Role = "user", Content = "hi" }], "gemini-2.5-pro", 0.7, default);

        Assert.Equal(1200, result.InputTokens);
        Assert.Equal(1024, result.CacheReadTokens);
    }
}
