using nashira_backend.Data.Models;

namespace nashira_backend.Tests;

// A turn whose deadline has passed is a timeout, wherever in the turn it passed. The
// two places it used to be missed both ended the turn as completed with a partial
// answer: the providers stop reading a cancelled stream without throwing, and the loop
// over rounds stopped on the token as a loop condition.
public class RoundDeadlineTests
{
    [Fact]
    public async Task A_deadline_that_passes_while_text_is_being_sent_ends_the_turn_as_a_timeout()
    {
        // Two text chunks and a normal end. The deadline passes while the first chunk
        // is with the browser, so the second is never read.
        var body = RunnerTestKit.Sse(
            "{\"choices\":[{\"delta\":{\"content\":\"Hal\"}}]}",
            "{\"choices\":[{\"delta\":{\"content\":\"f an answer\"},\"finish_reason\":\"stop\"}]}",
            "[DONE]");
        await using var kit = new RunnerTestKit(
            new ScriptedHttp(body),
            new RecordingSink(textDelay: TimeSpan.FromMilliseconds(1_500)),
            deadlineSeconds: 1);

        await kit.RunAsync();

        Assert.Equal(["Hal"], kit.Sink.Text);
        Assert.Equal("timeout", kit.Sink.ErrorCode);
        Assert.False(kit.Sink.Done);
        Assert.Equal(AgentTurn.StatusTimeout, (await kit.StoredTurnAsync()).Status);
    }

    [Fact]
    public async Task A_deadline_that_passes_between_rounds_ends_the_turn_as_a_timeout()
    {
        // Round one says something and asks for a tool. The deadline passes while the
        // tool's result is with the browser, so no round is under way when it does. The
        // script has no second response: asking for one would fail the turn as an error.
        var roundOne = RunnerTestKit.Sse(
            "{\"choices\":[{\"delta\":{\"content\":\"Checking.\"}}]}",
            "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"not_a_tool\",\"arguments\":\"{}\"}}]}}]}",
            "{\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}",
            "[DONE]");
        await using var kit = new RunnerTestKit(
            new ScriptedHttp(roundOne),
            new RecordingSink(toolResultDelay: TimeSpan.FromMilliseconds(1_500)),
            deadlineSeconds: 1);

        await kit.RunAsync();

        Assert.Equal(["not_a_tool"], kit.Sink.ToolResults);
        Assert.Equal(1, kit.Http.Calls);
        Assert.Equal("timeout", kit.Sink.ErrorCode);
        Assert.False(kit.Sink.Done);
        var turn = await kit.StoredTurnAsync();
        Assert.Equal(AgentTurn.StatusTimeout, turn.Status);
        Assert.Equal(1, turn.Iterations);
        Assert.Equal("Checking.", turn.AssistantText);
    }
}
