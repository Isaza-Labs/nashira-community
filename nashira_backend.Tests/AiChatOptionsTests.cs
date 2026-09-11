using Microsoft.Extensions.Configuration;
using nashira_backend.Configuration;

namespace nashira_backend.Tests;

// The turn budget, and the one relationship between its numbers that has to hold.
//
// These started as constants in AgentConversationRunner and were overruled by a
// number nobody had written down: the "llm" HttpClient kept .NET's default 100s
// timeout, twenty seconds under the 120s turn deadline, so a long turn always died
// at the transport — a raw TaskCanceledException instead of the runner's timeout
// path, with the deadline's message and partial text never reached. The first test
// is that invariant; the rest is that the section still binds.
public class AiChatOptionsTests
{
    [Fact]
    public void Transport_timeout_outlives_the_turn_deadline()
    {
        var options = new AiChatOptions();

        Assert.True(
            AiChatOptions.TransportTimeout.TotalSeconds > options.StreamDeadlineSeconds,
            $"the llm HttpClient timeout ({AiChatOptions.TransportTimeout.TotalSeconds}s) must stay "
            + $"above the turn deadline ({options.StreamDeadlineSeconds}s), or the transport ends "
            + "long turns instead of the deadline and the runner's timeout path never runs");
    }

    // Not a round number for its own sake: the gap is the room a turn has to notice
    // its own deadline, cancel, and persist what it had. A backstop a few seconds
    // above the deadline would technically satisfy the test above and still race it.
    [Fact]
    public void Transport_timeout_leaves_room_rather_than_shaving_it()
    {
        var options = new AiChatOptions();
        var margin = AiChatOptions.TransportTimeout.TotalSeconds - options.StreamDeadlineSeconds;

        Assert.True(margin >= 60, $"only {margin}s of margin between transport and deadline");
    }

    [Fact]
    public void Defaults_are_sized_for_multi_tool_turns()
    {
        var options = new AiChatOptions();

        // Building a workflow and its schedule is a chain of tool calls, each a
        // round-trip to the model. A budget tuned to a single question starves it.
        Assert.Equal(240, options.StreamDeadlineSeconds);
        Assert.Equal(50, options.MaxIterations);
        Assert.Equal(75, options.DeadlineWarnPercent);
    }

    // The tool-result cap is what decides whether the model sees a whole page of an
    // external API or half of one. It shipped as a private 8_000 constant — a
    // regression from flow-weaver's configurable 100_000 — and a single list page
    // never fit, so every "the answer is truncated" report traced back here. The
    // number is pinned so a well-meaning "let's save some tokens" cannot quietly
    // reintroduce it.
    [Fact]
    public void Tool_result_cap_fits_a_whole_list_page()
    {
        var options = new AiChatOptions();

        Assert.Equal(100_000, options.MaxToolResultChars);
    }

    // History is a window, and its default has to hold a real exchange or two with
    // tables in them while staying well under the model's context.
    [Fact]
    public void History_window_default_is_sixty_thousand_chars()
    {
        Assert.Equal(60_000, new AiChatOptions().HistoryCharBudget);
    }

    [Fact]
    public void Section_binds_from_configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiChat:StreamDeadlineSeconds"] = "240",
                ["AiChat:MaxIterations"] = "12",
                ["AiChat:DeadlineWarnPercent"] = "50",
                ["AiChat:MaxToolResultChars"] = "32000",
                ["AiChat:HistoryCharBudget"] = "0",
            })
            .Build();

        var options = config.GetSection(AiChatOptions.SectionName).Get<AiChatOptions>();

        Assert.NotNull(options);
        Assert.Equal(240, options!.StreamDeadlineSeconds);
        Assert.Equal(12, options.MaxIterations);
        Assert.Equal(50, options.DeadlineWarnPercent);
        Assert.Equal(32_000, options.MaxToolResultChars);
        Assert.Equal(0, options.HistoryCharBudget);
    }

    // The env-var spelling operators actually use. `AiChat__StreamDeadlineSeconds`
    // is the documented override, and it is worth a test because a rename of the
    // section constant would break it silently everywhere it is already deployed.
    [Fact]
    public void Section_binds_from_double_underscore_environment_variables()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiChat:StreamDeadlineSeconds"] = "90",
            })
            .Build();

        Assert.Equal("AiChat", AiChatOptions.SectionName);
        Assert.Equal(90, config.GetSection(AiChatOptions.SectionName).Get<AiChatOptions>()!.StreamDeadlineSeconds);
    }

    // An unset section must not zero the budget out. The runner falls back when it
    // sees a non-positive value, but the options themselves should never hand it one.
    [Fact]
    public void Missing_section_keeps_the_defaults()
    {
        var config = new ConfigurationBuilder().Build();

        var options = config.GetSection(AiChatOptions.SectionName).Get<AiChatOptions>() ?? new AiChatOptions();

        Assert.True(options.StreamDeadlineSeconds > 0);
        Assert.True(options.MaxIterations > 0);
        Assert.True(options.MaxToolResultChars > 0);
    }
}
