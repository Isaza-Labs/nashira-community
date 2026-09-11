using nashira_backend.Services.Ai.Conversation;

namespace nashira_backend.Tests;

// Which provider answers a chat turn, when the request, the conversation and the
// tenant default disagree.
//
// The chat used to call ResolveDefaultAsync unconditionally, so every turn went to
// the oldest enabled provider and a tenant with several had no way to reach the
// others. These are the rules the selector rests on; the one that is easy to get
// wrong is the third.
public class ChatModelSelectionTests
{
    private static readonly Guid Anthropic = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OpenAi = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void A_turn_that_names_nothing_keeps_what_the_conversation_used()
    {
        var (provider, model) = AgentConversationRunner.ChooseModel(
            requestedProvider: null, requestedModel: null,
            storedProvider: Anthropic, storedModel: "claude-sonnet-5");

        Assert.Equal(Anthropic, provider);
        Assert.Equal("claude-sonnet-5", model);
    }

    [Fact]
    public void A_conversation_with_no_choice_on_record_falls_back_to_the_default()
    {
        var (provider, model) = AgentConversationRunner.ChooseModel(null, null, null, null);

        // Null provider is the caller's signal to use the tenant default; a null
        // model then means the provider's own DefaultModel.
        Assert.Null(provider);
        Assert.Null(model);
    }

    // The one that matters: a model id belongs to the provider that serves it.
    // Inheriting the conversation's model while switching provider would send
    // "gpt-5" to Anthropic, and the failure surfaces as an opaque vendor error.
    [Fact]
    public void Switching_provider_drops_the_previous_model_instead_of_carrying_it_over()
    {
        var (provider, model) = AgentConversationRunner.ChooseModel(
            requestedProvider: Anthropic, requestedModel: null,
            storedProvider: OpenAi, storedModel: "gpt-5");

        Assert.Equal(Anthropic, provider);
        Assert.Null(model); // resolves to Anthropic's own default
    }

    [Fact]
    public void Naming_both_uses_both()
    {
        var (provider, model) = AgentConversationRunner.ChooseModel(
            requestedProvider: Anthropic, requestedModel: "claude-opus-5",
            storedProvider: OpenAi, storedModel: "gpt-5");

        Assert.Equal(Anthropic, provider);
        Assert.Equal("claude-opus-5", model);
    }

    // A model with no provider is the picker offering another model of the provider
    // already in play — the provider must survive.
    [Fact]
    public void A_model_without_a_provider_stays_on_the_conversations_provider()
    {
        var (provider, model) = AgentConversationRunner.ChooseModel(
            requestedProvider: null, requestedModel: "claude-haiku-4-5",
            storedProvider: Anthropic, storedModel: "claude-sonnet-5");

        Assert.Equal(Anthropic, provider);
        Assert.Equal("claude-haiku-4-5", model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_model_is_not_a_choice(string blank)
    {
        var (provider, model) = AgentConversationRunner.ChooseModel(
            requestedProvider: null, requestedModel: blank,
            storedProvider: Anthropic, storedModel: "claude-sonnet-5");

        Assert.Equal(Anthropic, provider);
        Assert.Equal("claude-sonnet-5", model);
    }
}
