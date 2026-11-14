using System.IO.Pipelines;
using System.Net;
using System.Text;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// A reply that goes silent part-way through ends after the idle limit instead of
// holding the turn until its deadline. Everything else behaves as before: a server
// that is slow to start (whatever it sends first), a line that arrives in pieces,
// keepalives and the caller's own cancellation.
//
// Every wait is bounded, so a change that stops the limit from firing fails these
// tests instead of hanging them.
public class StreamIdleLimitTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    // A response body the test writes into and never finishes unless told to.
    private sealed class OpenBody
    {
        private readonly Pipe _pipe = new();
        public Stream Stream => _pipe.Reader.AsStream();
        public async Task WriteAsync(string text) => await _pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
        public Task CompleteAsync() => _pipe.Writer.CompleteAsync().AsTask();
    }

    private sealed class BodyHandler(OpenBody body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body.Stream),
            });
    }

    private static readonly List<LlmMessage> Hi = [new LlmMessage { Role = "user", Content = "hi" }];

    // The reply's text; throws what the stream throws.
    private static async Task<string> Text(IAsyncEnumerable<ChatStreamEvent> events)
    {
        var text = new StringBuilder();
        await foreach (var e in events)
            if (e.Type == "text_delta") text.Append(e.TextDelta);
        return text.ToString();
    }

    // ── the reader ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_that_goes_silent_after_it_started_ends_after_the_limit()
    {
        var body = new OpenBody();
        await body.WriteAsync("first\n");
        using var lines = new StreamLineReader(body.Stream, Limit);
        Assert.Equal("first", await lines.ReadLineAsync(CancellationToken.None));
        lines.ReplyStarted();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => lines.ReadLineAsync(CancellationToken.None).WaitAsync(Bound));

        Assert.Equal(Limit, e.Limit);
        Assert.InRange(watch.Elapsed, Limit * 0.8, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Silence_before_anything_arrives_is_not_cut_off()
    {
        var body = new OpenBody();
        using var lines = new StreamLineReader(body.Stream, Limit);

        var read = lines.ReadLineAsync(CancellationToken.None);
        await Task.Delay(Limit * 3);
        Assert.False(read.IsCompleted);
        await body.WriteAsync("first\n");

        Assert.Equal("first", await read.WaitAsync(Bound));
    }

    // Lines alone do not start the limit: the provider does, when the model has
    // generated something.
    [Fact]
    public async Task Silence_after_lines_that_are_not_the_reply_is_not_cut_off()
    {
        var body = new OpenBody();
        await body.WriteAsync(": connected\nevent: ping\n");
        using var lines = new StreamLineReader(body.Stream, Limit);
        Assert.Equal(": connected", await lines.ReadLineAsync(CancellationToken.None));
        Assert.Equal("event: ping", await lines.ReadLineAsync(CancellationToken.None));

        var read = lines.ReadLineAsync(CancellationToken.None);
        await Task.Delay(Limit * 3);
        Assert.False(read.IsCompleted);
        await body.WriteAsync("data: x\n");

        Assert.Equal("data: x", await read.WaitAsync(Bound));
    }

    // A line that takes longer than the limit to complete, but never goes quiet
    // for that long, is a stream that is alive.
    [Fact]
    public async Task Bytes_of_an_unfinished_line_keep_the_stream_alive()
    {
        var body = new OpenBody();
        using var lines = new StreamLineReader(body.Stream, Limit);
        lines.ReplyStarted();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 9; i++)
            {
                await body.WriteAsync("ab");
                await Task.Delay(Limit / 3);
            }
            await body.WriteAsync("\n");
        });

        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 9)), await lines.ReadLineAsync(CancellationToken.None).WaitAsync(Bound));
        await writer;
    }

    [Fact]
    public async Task A_line_that_stops_arriving_part_way_ends_after_the_limit()
    {
        var body = new OpenBody();
        using var lines = new StreamLineReader(body.Stream, Limit);
        lines.ReplyStarted();
        await body.WriteAsync("data: {\"cho");

        await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => lines.ReadLineAsync(CancellationToken.None).WaitAsync(Bound));
    }

    [Fact]
    public async Task Keepalives_inside_the_limit_keep_a_long_stream_alive()
    {
        var body = new OpenBody();
        using var lines = new StreamLineReader(body.Stream, Limit);
        await body.WriteAsync("data: {}\n");
        Assert.Equal("data: {}", await lines.ReadLineAsync(CancellationToken.None));
        lines.ReplyStarted();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(Limit / 3);
                await body.WriteAsync(": keepalive\n");
            }
            await body.WriteAsync("data: [DONE]\n");
            await body.CompleteAsync();
        });

        var seen = new List<string>();
        while (await lines.ReadLineAsync(CancellationToken.None).WaitAsync(Bound) is { } line) seen.Add(line);
        await writer;

        Assert.Equal(6, seen.Count(l => l == ": keepalive"));
        Assert.Equal("data: [DONE]", seen[^1]);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_reported_as_a_stall()
    {
        var body = new OpenBody();
        await body.WriteAsync("first\n");
        using var lines = new StreamLineReader(body.Stream, TimeSpan.FromSeconds(30));
        await lines.ReadLineAsync(CancellationToken.None);
        lines.ReplyStarted();
        using var cts = new CancellationTokenSource(Limit);

        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lines.ReadLineAsync(cts.Token).WaitAsync(Bound));
        Assert.IsNotType<LlmStreamStalledException>(e);
    }

    [Fact]
    public async Task A_limit_of_zero_turns_the_check_off()
    {
        var body = new OpenBody();
        await body.WriteAsync("first\n");
        using var lines = new StreamLineReader(body.Stream, TimeSpan.Zero);
        await lines.ReadLineAsync(CancellationToken.None);
        lines.ReplyStarted();
        using var cts = new CancellationTokenSource(Limit * 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lines.ReadLineAsync(cts.Token).WaitAsync(Bound));
        Assert.True(cts.IsCancellationRequested);
    }

    // ── OpenAI-compatible ───────────────────────────────────────────────────

    private const string OpenAiRole = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n";

    private static IAsyncEnumerable<ChatStreamEvent> OpenAi(OpenBody body)
        => new OpenAiProvider(new HttpClient(new BodyHandler(body)), "k", "https://ring.example", streamIdle: Limit)
            .ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None);

    // The case this exists for: most of the answer arrives, then the rest of the
    // stream (its last text, finish_reason, usage, [DONE]) never does and the
    // connection stays open.
    [Fact]
    public async Task OpenAi_ends_a_reply_whose_tail_never_arrives_with_the_text_it_had()
    {
        var body = new OpenBody();
        await body.WriteAsync(OpenAiRole);
        await body.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"por sitio/estado/f\"}}]}\n\n");

        var text = new StringBuilder();
        await Assert.ThrowsAsync<LlmStreamStalledException>(async () =>
        {
            await foreach (var e in OpenAi(body).WithCancellation(CancellationToken.None))
                if (e.Type == "text_delta") text.Append(e.TextDelta);
        }).WaitAsync(Bound);

        Assert.Equal("por sitio/estado/f", text.ToString());
    }

    // A server that names the role at once and then reads a long prompt for longer
    // than the limit has not gone silent part-way through a reply.
    [Fact]
    public async Task OpenAi_opening_chunk_then_a_long_prompt_read_is_not_cut_off()
    {
        var body = new OpenBody();
        await body.WriteAsync(OpenAiRole);
        var reply = Text(OpenAi(body));

        await Task.Delay(Limit * 3);
        Assert.False(reply.IsCompleted);
        await body.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"hola\"}}]}\n\n");
        await body.WriteAsync("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        await body.CompleteAsync();

        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    [Theory]
    [InlineData("{\"reasoning_content\":\"thinking\"}")]
    [InlineData("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"whoami\",\"arguments\":\"\"}}]}")]
    public async Task OpenAi_reasoning_or_a_tool_call_also_starts_the_limit(string delta)
    {
        var body = new OpenBody();
        await body.WriteAsync(OpenAiRole);
        await body.WriteAsync("data: {\"choices\":[{\"delta\":" + delta + "}]}\n\n");

        await Assert.ThrowsAsync<LlmStreamStalledException>(() => Text(OpenAi(body))).WaitAsync(Bound);
    }

    // ── Anthropic ───────────────────────────────────────────────────────────

    private const string AnthropicStart =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":5}}}\n\n"
        + "event: ping\ndata: {\"type\":\"ping\"}\n\n";

    private const string AnthropicText =
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
        + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hola\"}}\n\n";

    private static IAsyncEnumerable<ChatStreamEvent> Anthropic(OpenBody body)
        => new AnthropicProvider(new HttpClient(new BodyHandler(body)), "k", "https://api.example", streamIdle: Limit)
            .ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None);

    [Fact]
    public async Task Anthropic_ends_a_reply_that_goes_silent()
    {
        var body = new OpenBody();
        await body.WriteAsync(AnthropicStart + AnthropicText);

        await Assert.ThrowsAsync<LlmStreamStalledException>(() => Text(Anthropic(body))).WaitAsync(Bound);
    }

    [Fact]
    public async Task Anthropic_silence_before_the_first_content_block_is_not_cut_off()
    {
        var body = new OpenBody();
        await body.WriteAsync(AnthropicStart);
        var reply = Text(Anthropic(body));

        await Task.Delay(Limit * 3);
        Assert.False(reply.IsCompleted);
        await body.WriteAsync(AnthropicText
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        await body.CompleteAsync();

        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    private const string AnthropicEnd =
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
        + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
        + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    [Theory]
    // A text block that opens empty, followed by an empty delta.
    [InlineData("{\"type\":\"text\",\"text\":\"\"}", "{\"type\":\"text_delta\",\"text\":\"\"}")]
    // A thinking block that opens empty.
    [InlineData("{\"type\":\"thinking\",\"thinking\":\"\"}", "{\"type\":\"thinking_delta\",\"thinking\":\"\"}")]
    // A tool call whose input has not started to arrive: the API holds each
    // parameter back until it is whole.
    [InlineData("{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"lookup\",\"input\":{}}", "{\"type\":\"input_json_delta\",\"partial_json\":\"\"}")]
    public async Task Anthropic_an_empty_block_opening_does_not_start_the_limit(string block, string emptyDelta)
    {
        var body = new OpenBody();
        await body.WriteAsync(AnthropicStart
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":" + block + "}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":" + emptyDelta + "}\n\n");
        var reply = Text(Anthropic(body));

        await Task.Delay(Limit * 3);
        Assert.False(reply.IsCompleted);
        await body.WriteAsync(
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + AnthropicText.Replace("\"index\":0", "\"index\":1") + AnthropicEnd.Replace("\"index\":0", "\"index\":1"));
        await body.CompleteAsync();

        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    [Theory]
    [InlineData("{\"type\":\"thinking_delta\",\"thinking\":\"Let me see.\"}")]
    [InlineData("{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"q\\\":\"}")]
    public async Task Anthropic_thinking_or_a_piece_of_a_tool_call_starts_the_limit(string delta)
    {
        var body = new OpenBody();
        await body.WriteAsync(AnthropicStart
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":" + delta + "}\n\n");

        await Assert.ThrowsAsync<LlmStreamStalledException>(() => Text(Anthropic(body))).WaitAsync(Bound);
    }

    // ── Gemini and Ollama ───────────────────────────────────────────────────

    [Fact]
    public async Task Gemini_ends_a_reply_that_goes_silent()
    {
        var body = new OpenBody();
        await body.WriteAsync("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hola\"}]}}]}\n\n");
        var provider = new GeminiProvider(new HttpClient(new BodyHandler(body)), "k", "https://gemini.example", streamIdle: Limit);

        await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None))).WaitAsync(Bound);
    }

    [Theory]
    [InlineData("{\"text\":\"\"}")]
    [InlineData("{}")]
    [InlineData("{\"text\":\"\",\"thought\":true}")]
    public async Task Gemini_an_empty_opening_part_does_not_start_the_limit(string part)
    {
        var body = new OpenBody();
        await body.WriteAsync("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + part + "]}}]}\n\n");
        var provider = new GeminiProvider(new HttpClient(new BodyHandler(body)), "k", "https://gemini.example", streamIdle: Limit);
        var reply = Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None));

        await Task.Delay(Limit * 3);
        Assert.False(reply.IsCompleted);
        await body.WriteAsync("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hola\"}]},\"finishReason\":\"STOP\"}]}\n\n");
        await body.CompleteAsync();

        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    [Theory]
    [InlineData("{\"text\":\"Thinking it over.\",\"thought\":true}")]
    [InlineData("{\"functionCall\":{\"name\":\"lookup\",\"args\":{}}}")]
    public async Task Gemini_a_thought_or_a_function_call_starts_the_limit(string part)
    {
        var body = new OpenBody();
        await body.WriteAsync("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + part + "]}}]}\n\n");
        var provider = new GeminiProvider(new HttpClient(new BodyHandler(body)), "k", "https://gemini.example", streamIdle: Limit);

        await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None))).WaitAsync(Bound);
    }

    [Fact]
    public async Task Ollama_ends_a_reply_that_goes_silent()
    {
        var body = new OpenBody();
        await body.WriteAsync("{\"message\":{\"role\":\"assistant\",\"content\":\"hola\"},\"done\":false}\n");
        var provider = new OllamaProvider(new HttpClient(new BodyHandler(body)), "", "http://ollama.example", streamIdle: Limit);

        await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None))).WaitAsync(Bound);
    }

    [Fact]
    public async Task Ollama_an_empty_opening_chunk_does_not_start_the_limit()
    {
        var body = new OpenBody();
        await body.WriteAsync("{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":false}\n");
        var provider = new OllamaProvider(new HttpClient(new BodyHandler(body)), "", "http://ollama.example", streamIdle: Limit);
        var reply = Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None));

        await Task.Delay(Limit * 3);
        Assert.False(reply.IsCompleted);
        await body.WriteAsync("{\"message\":{\"role\":\"assistant\",\"content\":\"hola\"},\"done\":false}\n");
        await body.WriteAsync("{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"prompt_eval_count\":3,\"eval_count\":1}\n");
        await body.CompleteAsync();

        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    [Fact]
    public void The_default_is_sixty_seconds_and_the_setting_says_so()
    {
        Assert.Equal(60, new nashira_backend.Configuration.AiChatOptions().StreamIdleSeconds);
    }
}
