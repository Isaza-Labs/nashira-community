using System.Net;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// The idle limit switched on, where it meets the rest of a request: the caller's own
// cancellation, the turn deadline, the response being let go of, later requests, the
// retries a request already had, and where the limit's value comes from.
//
// Each test decides its outcome by what happened, not by how long it took. Where a
// limit has to run out, it is a real one and the test waits for the result (up to
// Bound); where a test asks whether a read is under the limit at all, it looks at the
// token that read was given: the caller's own token means no limit and no timer, a
// different one means the limit is running.
public class StreamIdleEnabledPathTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan LongLimit = TimeSpan.FromHours(1);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TurnBound = TimeSpan.FromMinutes(2);

    private static readonly List<LlmMessage> Hi = [new LlmMessage { Role = "user", Content = "hi" }];

    public static TheoryData<string> EveryType => IdleWire.EveryType;

    private static async Task<string> Text(IAsyncEnumerable<ChatStreamEvent> events)
    {
        var text = new System.Text.StringBuilder();
        await foreach (var e in events)
            if (e.Type == "text_delta") text.Append(e.TextDelta);
        return text.ToString();
    }

    private static IAsyncEnumerable<ChatStreamEvent> Ask(IStreamingToolCallingLlmProvider provider, CancellationToken ct) =>
        provider.ChatWithToolsStreamAsync(Hi, [], "m", 0.0, ct);

    // ── which reads the limit is on ─────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task The_limit_runs_once_the_model_has_generated_something_and_not_before(string type)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        var provider = IdleWire.Provider(type, new GatedServer().Then(body).CreateClient(), LongLimit);

        body.Feed(wire.Opening);
        var reply = Text(Ask(provider, caller.Token));
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal(caller.Token, body.ReadToken);

        body.Feed(wire.Said);
        await body.Waiting().WaitAsync(Bound);
        Assert.NotEqual(caller.Token, body.ReadToken);
        Assert.True(body.ReadToken.CanBeCanceled);

        body.Feed(wire.Rest);
        body.End();
        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    // Each request reads with a reader of its own: that the model was answering in the
    // last one does not put the next one's opening silence under the limit.
    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task A_later_request_starts_with_no_limit_running(string type)
    {
        var wire = IdleWire.For(type);
        var first = new GatedBody();
        var second = new GatedBody();
        using var caller = new CancellationTokenSource();
        var provider = IdleWire.Provider(type, new GatedServer().Then(first).Then(second).CreateClient(), LongLimit);

        first.Feed(wire.Opening + wire.Said + wire.Rest);
        first.End();
        Assert.Equal("hola", await Text(Ask(provider, caller.Token)).WaitAsync(Bound));

        second.Feed(wire.Opening);
        var reply = Text(Ask(provider, caller.Token));
        await second.Waiting().WaitAsync(Bound);
        Assert.Equal(caller.Token, second.ReadToken);

        second.Feed(wire.Said + wire.Rest);
        second.End();
        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    // ── the caller's cancellation against the limit ─────────────────────────

    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task The_callers_cancellation_while_the_limit_runs_is_a_cancellation(string type)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        var provider = IdleWire.Provider(type, new GatedServer().Then(body).CreateClient(), LongLimit);

        body.Feed(wire.Opening + wire.Said);
        var reply = Text(Ask(provider, caller.Token));
        await body.Waiting().WaitAsync(Bound);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reply.WaitAsync(Bound));
        Assert.Equal(1, body.ReadsCancelled);
    }

    // The limit runs out and, before the reader has looked at why its read was
    // cancelled, the caller cancels too. That is the caller's cancellation: a turn that
    // was stopped, or whose deadline passed, is not reported as a stalled model.
    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task A_caller_that_cancels_just_as_the_limit_runs_out_is_a_cancellation_not_a_stall(string type)
    {
        var wire = IdleWire.For(type);
        using var caller = new CancellationTokenSource();
        var body = new GatedBody { OnReadCancelled = caller.Cancel };
        var provider = IdleWire.Provider(type, new GatedServer().Then(body).CreateClient(), Limit);

        body.Feed(wire.Opening + wire.Said);
        var reply = Text(Ask(provider, caller.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reply.WaitAsync(Bound));
        Assert.Equal(1, body.ReadsCancelled);
    }

    // ── letting go of the response ──────────────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task A_stalled_reply_closes_the_response_and_is_not_asked_for_again(string type)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        var server = new GatedServer().Then(body);
        var provider = IdleWire.Provider(type, server.CreateClient(), Limit);

        body.Feed(wire.Opening + wire.Said);
        var e = await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(Ask(provider, CancellationToken.None)).WaitAsync(Bound));

        Assert.Equal(Limit, e.Limit);
        Assert.True(body.Disposed);
        Assert.Single(server.Requests);
    }

    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task A_cancelled_reply_closes_the_response(string type)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        var provider = IdleWire.Provider(type, new GatedServer().Then(body).CreateClient(), LongLimit);

        body.Feed(wire.Opening + wire.Said);
        var reply = Text(Ask(provider, caller.Token));
        await body.Waiting().WaitAsync(Bound);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reply.WaitAsync(Bound));

        Assert.True(body.Disposed);
    }

    // The caller stops listening part-way, as the runner does when a turn ends early.
    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task A_reply_the_caller_walks_away_from_closes_the_response(string type)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        var provider = IdleWire.Provider(type, new GatedServer().Then(body).CreateClient(), LongLimit);

        body.Feed(wire.Opening + wire.Said);
        var heard = "";
        await foreach (var e in Ask(provider, CancellationToken.None))
        {
            if (e.Type != "text_delta") continue;
            heard = e.TextDelta;
            break;
        }

        Assert.Equal("hola", heard);
        Assert.True(body.Disposed);
    }

    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task A_finished_reply_closes_the_response(string type)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        var provider = IdleWire.Provider(type, new GatedServer().Then(body).CreateClient(), LongLimit);

        body.Feed(wire.Opening + wire.Said + wire.Rest);
        body.End();

        Assert.Equal("hola", await Text(Ask(provider, CancellationToken.None)).WaitAsync(Bound));
        Assert.True(body.Disposed);
    }

    // ── what is sent ────────────────────────────────────────────────────────

    // The limit is about reading the reply. What goes to the model server is the same
    // with it on or off: method, address, headers and body.
    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task The_request_is_the_same_with_the_limit_on_or_off(string type)
    {
        async Task<string> Sent(TimeSpan limit)
        {
            var wire = IdleWire.For(type);
            var body = new GatedBody();
            var server = new GatedServer().Then(body);
            body.Feed(wire.Opening + wire.Said + wire.Rest);
            body.End();
            var provider = IdleWire.Provider(type, server.CreateClient(), limit);
            Assert.Equal("hola", await Text(Ask(provider, CancellationToken.None)).WaitAsync(Bound));
            return Assert.Single(server.Requests);
        }

        var off = await Sent(TimeSpan.Zero);
        Assert.StartsWith("POST https://", off.Replace("http://", "https://"));
        Assert.Equal(off, await Sent(LongLimit));
    }

    // ── the retries a request already had ───────────────────────────────────

    // An endpoint that refuses stream_options is asked once more without it. The limit
    // is on the reply's stream, so it changes nothing about that.
    [Theory]
    [InlineData(0)]
    [InlineData(3600)]
    public async Task A_refused_stream_options_is_retried_the_same_with_the_limit_on_or_off(int limitSeconds)
    {
        var wire = IdleWire.For("custom");
        var body = new GatedBody();
        var server = new GatedServer()
            .Then(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"stream_options is not supported\"}}")
            .Then(body);
        var provider = IdleWire.Provider("custom", server.CreateClient(), TimeSpan.FromSeconds(limitSeconds));

        body.Feed(wire.Opening + wire.Said + wire.Rest);
        body.End();

        Assert.Equal("hola", await Text(Ask(provider, CancellationToken.None)).WaitAsync(Bound));
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("stream_options", server.Requests[0]);
        Assert.DoesNotContain("stream_options", server.Requests[1]);
    }

    // A busy server is asked again before any of the reply has been read; the two
    // adapters named here cover both retry loops (the shared one and Gemini's own).
    [Theory]
    [InlineData("custom", 0)]
    [InlineData("custom", 3600)]
    [InlineData("gemini", 0)]
    [InlineData("gemini", 3600)]
    public async Task A_busy_server_is_retried_the_same_with_the_limit_on_or_off(string type, int limitSeconds)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        var server = new GatedServer()
            .Then(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"busy\"}}")
            .Then(body);
        var provider = IdleWire.Provider(type, server.CreateClient(), TimeSpan.FromSeconds(limitSeconds));

        body.Feed(wire.Opening + wire.Said + wire.Rest);
        body.End();

        Assert.Equal("hola", await Text(Ask(provider, CancellationToken.None)).WaitAsync(Bound));
        Assert.Equal(2, server.Requests.Count);
    }

    // ── where the limit's value comes from ──────────────────────────────────

    [Theory]
    [MemberData(nameof(EveryType))]
    public async Task A_providers_own_limit_is_the_one_that_runs_out_under_a_longer_server_wide_one(string type)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        var provider = await StoredIdleProvider.ResolveAsync(
            type, """{"stream_idle_seconds":1}""", new GatedServer().Then(body), serverWideSeconds: 3600);

        body.Feed(wire.Opening + wire.Said);
        var e = await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(Ask(provider, CancellationToken.None)).WaitAsync(Bound));

        Assert.Equal(TimeSpan.FromSeconds(1), e.Limit);
    }

    // A value already stored that is not a limit (saved before saving checked it, or
    // edited in the database) is ignored, and the server-wide setting applies.
    [Theory]
    [InlineData("custom", """{"stream_idle_seconds":"3600"}""")]
    [InlineData("anthropic", """{"stream_idle_seconds":"3600"}""")]
    [InlineData("gemini", """{"stream_idle_seconds":"3600"}""")]
    [InlineData("ollama", """{"stream_idle_seconds":"3600"}""")]
    [InlineData("custom", """{"stream_idle_seconds":1e-400}""")]
    [InlineData("custom", """{"stream_idle_seconds":0.3}""")]
    [InlineData("custom", """{"stream_idle_seconds":-1}""")]
    [InlineData("custom", """{"stream_idle_seconds":1e12}""")]
    public async Task A_stored_value_that_is_not_a_limit_leaves_the_server_wide_one_in_charge(string type, string config)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        var provider = await StoredIdleProvider.ResolveAsync(type, config, new GatedServer().Then(body), serverWideSeconds: 1);

        body.Feed(wire.Opening + wire.Said);
        var e = await Assert.ThrowsAsync<LlmStreamStalledException>(
            () => Text(Ask(provider, CancellationToken.None)).WaitAsync(Bound));

        Assert.Equal(TimeSpan.FromSeconds(1), e.Limit);
    }

    // Off is no timer at all, not a long one: with nothing set, and with a provider set
    // to 0 under a server-wide limit, a read after the model has started answering is
    // under the caller's own token and nothing else.
    [Theory]
    [InlineData("custom", "{}", 0)]
    [InlineData("anthropic", "{}", 0)]
    [InlineData("gemini", "{}", 0)]
    [InlineData("ollama", "{}", 0)]
    [InlineData("custom", """{"stream_idle_seconds":0}""", 60)]
    [InlineData("anthropic", """{"stream_idle_seconds":0}""", 60)]
    [InlineData("gemini", """{"stream_idle_seconds":0}""", 60)]
    [InlineData("ollama", """{"stream_idle_seconds":0}""", 60)]
    public async Task With_the_limit_off_a_read_is_under_the_callers_token_alone(string type, string config, int serverWideSeconds)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        var provider = await StoredIdleProvider.ResolveAsync(type, config, new GatedServer().Then(body), serverWideSeconds);

        body.Feed(wire.Opening + wire.Said);
        var reply = Text(Ask(provider, caller.Token));
        await body.Waiting().WaitAsync(Bound);
        Assert.Equal(caller.Token, body.ReadToken);

        body.Feed(wire.Rest);
        body.End();
        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    // The same rows with the limit on, so that the test above cannot pass for a reason
    // of its own: here the read is under a different token.
    [Theory]
    [InlineData("custom", """{"stream_idle_seconds":60}""", 0)]
    [InlineData("anthropic", """{"stream_idle_seconds":60}""", 0)]
    [InlineData("gemini", """{"stream_idle_seconds":60}""", 0)]
    [InlineData("ollama", """{"stream_idle_seconds":60}""", 0)]
    [InlineData("custom", "{}", 60)]
    [InlineData("anthropic", "{}", 60)]
    [InlineData("gemini", "{}", 60)]
    [InlineData("ollama", "{}", 60)]
    public async Task With_the_limit_on_a_read_is_under_a_token_of_the_limits_own(string type, string config, int serverWideSeconds)
    {
        var wire = IdleWire.For(type);
        var body = new GatedBody();
        using var caller = new CancellationTokenSource();
        var provider = await StoredIdleProvider.ResolveAsync(type, config, new GatedServer().Then(body), serverWideSeconds);

        body.Feed(wire.Opening + wire.Said);
        var reply = Text(Ask(provider, caller.Token));
        await body.Waiting().WaitAsync(Bound);
        Assert.NotEqual(caller.Token, body.ReadToken);

        body.Feed(wire.Rest);
        body.End();
        Assert.Equal("hola", await reply.WaitAsync(Bound));
    }

    // ── a whole turn ────────────────────────────────────────────────────────

    private const string Par = "{\"choices\":[{\"delta\":{\"content\":\"Par\"}}]}";

    // The turn's deadline passes while a limit that is far from running out is on: the
    // turn ends as a timeout, as it does with the limit off, and not as a model that
    // stopped sending.
    //
    // The deadline is real time, and the runner offers no way to move it by hand. So
    // the test asserts only what is true wherever in the reply the deadline lands, and
    // when the machine is so slow that the deadline passes before the model was even
    // asked (the runner then stops with a cancellation and reports nothing), that run
    // says nothing about the limit and the test tries again with a longer deadline.
    [Fact]
    public async Task A_turn_whose_deadline_passes_first_is_a_timeout_with_the_limit_on()
    {
        foreach (var deadlineSeconds in new[] { 2, 10, 60 })
        {
            var reply = new ScriptedBody(RunnerTestKit.Sse(Par), ScriptedBody.Ending.Hang);
            await using var kit = new RunnerTestKit(
                new ScriptedHttp(reply), deadlineSeconds: deadlineSeconds, providerConfig: """{"stream_idle_seconds":3600}""");

            try
            {
                await kit.RunAsync().WaitAsync(TurnBound);
            }
            catch (OperationCanceledException) when (kit.Http.Calls == 0)
            {
                continue;
            }

            Assert.Equal("timeout", kit.Sink.ErrorCode);
            Assert.DoesNotContain("stopped sending", kit.Sink.ErrorMessage);
            Assert.False(kit.Sink.Done);
            Assert.Equal(AgentTurn.StatusTimeout, (await kit.StoredTurnAsync()).Status);
            return;
        }

        Assert.Fail("the turn never got as far as asking the model, even with a 60 s deadline");
    }

    // The limit is on every round of a turn, not only its first: round one calls a tool
    // and ends normally, round two says something and goes silent.
    [Fact]
    public async Task A_later_round_that_goes_silent_ends_the_turn_as_an_error_after_the_limit()
    {
        var roundOne = RunnerTestKit.Sse(
            "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"not_a_tool\",\"arguments\":\"{}\"}}]}}]}",
            "{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
            "[DONE]");
        var roundTwo = new ScriptedBody(RunnerTestKit.Sse(Par), ScriptedBody.Ending.Hang);
        await using var kit = new RunnerTestKit(
            new ScriptedHttp(roundOne, roundTwo), providerConfig: """{"stream_idle_seconds":1}""");

        await kit.RunAsync().WaitAsync(TurnBound);

        Assert.Equal(2, kit.Http.Calls);
        Assert.Equal(["Par"], kit.Sink.Text);
        Assert.True(kit.Sink.Errored);
        Assert.Null(kit.Sink.ErrorCode);
        Assert.Contains("stopped sending for 1 s", kit.Sink.ErrorMessage);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusError, turn.Status);
        Assert.Equal(2, turn.Iterations);
        Assert.Equal(2, kit.Log.Rounds.Count);
        Assert.Contains(" outcome=failed", kit.Log.Rounds[1]);
    }
}
