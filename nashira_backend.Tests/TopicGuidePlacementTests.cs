using System.Net;
using System.Text.Json;
using nashira_backend.Configuration;
using nashira_backend.Services.Ai.Conversation;
using nashira_backend.Services.Ai.Providers;

namespace nashira_backend.Tests;

// AiChat__ScopedPromptAfterUserMessage: where the topic-guide system message goes, and
// for which providers. Off is the order every request has always had.
public class TopicGuidePlacementTests
{
    private static string Shape(List<LlmMessage> messages) =>
        string.Join(" | ", messages.Select(m => $"{m.Role}:{m.Content}"));

    private static readonly LlmMessage[] EarlierTurns =
    [
        new() { Role = "system", Content = "note" },
        new() { Role = "user", Content = "q1" },
        new() { Role = "assistant", Content = "a1" },
    ];

    [Fact]
    public void The_switch_is_off_by_default()
    {
        Assert.False(new AiChatOptions().ScopedPromptAfterUserMessage);
    }

    // The default is the order every request has had: nothing about it may move.
    [Fact]
    public void Opening_messages_keep_their_order_by_default()
    {
        var messages = AgentConversationRunner.ArrangeMessages("base", "guides", "persona", EarlierTurns, "q2", scopedAfterUser: false);

        Assert.Equal(
            "system:base | system:guides | system:persona | system:note | user:q1 | assistant:a1 | user:q2",
            Shape(messages));
    }

    // With the switch on, only the topic-guide message moves, to the very end, and it
    // is the same text. What is ahead of the conversation no longer depends on it.
    [Fact]
    public void Topic_guides_move_behind_the_user_message_unchanged_when_asked()
    {
        var messages = AgentConversationRunner.ArrangeMessages("base", "guides", "persona", EarlierTurns, "q2", scopedAfterUser: true);

        Assert.Equal(
            "system:base | system:persona | system:note | user:q1 | assistant:a1 | user:q2 | system:guides",
            Shape(messages));

        var other = AgentConversationRunner.ArrangeMessages("base", "other guides", "persona", EarlierTurns, "q2", scopedAfterUser: true);
        Assert.Equal(Shape(messages.Take(messages.Count - 1).ToList()), Shape(other.Take(other.Count - 1).ToList()));
    }

    // No topic-guide message and no persona: both orders are the same request.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opening_messages_without_guides_or_persona_do_not_depend_on_the_switch(bool scopedAfterUser)
    {
        var messages = AgentConversationRunner.ArrangeMessages("base", "", null, [], "q", scopedAfterUser);

        Assert.Equal("system:base | user:q", Shape(messages));
    }

    // Only the OpenAI-compatible adapter sends a system message where it stands; for
    // the others the switch is a no-op whatever its setting, so their requests can
    // never change because of it.
    [Fact]
    public void The_switch_applies_only_to_the_openai_compatible_adapter()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK));
        ILlmProvider[] others =
        [
            new AnthropicProvider(http, "k"),
            new GeminiProvider(http, "k"),
            new OllamaProvider(http, ""),
        ];
        ILlmProvider[] compatible =
        [
            new OpenAiProvider(http, "k"),
            new OpenAiProvider(http, "k", "https://ring.example/v1", null, "custom"),
            new OpenAiProvider(http, "k", "https://api.deepseek.com", null, "deepseek"),
        ];

        foreach (var p in others)
        {
            Assert.False(AgentConversationRunner.ScopedPromptAfterUserFor(true, null, p), p.GetType().Name);
            Assert.False(AgentConversationRunner.ScopedPromptAfterUserFor(false, null, p), p.GetType().Name);
        }
        foreach (var p in compatible)
        {
            Assert.True(AgentConversationRunner.ScopedPromptAfterUserFor(true, null, p), p.ProviderType);
            Assert.False(AgentConversationRunner.ScopedPromptAfterUserFor(false, null, p), p.ProviderType);
        }
    }

    // A provider row's own setting (Config.guidance_after_question) wins over the
    // server-wide switch in both directions; without one the switch decides. It still
    // never applies to the other adapters.
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    public void A_providers_own_setting_wins_over_the_server_switch(bool switchOn, bool? providerSetting, bool expected)
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK));
        var ring = new OpenAiProvider(http, "k", "https://ring.example/v1", null, "custom");

        Assert.Equal(expected, AgentConversationRunner.ScopedPromptAfterUserFor(switchOn, providerSetting, ring));
        Assert.False(AgentConversationRunner.ScopedPromptAfterUserFor(switchOn, true, new AnthropicProvider(http, "k")));
        Assert.False(AgentConversationRunner.ScopedPromptAfterUserFor(switchOn, true, new GeminiProvider(http, "k")));
        Assert.False(AgentConversationRunner.ScopedPromptAfterUserFor(switchOn, true, new OllamaProvider(http, "")));
    }

    // The setting is read from operator-edited JSON: only a real true/false counts, so
    // a typo such as "yes" leaves the decision to the server switch.
    [Theory]
    [InlineData("""{"guidance_after_question":true}""", true)]
    [InlineData("""{"guidance_after_question":false}""", false)]
    [InlineData("""{}""", null)]
    [InlineData("""{"guidance_after_question":"yes"}""", null)]
    [InlineData("""{"guidance_after_question":1}""", null)]
    [InlineData("""[]""", null)]
    public void The_provider_setting_is_read_only_from_a_json_boolean(string config, bool? expected)
        => Assert.Equal(expected, LlmProviderFactory.ConfigBool(
            JsonDocument.Parse(config).RootElement, LlmProviderFactory.GuidanceAfterQuestionConfigKey));

    private static async Task<JsonElement> SentMessages(List<LlmMessage> messages)
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "data: [DONE]\n", "text/event-stream");
        using var http = new HttpClient(handler);
        var provider = new OpenAiProvider(http, "k", "https://ring.example/v1", null, "custom");
        await foreach (var _ in provider.ChatWithToolsStreamAsync(messages, [], "m", 0.2, CancellationToken.None)) { }
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        return body.RootElement.GetProperty("messages").Clone();
    }

    private static string Roles(JsonElement messages) =>
        string.Join(",", messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));

    // What actually goes on the wire to an OpenAI-compatible server. Off: the order
    // every request has had. On: the topic-guide message is the last message, still a
    // system message, with exactly the same text.
    [Fact]
    public async Task On_the_wire_the_topic_guide_moves_behind_the_question_and_stays_a_system_message()
    {
        const string guides = "## Topic skills\n- workflows.md: loaded\n\n(guide text)";
        var off = await SentMessages(AgentConversationRunner.ArrangeMessages("base", guides, null, [], "q", scopedAfterUser: false));
        var on = await SentMessages(AgentConversationRunner.ArrangeMessages("base", guides, null, [], "q", scopedAfterUser: true));

        Assert.Equal("system,system,user", Roles(off));
        Assert.Equal(guides, off[1].GetProperty("content").GetString());

        Assert.Equal("system,user,system", Roles(on));
        Assert.Equal(guides, on[2].GetProperty("content").GetString());
        Assert.Equal("base", on[0].GetProperty("content").GetString());
        Assert.Equal("q", on[1].GetProperty("content").GetString());
    }

    // A follow-up question in the same conversation. History holds the question and
    // the answer (never the guide), so the next request repeats the previous one up to
    // and including the earlier question, then the answer, the new question and the
    // guide. That shared part is what a prompt cache can resume; the guide behind the
    // previous question is not, which is the switch's known limit across turns.
    [Fact]
    public void A_follow_up_repeats_the_previous_request_up_to_the_earlier_question()
    {
        var first = AgentConversationRunner.ArrangeMessages("base", "guides", "persona", [], "q1", scopedAfterUser: true);
        LlmMessage[] history = [new() { Role = "user", Content = "q1" }, new() { Role = "assistant", Content = "a1" }];
        var second = AgentConversationRunner.ArrangeMessages("base", "guides", "persona", history, "q2", scopedAfterUser: true);

        Assert.Equal("system:base | system:persona | user:q1 | system:guides", Shape(first));
        Assert.Equal("system:base | system:persona | user:q1 | assistant:a1 | user:q2 | system:guides", Shape(second));
        Assert.Equal(Shape(first.Take(3).ToList()), Shape(second.Take(3).ToList()));
    }
}
