using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// Anthropic requires messages to alternate user/assistant. MapMessages drops a
// turn that ends up with no content blocks — correctly, since an empty content
// array is a 400 — but dropping one from the MIDDLE of the history leaves the
// turns on either side of it adjacent, and two user turns in a row is the same
// 400 by a different name.
//
// An empty assistant message is not hypothetical: AgentConversationRunner
// persists `finalText` verbatim, and a turn that stops to ask for tool
// confirmation produces no text and is not marked errored, so "" is stored. From
// then on every turn in that conversation replays it, and the conversation is
// permanently unusable.
public class AnthropicHistoryAlternationTests
{
    private static LlmMessage User(string text) => new() { Role = "user", Content = text };
    private static LlmMessage Assistant(string text) => new() { Role = "assistant", Content = text };

    private static List<string> Roles(List<Dictionary<string, object?>> mapped)
        => mapped.Select(m => (string)m["role"]!).ToList();

    [Fact]
    public void An_empty_assistant_turn_mid_history_does_not_collapse_the_roles()
    {
        var (_, mapped) = AnthropicProvider.MapMessages(
        [
            new LlmMessage { Role = "system", Content = "prompt" },
            User("first question"),
            Assistant(""),          // a turn that stopped to ask for confirmation
            User("second question"),
        ]);

        var roles = Roles(mapped);
        for (var i = 1; i < roles.Count; i++)
            Assert.True(roles[i] != roles[i - 1],
                $"roles must alternate, got [{string.Join(", ", roles)}] — Anthropic answers two "
                + "consecutive user turns with a 400, so the whole conversation stops working");
    }

    [Fact]
    public void The_users_two_questions_both_survive()
    {
        var (_, mapped) = AnthropicProvider.MapMessages(
        [
            User("first question"),
            Assistant(""),
            User("second question"),
        ]);

        // Whatever the repair is, it must not silently drop what the user said.
        var text = string.Join(" ", mapped.SelectMany(m =>
            ((List<Dictionary<string, object?>>)m["content"]!).Select(b => b["text"] as string ?? "")));
        Assert.Contains("first question", text);
        Assert.Contains("second question", text);
    }

    [Fact]
    public void A_normal_history_is_untouched()
    {
        var (system, mapped) = AnthropicProvider.MapMessages(
        [
            new LlmMessage { Role = "system", Content = "prompt" },
            User("q1"),
            Assistant("a1"),
            User("q2"),
        ]);

        Assert.Equal("prompt", system);
        Assert.Equal(["user", "assistant", "user"], Roles(mapped));
    }

    // Several empty assistant turns in a row is the same failure, and a conversation
    // where the agent asked for confirmation twice reaches it.
    [Fact]
    public void Repeated_empty_assistant_turns_still_alternate()
    {
        var (_, mapped) = AnthropicProvider.MapMessages(
        [
            User("q1"),
            Assistant(""),
            User("q2"),
            Assistant(""),
            User("q3"),
        ]);

        var roles = Roles(mapped);
        for (var i = 1; i < roles.Count; i++)
            Assert.True(roles[i] != roles[i - 1],
                $"roles must alternate, got [{string.Join(", ", roles)}]");
    }
}
