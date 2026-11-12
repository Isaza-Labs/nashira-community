using System.Net;
using System.Text;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// Exercises the SSE streaming parser (the riskiest part of the agent loop, §6.1)
// against a canned OpenAI-style event stream — no network.
public class OpenAiProviderStreamTests
{
    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
    }

    [Fact]
    public async Task Stream_parses_text_deltas_a_tool_call_and_done()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Hola\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"content\":\" mundo\"}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"device_ping\",\"arguments\":\"\"}}]}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"ip\\\":\\\"1.1.1.1\\\"}\"}}]}}]}",
            "data: {\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":7}}",
            "data: [DONE]",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var provider = new OpenAiProvider(http, "test-key");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "gpt-x", 0.0, CancellationToken.None))
        {
            events.Add(e);
        }

        var text = string.Concat(events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta));
        Assert.Equal("Hola mundo", text);

        var toolCall = events.SingleOrDefault(e => e.Type == "tool_call");
        Assert.NotNull(toolCall);
        Assert.Equal("device_ping", toolCall!.ToolCall!.Name);
        Assert.Equal("1.1.1.1", toolCall.ToolCall.Arguments.GetProperty("ip").GetString());

        Assert.Contains(events, e => e.Type == "done");
    }

    // The one stop reason that matters: "length" means the model was cut off, and the
    // text carries no sign of it. It arrives on the last content chunk and must reach
    // the runner on the "done" event — both of them, since usage-only chunks and the
    // [DONE] sentinel each produce one.
    [Fact]
    public async Task Stream_reports_finish_reason_length_on_done()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Half an ans\"},\"finish_reason\":null}]}",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}",
            "data: {\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":4096}}",
            "data: [DONE]",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var provider = new OpenAiProvider(http, "test-key");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "gpt-x", 0.0, CancellationToken.None))
        {
            events.Add(e);
        }

        var dones = events.Where(e => e.Type == "done").ToList();
        Assert.NotEmpty(dones);
        Assert.All(dones, d => Assert.Equal("length", d.StopReason));
        Assert.Equal("Half an ans", string.Concat(events.Where(e => e.Type == "text_delta").Select(e => e.TextDelta)));
    }

    // A normal stop is reported as such, so the runner's cut-off path stays quiet.
    [Fact]
    public async Task Stream_reports_finish_reason_stop_on_done()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"Done.\"},\"finish_reason\":null}]}",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}",
            "data: [DONE]",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var provider = new OpenAiProvider(http, "test-key");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "gpt-x", 0.0, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Equal("stop", events.Single(e => e.Type == "done").StopReason);
    }

    // Cut off mid-tool-call, the arguments are not JSON. Before, the parse threw out of
    // the enumerator and the whole turn ended as a generic error; now the broken call
    // is dropped and "done" still says why the stream ended.
    [Fact]
    public async Task Stream_drops_a_tool_call_cut_mid_arguments_instead_of_throwing()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"execute_operation\",\"arguments\":\"\"}}]}}]}",
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"operation_id\\\":\\\"endpo\"}}]}}]}",
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}",
            "data: [DONE]",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var provider = new OpenAiProvider(http, "test-key");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "gpt-x", 0.0, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.DoesNotContain(events, e => e.Type == "tool_call");
        Assert.Equal("length", events.Single(e => e.Type == "done").StopReason);
    }

    // How much of the prompt the server read back from its cache. Local
    // OpenAI-compatible servers and OpenAI itself say it in
    // usage.prompt_tokens_details.cached_tokens; without reading it, a working
    // prefix cache and a broken one both look like zero.
    [Theory]
    [InlineData("{\"prompt_tokens\":6038,\"completion_tokens\":8,\"prompt_tokens_details\":{\"cached_tokens\":4096}}", 4096)]
    [InlineData("{\"prompt_tokens\":6038,\"completion_tokens\":8,\"prompt_cache_hit_tokens\":4096,\"prompt_cache_miss_tokens\":1942}", 4096)]
    // Both spellings on one response are the same count twice, not two counts.
    [InlineData("{\"prompt_tokens\":6038,\"completion_tokens\":8,\"prompt_cache_hit_tokens\":4096,\"prompt_tokens_details\":{\"cached_tokens\":4096}}", 4096)]
    [InlineData("{\"prompt_tokens\":6038,\"completion_tokens\":8,\"prompt_tokens_details\":{\"cached_tokens\":0}}", 0)]
    public async Task Stream_reports_cached_prompt_tokens_on_done(string usageJson, int expectedCached)
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}",
            "data: {\"choices\":[],\"usage\":" + usageJson + "}",
            "data: [DONE]",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var provider = new OpenAiProvider(http, "test-key");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "gpt-x", 0.0, CancellationToken.None))
        {
            events.Add(e);
        }

        var usage = events.Single(e => e.Type == "done" && e.InputTokens is not null);
        // The cached part is inside prompt_tokens, never added to it.
        Assert.Equal(6038, usage.InputTokens);
        Assert.Equal(expectedCached, usage.CacheReadTokens);
    }

    // A server that says nothing about its cache reports null, not a made-up zero:
    // the runner's totals then stay at zero without claiming the cache missed.
    [Fact]
    public async Task Stream_leaves_cached_tokens_null_when_the_server_does_not_report_them()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}",
            "data: {\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":7}}",
            "data: [DONE]",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var provider = new OpenAiProvider(http, "test-key");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "gpt-x", 0.0, CancellationToken.None))
        {
            events.Add(e);
        }

        var usage = events.Single(e => e.Type == "done" && e.InputTokens is not null);
        Assert.Null(usage.CacheReadTokens);
    }

    // The usage object can also ride on a chunk that still has choices (some
    // servers attach it to the last content chunk instead of a chunk of its own).
    [Fact]
    public async Task Stream_reads_cached_tokens_from_a_usage_object_on_a_content_chunk()
    {
        var sse = string.Join("\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":1,\"prompt_tokens_details\":{\"cached_tokens\":512}}}",
            "data: [DONE]",
            "");

        using var http = new HttpClient(new CannedHandler(sse));
        var provider = new OpenAiProvider(http, "test-key");

        var events = new List<ChatStreamEvent>();
        await foreach (var e in provider.ChatWithToolsStreamAsync(
            [new LlmMessage { Role = "user", Content = "hi" }], [], "gpt-x", 0.0, CancellationToken.None))
        {
            events.Add(e);
        }

        var usage = events.Single(e => e.Type == "done" && e.InputTokens is not null);
        Assert.Equal(512, usage.CacheReadTokens);
    }
}
