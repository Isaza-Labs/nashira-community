using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Configuration;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Providers;
using nashira_backend.Services.Identity;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

// With the idle limit set, a reply that goes silent part-way through ends after it
// instead of holding the turn until its deadline. The limit is off unless a provider's
// Config or the server-wide setting turns it on. Everything else behaves as before: a server
// that is slow to start (whatever it sends first), a line that arrives in pieces,
// keepalives and the caller's own cancellation.
//
// Every wait is bounded, so a change that stops the limit from firing fails these
// tests instead of hanging them.
//
// A test that says the limit does NOT apply somewhere does not wait and see: it looks
// at the token the waiting read was given (GatedBody.ReadToken). The caller's own token
// means the read is under no limit and no timer exists; that holds however slowly the
// test machine runs. A test about WHEN the limit runs out gives the reader a clock it
// moves by hand (ManualTime) and a body it feeds byte by byte, so it can stop a
// millisecond short of the limit and step over it. StreamIdleEnabledPathTests has the
// limit switched on against cancellation, the turn deadline, retries and the
// setting's sources.
public class StreamIdleLimitTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // The limit in the tests that move the clock by hand, and a step short of it.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AlmostWait = TimeSpan.FromSeconds(27);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);

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

    // A provider of this type, with the limit on, is sent `opening` and nothing more.
    // Once it has read all of it and is waiting, that read must be under the caller's
    // own token: the opening did not start the limit. Then the rest arrives and the
    // reply is returned.
    private static async Task<string> ReplyAfterAnOpeningThatDoesNotStartTheLimit(string type, string opening, string rest)
    {
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        // An hour: the rest of the reply is read under it, and must not be able to stall.
        var provider = IdleWire.Provider(type, new GatedServer().Then(body).CreateClient(), TimeSpan.FromHours(1));

        body.Feed(opening);
        var reply = Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, caller.Token));
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal(caller.Token, body.ReadToken);

        body.Feed(rest);
        body.End();
        return await reply.WaitAsync(Bound);
    }

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
        Assert.True(watch.Elapsed >= Limit * 0.8, $"ended after {watch.Elapsed}, before the limit");
    }

    [Fact]
    public async Task Silence_before_anything_arrives_is_under_no_limit()
    {
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        using var lines = new StreamLineReader(body, Limit);

        var read = lines.ReadLineAsync(caller.Token);
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal(caller.Token, body.ReadToken);
        body.Feed("first\n");

        Assert.Equal("first", await read.WaitAsync(Bound));
    }

    // Lines alone do not start the limit: the provider does, when the model has
    // generated something.
    [Fact]
    public async Task Silence_after_lines_that_are_not_the_reply_is_under_no_limit()
    {
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        body.Feed(": connected\nevent: ping\n");
        using var lines = new StreamLineReader(body, Limit);
        Assert.Equal(": connected", await lines.ReadLineAsync(caller.Token));
        Assert.Equal("event: ping", await lines.ReadLineAsync(caller.Token));

        var read = lines.ReadLineAsync(caller.Token);
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal(caller.Token, body.ReadToken);
        body.Feed("data: x\n");

        Assert.Equal("data: x", await read.WaitAsync(Bound));
    }

    // Once the provider says the reply has started, the next read is under the limit:
    // it is given a token of the limit's own instead of the caller's, and the limit's
    // timer is set.
    [Fact]
    public async Task A_read_after_the_reply_started_is_under_the_limit()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(caller.Token);
        await body.Waiting().WaitAsync(Bound);
        Assert.NotEqual(caller.Token, body.ReadToken);
        Assert.True(body.ReadToken.CanBeCanceled);
        Assert.Equal([Wait], time.Due);
        body.Feed("data: x\n");

        Assert.Equal("data: x", await read.WaitAsync(Bound));
    }

    // ── the reader, on a clock moved by hand ────────────────────────────────

    [Fact]
    public async Task A_silent_reply_ends_at_the_limit_and_not_a_moment_before()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal([Wait], time.Due);

        time.Advance(Wait - Tick);
        Assert.Equal(0, body.ReadsCancelled);
        time.Advance(Tick);
        Assert.Equal(1, body.ReadsCancelled);

        var e = await Assert.ThrowsAsync<LlmStreamStalledException>(() => read.WaitAsync(Bound));
        Assert.Equal(Wait, e.Limit);
        Assert.Equal(0, time.Live);
    }

    // Until the reply has started, and with the limit at zero after it has, there is
    // no timer at all: any amount of silence passes.
    [Theory]
    [InlineData(30, false)]
    [InlineData(0, true)]
    public async Task With_no_reply_started_or_no_limit_there_is_no_timer(int limitSeconds, bool replyStarted)
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, TimeSpan.FromSeconds(limitSeconds), time);
        if (replyStarted) lines.ReplyStarted();
        body.Feed(": connected\n");
        Assert.Equal(": connected", await lines.ReadLineAsync(CancellationToken.None));

        var read = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        time.Advance(TimeSpan.FromDays(2));
        body.Feed("data: x\n");

        Assert.Equal("data: x", await read.WaitAsync(Bound));
        Assert.Equal(0, time.Created);
        Assert.Equal(0, body.ReadsCancelled);
    }

    // A line that takes far longer than the limit to complete, but never goes quiet
    // for that long, is a stream that is alive: every piece puts the whole limit back.
    [Fact]
    public async Task Bytes_of_an_unfinished_line_keep_the_stream_alive()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        for (var i = 0; i < 18; i++)
        {
            time.Advance(AlmostWait);
            Assert.Equal([Wait - AlmostWait], time.Due);
            body.Feed("ab");
            await body.Waiting().WaitAsync(Bound);
            Assert.Equal([Wait], time.Due);
        }
        time.Advance(AlmostWait);
        body.Feed("\n");

        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 18)), await read.WaitAsync(Bound));
        Assert.Equal(0, body.ReadsCancelled);
    }

    // The limit counts from the last bytes, not from when the line began.
    [Fact]
    public async Task A_line_that_stops_arriving_part_way_ends_one_limit_after_its_last_bytes()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        time.Advance(AlmostWait);
        body.Feed("data: {\"cho");
        await body.Waiting().WaitAsync(Bound);

        time.Advance(Wait - Tick);
        Assert.Equal(0, body.ReadsCancelled);
        time.Advance(Tick);
        Assert.Equal(1, body.ReadsCancelled);

        await Assert.ThrowsAsync<LlmStreamStalledException>(() => read.WaitAsync(Bound));
    }

    // Every read after the reply started gets the whole limit, whatever the line turns
    // out to be: a keepalive comment is not generated output, and it still shows the
    // stream is alive. No timer is left running between one read and the next.
    [Fact]
    public async Task Keepalives_inside_the_limit_keep_a_long_stream_alive()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, Wait, time);
        body.Feed("data: {}\n");
        Assert.Equal("data: {}", await lines.ReadLineAsync(CancellationToken.None));
        lines.ReplyStarted();

        for (var i = 0; i < 12; i++)
        {
            var read = lines.ReadLineAsync(CancellationToken.None);
            await body.Waiting().WaitAsync(Bound);
            Assert.Equal([Wait], time.Due);
            time.Advance(AlmostWait);
            body.Feed(": keepalive\n");
            Assert.Equal(": keepalive", await read.WaitAsync(Bound));
            Assert.Equal(0, time.Live);
        }
        body.Feed("data: [DONE]\n");
        body.End();

        Assert.Equal("data: [DONE]", await lines.ReadLineAsync(CancellationToken.None).WaitAsync(Bound));
        Assert.Null(await lines.ReadLineAsync(CancellationToken.None).WaitAsync(Bound));
        Assert.Equal(0, body.ReadsCancelled);
        Assert.Equal(0, time.Live);
    }

    // The limit is on waiting for the server. Time the caller spends on a line it was
    // given is not counted: the next read starts with the whole limit.
    [Fact]
    public async Task Time_between_two_reads_does_not_count_against_the_limit()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();
        body.Feed("data: one\n");
        Assert.Equal("data: one", await lines.ReadLineAsync(CancellationToken.None).WaitAsync(Bound));
        Assert.Equal(0, time.Live);

        time.Advance(Wait * 5);
        var read = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal([Wait], time.Due);
        Assert.Equal(0, body.ReadsCancelled);
        body.Feed("data: two\n");

        Assert.Equal("data: two", await read.WaitAsync(Bound));
    }

    // The caller cancels while the limit is running, and the limit's time then passes
    // anyway: a cancellation, and the timer went with the read.
    [Fact]
    public async Task A_cancelled_read_leaves_no_timer_to_run_out_later()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(caller.Token);
        await body.Waiting().WaitAsync(Bound);
        time.Advance(AlmostWait);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound));
        Assert.Equal(0, time.Live);
        time.Advance(Wait * 2);
        Assert.Equal(1, body.ReadsCancelled);
    }

    // A caller that has already cancelled when the read begins gets its cancellation,
    // and no timer is left behind.
    [Fact]
    public async Task A_caller_already_cancelled_when_the_read_begins_is_a_cancellation()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lines.ReadLineAsync(caller.Token).WaitAsync(Bound));
        Assert.Equal(0, time.Live);
        time.Advance(Wait * 2);
    }

    // The caller cancels, and before the cancelled read has ended the limit's time
    // passes too. The timer is a source of its own and fires; the caller cancelled
    // first, and it is still the caller's cancellation.
    [Fact]
    public async Task A_limit_that_runs_out_just_after_the_caller_cancelled_is_not_reported_as_a_stall()
    {
        var time = new ManualTime();
        using var caller = new CancellationTokenSource();
        var body = new GatedBody { OnReadCancelled = () => time.Advance(Wait * 2) };
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(caller.Token);
        await body.Waiting().WaitAsync(Bound);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound));
        Assert.Equal(1, body.ReadsCancelled);
        Assert.Equal(0, time.Live);
    }

    // A limit with a fraction of a second runs for that long, to the millisecond, and
    // bytes put the same amount back.
    [Fact]
    public async Task A_limit_with_a_fraction_of_a_second_runs_for_exactly_that_long()
    {
        var limit = TimeSpan.FromSeconds(2.5);
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, limit, time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal([limit], time.Due);
        time.Advance(TimeSpan.FromSeconds(2));
        body.Feed("da");
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal([limit], time.Due);

        time.Advance(limit - Tick);
        Assert.Equal(0, body.ReadsCancelled);
        time.Advance(Tick);
        Assert.Equal(1, body.ReadsCancelled);

        var e = await Assert.ThrowsAsync<LlmStreamStalledException>(() => read.WaitAsync(Bound));
        Assert.Equal(limit, e.Limit);
    }

    // A limit longer than a day is a day, not no limit.
    [Fact]
    public async Task A_limit_longer_than_a_day_runs_as_a_day()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, TimeSpan.FromSeconds(5_000_000), time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal([TimeSpan.FromDays(1)], time.Due);

        time.Advance(TimeSpan.FromDays(1));
        var e = await Assert.ThrowsAsync<LlmStreamStalledException>(() => read.WaitAsync(Bound));
        Assert.Equal(TimeSpan.FromDays(1), e.Limit);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_reported_as_a_stall()
    {
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        body.Feed("first\n");
        using var lines = new StreamLineReader(body, TimeSpan.FromHours(1));
        await lines.ReadLineAsync(caller.Token);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(caller.Token);
        await body.Waiting().WaitAsync(Bound);
        Assert.NotEqual(caller.Token, body.ReadToken);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound));
    }

    // The limit runs out and the caller cancels before the reader has looked at why its
    // read ended: still the caller's cancellation.
    [Fact]
    public async Task A_caller_that_cancels_just_as_the_limit_runs_out_is_not_reported_as_a_stall()
    {
        using var caller = new CancellationTokenSource();
        var body = new GatedBody { OnReadCancelled = caller.Cancel };
        using var lines = new StreamLineReader(body, Limit);
        lines.ReplyStarted();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lines.ReadLineAsync(caller.Token).WaitAsync(Bound));
        Assert.Equal(1, body.ReadsCancelled);
    }

    // Zero is no limit at all: the read is the caller's, as it was before the limit
    // existed, and nothing but the caller ends it.
    [Fact]
    public async Task A_limit_of_zero_turns_the_check_off()
    {
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        body.Feed("first\n");
        using var lines = new StreamLineReader(body, TimeSpan.Zero);
        await lines.ReadLineAsync(caller.Token);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(caller.Token);
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal(caller.Token, body.ReadToken);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Bound));
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
    public async Task OpenAi_opening_chunk_then_a_long_prompt_read_is_under_no_limit()
    {
        Assert.Equal("hola", await ReplyAfterAnOpeningThatDoesNotStartTheLimit(
            "custom",
            OpenAiRole,
            "data: {\"choices\":[{\"delta\":{\"content\":\"hola\"}}]}\n\n"
            + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
    }

    [Theory]
    [InlineData("{\"reasoning_content\":\"thinking\"}")]
    [InlineData("{\"reasoning\":\"thinking\"}")]
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
    public async Task Anthropic_silence_before_the_first_content_block_is_under_no_limit()
    {
        Assert.Equal("hola", await ReplyAfterAnOpeningThatDoesNotStartTheLimit(
            "anthropic", AnthropicStart, AnthropicText + AnthropicEnd));
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
        Assert.Equal("hola", await ReplyAfterAnOpeningThatDoesNotStartTheLimit(
            "anthropic",
            AnthropicStart
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":" + block + "}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":" + emptyDelta + "}\n\n",
            "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + AnthropicText.Replace("\"index\":0", "\"index\":1") + AnthropicEnd.Replace("\"index\":0", "\"index\":1")));
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
        Assert.Equal("hola", await ReplyAfterAnOpeningThatDoesNotStartTheLimit(
            "gemini",
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + part + "]}}]}\n\n",
            "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hola\"}]},\"finishReason\":\"STOP\"}]}\n\n"));
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
        Assert.Equal("hola", await ReplyAfterAnOpeningThatDoesNotStartTheLimit(
            "ollama",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":false}\n",
            "{\"message\":{\"role\":\"assistant\",\"content\":\"hola\"},\"done\":false}\n"
            + "{\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"prompt_eval_count\":3,\"eval_count\":1}\n"));
    }

    [Theory]
    [InlineData("{\"role\":\"assistant\",\"content\":\"\",\"thinking\":\"Let me see.\"}")]
    [InlineData("{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"lookup\",\"arguments\":{}}}]}")]
    public async Task Ollama_thinking_or_a_tool_call_starts_the_limit(string message)
    {
        var body = new OpenBody();
        await body.WriteAsync("{\"message\":" + message + ",\"done\":false}\n");
        var provider = new OllamaProvider(new HttpClient(new BodyHandler(body)), "", "http://ollama.example", streamIdle: Limit);

        await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None))).WaitAsync(Bound);
    }

    // ── what the reader leaves alone ────────────────────────────────────────

    [Fact]
    public async Task A_character_split_across_two_reads_is_decoded_whole()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();
        var bytes = Encoding.UTF8.GetBytes("señal ✓\n");
        var cut = Array.IndexOf(bytes, (byte)0xC3) + 1; // between the two bytes of "ñ"

        body.Feed(bytes[..cut]);
        var line = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        time.Advance(AlmostWait);
        body.Feed(bytes[cut..]);

        Assert.Equal("señal ✓", await line.WaitAsync(Bound));
        Assert.Equal(0, body.ReadsCancelled);
    }

    [Fact]
    public async Task A_broken_connection_is_reported_as_what_it_was_not_as_a_stall()
    {
        var time = new ManualTime();
        var body = new GatedBody();
        using var lines = new StreamLineReader(body, Wait, time);
        lines.ReplyStarted();

        var read = lines.ReadLineAsync(CancellationToken.None);
        await body.Waiting().WaitAsync(Bound);
        body.Break(new IOException("the connection was reset"));

        var error = await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(Bound));
        Assert.Equal("the connection was reset", error.Message);
        Assert.Equal(0, time.Live);
    }

    [Theory]
    [InlineData(60, "60 s")]
    [InlineData(2.5, "2.5 s")]
    [InlineData(1.25, "1.25 s")]
    [InlineData(1, "1 s")]
    public void The_message_names_the_limit_as_it_was_set(double seconds, string shown)
    {
        Assert.Contains($"stopped sending for {shown} ", new LlmStreamStalledException(TimeSpan.FromSeconds(seconds)).Message);
    }

    [Fact]
    public async Task A_limit_longer_than_a_timer_accepts_does_not_break_the_stream()
    {
        var body = new OpenBody();
        using var lines = new StreamLineReader(body.Stream, TimeSpan.FromSeconds(5_000_000));
        lines.ReplyStarted();
        await body.WriteAsync("data: one\n");

        Assert.Equal("data: one", await lines.ReadLineAsync(CancellationToken.None).WaitAsync(Bound));
    }

    // ── the setting ─────────────────────────────────────────────────────────

    [Fact]
    public void The_limit_is_off_unless_it_is_set()
    {
        Assert.Equal(0, new AiChatOptions().StreamIdleSeconds);
    }

    [Theory]
    [InlineData("""{"stream_idle_seconds":60}""", 60.0)]
    [InlineData("""{"stream_idle_seconds":0}""", 0.0)]
    [InlineData("""{"stream_idle_seconds":2.5}""", 2.5)]
    [InlineData("""{"stream_idle_seconds":1}""", 1.0)]
    [InlineData("""{"stream_idle_seconds":86400}""", 86400.0)]
    [InlineData("""{"models":["m2"]}""", null)]
    [InlineData("""{"stream_idle_seconds":"60"}""", null)]
    [InlineData("""{"stream_idle_seconds":true}""", null)]
    [InlineData("""{"stream_idle_seconds":-1}""", null)]
    [InlineData("""{"stream_idle_seconds":0.3}""", null)]
    [InlineData("""{"stream_idle_seconds":1e-100}""", null)]
    [InlineData("""{"stream_idle_seconds":1e-400}""", null)]
    [InlineData("""{"stream_idle_seconds":-1e-400}""", null)]
    [InlineData("""{"stream_idle_seconds":0.0}""", 0.0)]
    [InlineData("""{"stream_idle_seconds":0e5}""", 0.0)]
    [InlineData("""{"stream_idle_seconds":-0}""", 0.0)]
    [InlineData("""{"stream_idle_seconds":86401}""", null)]
    [InlineData("""{"stream_idle_seconds":1e12}""", null)]
    public void A_providers_own_limit_is_read_from_its_config(string config, double? seconds)
    {
        var limit = LlmProviderFactory.ConfigStreamIdle(JsonDocument.Parse(config).RootElement);

        Assert.Equal(seconds, limit?.TotalSeconds);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(60, 60)]
    [InlineData(-5, 0)]
    [InlineData(5_000_000, 86_400)]
    [InlineData(int.MaxValue, 86_400)]
    public void The_server_wide_limit_is_brought_into_range(int setting, double seconds)
    {
        Assert.Equal(seconds, LlmProviderFactory.ServerStreamIdle(setting).TotalSeconds);
    }

    // The setting as an environment variable or appsettings would supply it: text.
    [Theory]
    [InlineData("2147483648")]
    [InlineData("2.5")]
    [InlineData("NaN")]
    [InlineData("sixty")]
    public void A_server_wide_limit_that_is_not_a_whole_number_stops_startup_and_names_the_setting(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => LlmProviderFactory.CheckServerStreamIdle(ServerSetting(value)));

        Assert.Contains("AiChat:StreamIdleSeconds", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("60")]
    [InlineData("-5")]
    [InlineData("5000000")]
    public void A_server_wide_limit_that_is_a_whole_number_or_absent_starts(string? value)
    {
        LlmProviderFactory.CheckServerStreamIdle(ServerSetting(value));
    }

    // What happens to the setting in a running server: Program.cs registers the chat
    // options on the configuration, runs the check, and the first chat request binds
    // the options. For every value the check lets through, that binding must work and
    // give this number of seconds. A blank is the setting left out.
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData(" ", 0)]
    [InlineData("\t", 0)]
    [InlineData(" \r\n ", 0)]
    [InlineData("0", 0)]
    [InlineData("60", 60)]
    [InlineData(" 60 ", 60)]
    [InlineData("-5", -5)]
    [InlineData("+7", 7)]
    [InlineData("0x10", 16)]
    [InlineData("5000000", 5_000_000)]
    [InlineData("2147483647", int.MaxValue)]
    public void A_server_wide_limit_the_check_lets_through_is_what_chat_then_reads(string? value, int seconds)
    {
        var configuration = ServerSetting(value);
        using var services = ChatOptionsRegisteredOn(configuration);

        LlmProviderFactory.CheckServerStreamIdle(configuration);

        Assert.Equal(seconds, ChatOptions(services).StreamIdleSeconds);
    }

    // The check stops the server only on what would have failed every chat request:
    // each of these also fails when the chat options are bound without the check.
    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("2.5")]
    [InlineData("60.0")]
    [InlineData("1e3")]
    [InlineData("NaN")]
    [InlineData("sixty")]
    [InlineData("true")]
    public void A_server_wide_limit_the_check_refuses_would_have_failed_chat(string value)
    {
        var configuration = ServerSetting(value);
        using var services = ChatOptionsRegisteredOn(configuration);

        var error = Assert.Throws<InvalidOperationException>(() => LlmProviderFactory.CheckServerStreamIdle(configuration));

        Assert.Contains("AiChat:StreamIdleSeconds", error.Message);
        Assert.Throws<InvalidOperationException>(() => ChatOptions(services));
    }

    // The same from appsettings, where a value has a shape. Blank and null are the
    // setting left out; a number written as a number or as text is that number.
    [Theory]
    [InlineData("\"\"", 0)]
    [InlineData("\"   \"", 0)]
    [InlineData("null", 0)]
    [InlineData("60", 60)]
    [InlineData("\"60\"", 60)]
    public void A_server_wide_limit_in_appsettings_is_read_the_same_way(string json, int seconds)
    {
        var configuration = AppSettings("{\"AiChat\":{\"StreamIdleSeconds\":" + json + ",\"StreamDeadlineSeconds\":90}}");
        using var services = ChatOptionsRegisteredOn(configuration);

        LlmProviderFactory.CheckServerStreamIdle(configuration);

        var options = ChatOptions(services);
        Assert.Equal(seconds, options.StreamIdleSeconds);
        Assert.Equal(90, options.StreamDeadlineSeconds);
    }

    // A list or an object where the number belongs binds as 0 without a word, which
    // would switch off a limit someone asked for. The check stops the server on it.
    [Theory]
    [InlineData("[60]")]
    [InlineData("[60, 120]")]
    [InlineData("{\"seconds\":60}")]
    public void A_server_wide_limit_written_as_a_list_or_an_object_stops_startup_and_names_the_setting(string json)
    {
        var configuration = AppSettings("{\"AiChat\":{\"StreamIdleSeconds\":" + json + "}}");
        using var services = ChatOptionsRegisteredOn(configuration);

        var error = Assert.Throws<InvalidOperationException>(() => LlmProviderFactory.CheckServerStreamIdle(configuration));

        Assert.Contains("AiChat:StreamIdleSeconds", error.Message);
        Assert.Equal(0, ChatOptions(services).StreamIdleSeconds);
    }

    // Configuration is layered, and the value that counts is the one that wins. A blank
    // environment variable over a number in appsettings is blank, so the limit is off;
    // a number over a blank is that number.
    [Theory]
    [InlineData("60", "", 0)]
    [InlineData("", "60", 60)]
    [InlineData("", " ", 0)]
    public void A_blank_value_counts_where_it_is_the_value_that_wins(string inAppSettings, string inEnvironment, int seconds)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
                "{\"AiChat\":{\"StreamIdleSeconds\":" + JsonSerializer.Serialize(inAppSettings) + "}}")))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AiChat:StreamIdleSeconds"] = inEnvironment })
            .Build();
        using var services = ChatOptionsRegisteredOn(configuration);

        LlmProviderFactory.CheckServerStreamIdle(configuration);

        Assert.Equal(seconds, ChatOptions(services).StreamIdleSeconds);
    }

    // The chat options registered the way Program.cs registers them. Nothing is bound
    // until ChatOptions asks, as nothing is in the server until the first chat request.
    private static Microsoft.Extensions.DependencyInjection.ServiceProvider ChatOptionsRegisteredOn(IConfiguration configuration)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.OptionsConfigurationServiceCollectionExtensions.Configure<AiChatOptions>(
            services, configuration.GetSection(AiChatOptions.SectionName));
        return Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
    }

    private static AiChatOptions ChatOptions(IServiceProvider services) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IOptions<AiChatOptions>>(services).Value;

    private static IConfiguration AppSettings(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

    private static IConfiguration ServerSetting(string? value)
    {
        var settings = new Dictionary<string, string?>();
        if (value is not null) settings["AiChat:StreamIdleSeconds"] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    [Theory]
    [InlineData("""{"stream_idle_seconds":60}""")]
    [InlineData("""{"stream_idle_seconds":0}""")]
    [InlineData("""{"stream_idle_seconds":2.5,"models":["m2"]}""")]
    [InlineData("""{"stream_idle_seconds":86400}""")]
    [InlineData("""{"models":["m2"]}""")]
    public void Saving_a_provider_accepts_a_limit_in_seconds(string config)
    {
        var saved = AIProviderController.NormalizeConfig(JsonDocument.Parse(config).RootElement);

        Assert.Equal(JsonDocument.Parse(config).RootElement.GetRawText(), saved.GetRawText());
    }

    [Theory]
    [InlineData("""{"stream_idle_seconds":"60"}""")]
    [InlineData("""{"stream_idle_seconds":true}""")]
    [InlineData("""{"stream_idle_seconds":-1}""")]
    [InlineData("""{"stream_idle_seconds":null}""")]
    [InlineData("""{"stream_idle_seconds":0.3}""")]
    [InlineData("""{"stream_idle_seconds":1e-100}""")]
    [InlineData("""{"stream_idle_seconds":1e-400}""")]
    [InlineData("""{"stream_idle_seconds":-1e-400}""")]
    [InlineData("""{"stream_idle_seconds":86401}""")]
    [InlineData("""{"stream_idle_seconds":1e12}""")]
    public void Saving_a_provider_refuses_a_limit_that_would_be_ignored(string config)
    {
        var error = Assert.ThrowsAny<Exception>(
            () => AIProviderController.NormalizeConfig(JsonDocument.Parse(config).RootElement));

        Assert.Contains("config.stream_idle_seconds", error.Message);
    }

    // What each kind of provider sends once its model has said "hola" and gone silent.
    public static TheoryData<string, string> OpenReplies => new()
    {
        { "custom", "data: {\"choices\":[{\"delta\":{\"content\":\"hola\"}}]}\n\n" },
        { "anthropic", AnthropicStart + AnthropicText },
        { "gemini", "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"hola\"}]}}]}\n\n" },
        { "ollama", "{\"message\":{\"role\":\"assistant\",\"content\":\"hola\"},\"done\":false}\n" },
    };

    [Theory]
    [MemberData(nameof(OpenReplies))]
    public async Task A_stored_provider_with_the_setting_ends_a_silent_reply(string type, string reply)
    {
        var body = new OpenBody();
        await body.WriteAsync(reply);
        var provider = await StoredProviderAsync(type, """{"stream_idle_seconds":1}""", body);

        await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None))).WaitAsync(Bound);
    }

    [Fact]
    public async Task The_server_wide_setting_applies_to_a_provider_with_none_of_its_own()
    {
        var body = new OpenBody();
        await body.WriteAsync(OpenReplies.First()[1].ToString()!);
        var provider = await StoredProviderAsync("custom", "{}", body, serverWideSeconds: 1);

        await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None))).WaitAsync(Bound);
    }

    [Fact]
    public async Task A_server_wide_setting_far_out_of_range_does_not_break_a_healthy_reply()
    {
        var body = new OpenBody();
        await body.WriteAsync(OpenReplies.First()[1].ToString()!);
        var provider = await StoredProviderAsync("custom", "{}", body, serverWideSeconds: 5_000_000);
        var text = Text(provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, CancellationToken.None));

        await body.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\" mundo\"}}]}\n\ndata: [DONE]\n\n");
        await body.CompleteAsync();

        Assert.Equal("hola mundo", await text.WaitAsync(Bound));
    }

    // ── a whole turn ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_turn_whose_reply_goes_silent_keeps_its_text_and_is_stored_as_an_error()
    {
        // The model says something and reports its usage, then the server goes silent
        // with the connection open. The provider's limit is one second; the turn's
        // deadline is the default four minutes.
        var reply = new ScriptedBody(
            RunnerTestKit.Sse(
                "{\"choices\":[{\"delta\":{\"content\":\"Par\"}}]}",
                "{\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5}}"),
            ScriptedBody.Ending.Hang);
        await using var kit = new RunnerTestKit(new ScriptedHttp(reply), providerConfig: """{"stream_idle_seconds":1}""");

        await kit.RunAsync().WaitAsync(Bound);

        Assert.Equal(["Par"], kit.Sink.Text);
        Assert.True(kit.Sink.Errored);
        Assert.Null(kit.Sink.ErrorCode);
        Assert.Contains("stopped sending for 1 s", kit.Sink.ErrorMessage);
        Assert.False(kit.Sink.Done);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusError, turn.Status);
        Assert.Equal("Par", turn.AssistantText);
        Assert.Contains("stopped sending", turn.Error);
        Assert.Equal((100, 5), (turn.TokensIn, turn.TokensOut));
        Assert.Contains(" outcome=failed", Assert.Single(kit.Log.Rounds));
    }

    private static Task<IStreamingToolCallingLlmProvider> StoredProviderAsync(
        string type, string config, OpenBody body, int serverWideSeconds = 0) =>
        StoredIdleProvider.ResolveAsync(type, config, new BodyClients(body), serverWideSeconds);

    private sealed class BodyClients(OpenBody body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new BodyHandler(body));
    }
}
