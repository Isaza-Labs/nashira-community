using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.SelfCorrection;

namespace nashira_backend.Tests;

public class ErrorClassifierTests
{
    [Theory]
    [InlineData("Operation timed out after 30s", ErrorClassifier.Timeout)]
    [InlineData("field 'vlan' must be a string", ErrorClassifier.Validation)]
    [InlineData("commit_message is required", ErrorClassifier.Validation)]
    [InlineData("401 Unauthorized", ErrorClassifier.Auth)]
    [InlineData("authentication failed for device", ErrorClassifier.Auth)]
    [InlineData("connection refused by host", ErrorClassifier.Connection)]
    [InlineData("a repository with that name already exists", ErrorClassifier.Duplicate)]
    [InlineData("device 'r1' not found in inventory", ErrorClassifier.NotFound)]
    [InlineData("something entirely unexpected", ErrorClassifier.Unknown)]
    public void Classify_maps_message_to_category(string message, string expected) =>
        Assert.Equal(expected, ErrorClassifier.Classify(message));
}

public class LearningsStoreHelperTests
{
    [Theory]
    [InlineData("already exists", "a repo already exists here", true)]
    [InlineData("host key (mismatch|verification failed)", "ssh host key mismatch", true)]
    [InlineData("not_found", "found nothing", false)]
    [InlineData("[invalid(regex", "contains [invalid(regex literally", true)] // invalid regex -> substring fallback
    public void PatternMatches_uses_regex_then_substring(string pattern, string message, bool expected) =>
        Assert.Equal(expected, LearningsStore.PatternMatches(pattern, message));

    [Theory]
    [InlineData(3, 1, 0.75)]
    [InlineData(0, 0, 0.5)]
    [InlineData(1, 0, 1.0)]
    public void ConfidenceOf_is_success_rate(long success, long failure, double expected) =>
        Assert.Equal(expected, LearningsStore.ConfidenceOf(success, failure));
}

public class SelfCorrectionEngineTests
{
    private sealed class FakeStore : ILearningsStore
    {
        public AgentLearning? Fix { get; init; }
        public Task<AgentLearning?> FindBestFixAsync(string t, string s, string e, CancellationToken ct) => Task.FromResult(Fix);
        public Task RecordOutcomeAsync(Guid id, bool ok, CancellationToken ct) => Task.CompletedTask;
        public Task RecordDiscoveredAsync(string a, string b, string c, string d, string e, string? f, CancellationToken ct) => Task.CompletedTask;
    }

    private static SelfCorrectionEngine Engine(ILearningsStore store) =>
        new(store, NullLogger<SelfCorrectionEngine>.Instance);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Coerces_numeric_field_named_in_error_to_string()
    {
        var engine = Engine(new FakeStore());
        var c = await engine.SuggestCorrectionAsync("configure_vlan", Args("""{"vlan":100,"name":"x"}"""),
            "field 'vlan' must be a string", CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(SelfCorrectionEngine.StrategyParameterAdjust, c!.Strategy);
        Assert.Equal("static_rules", c.Source);
        Assert.Equal("100", c.CorrectedArgs!.Value.GetProperty("vlan").GetString());
    }

    [Fact]
    public async Task Escalates_with_hint_for_connection_errors()
    {
        var engine = Engine(new FakeStore());
        var c = await engine.SuggestCorrectionAsync("t", Args("{}"), "connection refused by host", CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(SelfCorrectionEngine.StrategyEscalate, c!.Strategy);
        Assert.Contains("connect", c.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uses_learning_store_escalate_message_when_present()
    {
        var store = new FakeStore
        {
            Fix = new AgentLearning
            {
                AgentLearningId = Guid.NewGuid(),
                FixStrategy = AgentLearning.StrategyEscalate,
                FixParamsJson = """{"message":"custom device hint"}""",
                Confidence = 0.8,
            },
        };
        var c = await Engine(store).SuggestCorrectionAsync("device_connect", Args("{}"), "some error", CancellationToken.None);

        Assert.NotNull(c);
        Assert.Equal(SelfCorrectionEngine.StrategyEscalate, c!.Strategy);
        Assert.Equal("learnings_store", c.Source);
        Assert.Equal("custom device hint", c.Message);
    }

    [Fact]
    public async Task Returns_null_when_nothing_matches()
    {
        var c = await Engine(new FakeStore()).SuggestCorrectionAsync("t", Args("{}"), "some weird gibberish xyz", CancellationToken.None);
        Assert.Null(c);
    }
}
