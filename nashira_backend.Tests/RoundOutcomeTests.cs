using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

// What a turn keeps about its model calls. Each round writes one "ai.chat.iteration"
// log line with its outcome, whether it completed, failed or was cancelled, and the
// token and cache figures a round reported count toward the stored turn even when the
// round did not finish.
public class RoundOutcomeTests
{
    private const string TextChunk = "{\"choices\":[{\"delta\":{\"content\":\"Par\"}}]}";
    private const string Usage100With80Cached =
        "{\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5,"
        + "\"prompt_tokens_details\":{\"cached_tokens\":80}}}";

    [Fact]
    public async Task A_round_that_breaks_mid_stream_is_logged_as_failed_and_keeps_what_it_reported()
    {
        // The server sends some text and its usage, then the connection breaks.
        var body = new ScriptedBody(RunnerTestKit.Sse(TextChunk, Usage100With80Cached), ScriptedBody.Ending.Break);
        await using var kit = new RunnerTestKit(new ScriptedHttp(body));

        await kit.RunAsync();

        var round = Assert.Single(kit.Log.Rounds);
        Assert.Contains(" outcome=failed", round);
        Assert.Contains(" tokens_in=100 tokens_cache_read=80 tokens_out=5 ", round);

        Assert.True(kit.Sink.Errored);
        Assert.Null(kit.Sink.ErrorCode);
        Assert.False(kit.Sink.Done);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusError, turn.Status);
        Assert.Equal("Par", turn.AssistantText);
        Assert.Equal((100, 5, 80), (turn.TokensIn, turn.TokensOut, turn.TokensCacheRead));
    }

    [Fact]
    public async Task A_round_cut_off_by_the_deadline_is_logged_as_cancelled_and_keeps_what_it_reported()
    {
        // The server sends some text and its usage, then goes silent with the
        // connection open until the turn's deadline cancels the read.
        var body = new ScriptedBody(RunnerTestKit.Sse(TextChunk, Usage100With80Cached), ScriptedBody.Ending.Hang);
        await using var kit = new RunnerTestKit(new ScriptedHttp(body), deadlineSeconds: 1);

        await kit.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));

        var round = Assert.Single(kit.Log.Rounds);
        Assert.Contains(" outcome=cancelled", round);
        Assert.Contains(" tokens_in=100 tokens_cache_read=80 tokens_out=5 ", round);

        Assert.Equal("timeout", kit.Sink.ErrorCode);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusTimeout, turn.Status);
        Assert.Equal((100, 5, 80), (turn.TokensIn, turn.TokensOut, turn.TokensCacheRead));
    }

    [Fact]
    public async Task A_finished_turn_stores_the_cache_figures_summed_over_its_rounds()
    {
        // Round one calls a tool and reports nothing cached; round two answers and
        // reports 120 of its 150 prompt tokens as cached.
        var roundOne = RunnerTestKit.Sse(
            "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"not_a_tool\",\"arguments\":\"{}\"}}]}}]}",
            "{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
            "{\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":5}}",
            "[DONE]");
        var roundTwo = RunnerTestKit.Sse(
            "{\"choices\":[{\"delta\":{\"content\":\"Done.\"},\"finish_reason\":\"stop\"}]}",
            "{\"choices\":[],\"usage\":{\"prompt_tokens\":150,\"completion_tokens\":7,"
            + "\"prompt_tokens_details\":{\"cached_tokens\":120}}}",
            "[DONE]");
        await using var kit = new RunnerTestKit(new ScriptedHttp(roundOne, roundTwo));

        await kit.RunAsync();

        Assert.Equal(2, kit.Http.Calls);
        Assert.True(kit.Sink.Done);
        Assert.Equal((250, 12, 120, 2), kit.Sink.Totals);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusCompleted, turn.Status);
        Assert.Equal((250, 12, 120, 0), (turn.TokensIn, turn.TokensOut, turn.TokensCacheRead, turn.TokensCacheWrite));
        Assert.Equal(2, turn.Iterations);

        Assert.Collection(kit.Log.Rounds,
            first =>
            {
                Assert.Contains(" tokens_in=100 tokens_cache_read=0 tokens_out=5 ", first);
                Assert.Contains(" outcome=completed", first);
            },
            second =>
            {
                Assert.Contains(" tokens_in=150 tokens_cache_read=120 tokens_out=7 ", second);
                Assert.Contains(" outcome=completed", second);
            });
    }

    [Fact]
    public async Task A_turn_on_anthropic_stores_what_was_read_from_and_written_to_the_cache()
    {
        // Anthropic reports the uncached part, the part read from the cache and the
        // part written to it separately; the stored prompt total is all three.
        var body = RunnerTestKit.Sse(
            "{\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":20,"
            + "\"cache_read_input_tokens\":300,\"cache_creation_input_tokens\":50}}}",
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi.\"}}",
            "{\"type\":\"content_block_stop\",\"index\":0}",
            "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":4}}",
            "{\"type\":\"message_stop\"}");
        await using var kit = new RunnerTestKit(new ScriptedHttp(body), providerType: "anthropic");

        await kit.RunAsync();

        Assert.True(kit.Sink.Done);
        Assert.Equal(["Hi."], kit.Sink.Text);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusCompleted, turn.Status);
        Assert.Equal((370, 4, 300, 50), (turn.TokensIn, turn.TokensOut, turn.TokensCacheRead, turn.TokensCacheWrite));
    }
}
