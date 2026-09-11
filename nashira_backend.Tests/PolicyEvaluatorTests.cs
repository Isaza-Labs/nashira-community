using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Policy;

namespace nashira_backend.Tests;

// Guardrails. A policy that fails to match when it should is a rule nobody is
// following; one that matches when it should not blocks legitimate work. Both are
// bugs, so the boundaries are worth pinning down.
public class PolicyEvaluatorTests
{
    // Test() needs no DB, but the type takes a DbContext — an in-memory one keeps
    // the constructor honest without a live Postgres.
    private static PolicyEvaluator NewEvaluator()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"policy-{Guid.NewGuid()}")
            .Options;
        return new PolicyEvaluator(new AppDbContext(options), NullLogger<PolicyEvaluator>.Instance);
    }

    private static readonly PolicyEvaluator Eval = NewEvaluator();

    private static PolicyContext Context(
        string env = "production",
        string? description = null,
        string[]? roles = null,
        string[]? pools = null,
        string[]? types = null) =>
        new(env, description, roles, pools, types);

    [Fact]
    public void A_rule_with_no_conditions_denies_everything()
    {
        // A change freeze is a legitimate thing to express.
        var decision = Eval.Test("""{"action":"deny","reason":"change freeze"}""", Context());

        Assert.True(decision.Denied);
        Assert.Equal("change freeze", decision.Reason);
    }

    [Fact]
    public void A_non_deny_action_never_denies()
    {
        // An author who typed "warn" wanted something this build does not do;
        // blocking their work is not a reasonable reading of that.
        Assert.False(Eval.Test("""{"action":"warn","when":{}}""", Context()).Denied);
        Assert.False(Eval.Test("""{"when":{"environment":["production"]}}""", Context()).Denied);
    }

    [Fact]
    public void An_environment_clause_matches_only_that_environment()
    {
        const string rule = """{"action":"deny","when":{"environment":["production"]}}""";

        Assert.True(Eval.Test(rule, Context(env: "production")).Denied);
        Assert.False(Eval.Test(rule, Context(env: "draft")).Denied);
    }

    // Clauses AND together, values within a clause OR. Adding a clause must narrow
    // a rule, never widen it.
    [Fact]
    public void Clauses_and_together()
    {
        const string rule =
            """{"action":"deny","when":{"environment":["production"],"device_role":["core"]}}""";

        Assert.True(Eval.Test(rule, Context(env: "production", roles: ["core"])).Denied);
        // Right environment, wrong role — one clause failing is enough to allow.
        Assert.False(Eval.Test(rule, Context(env: "production", roles: ["edge"])).Denied);
        Assert.False(Eval.Test(rule, Context(env: "draft", roles: ["core"])).Denied);
    }

    [Fact]
    public void Values_within_a_clause_or_together()
    {
        const string rule = """{"action":"deny","when":{"device_role":["core","spine"]}}""";

        Assert.True(Eval.Test(rule, Context(roles: ["spine"])).Denied);
        Assert.False(Eval.Test(rule, Context(roles: ["edge"])).Denied);
    }

    [Fact]
    public void Any_matching_value_in_the_context_triggers_the_clause()
    {
        // A run touching one core device is a run touching a core device.
        const string rule = """{"action":"deny","when":{"device_role":["core"]}}""";
        Assert.True(Eval.Test(rule, Context(roles: ["edge", "core"])).Denied);
    }

    // A policy about device roles must not fire on a run that targets no devices —
    // otherwise every device-scoped guardrail would block every device-less run.
    [Fact]
    public void A_present_clause_does_not_match_an_empty_context()
    {
        const string rule = """{"action":"deny","when":{"device_role":["core"]}}""";
        Assert.False(Eval.Test(rule, Context(roles: [])).Denied);
        Assert.False(Eval.Test(rule, Context()).Denied);
    }

    [Fact]
    public void An_absent_clause_places_no_restriction()
    {
        const string rule = """{"action":"deny","when":{"environment":["production"]}}""";
        // No roles in context, but the rule does not ask about roles.
        Assert.True(Eval.Test(rule, Context(env: "production")).Denied);
    }

    [Fact]
    public void A_single_string_is_accepted_where_an_array_is_expected()
    {
        // Writing one value as a bare string is what everybody does first.
        const string rule = """{"action":"deny","when":{"environment":"production"}}""";
        Assert.True(Eval.Test(rule, Context(env: "production")).Denied);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        const string rule = """{"action":"deny","when":{"device_role":["CORE"]}}""";
        Assert.True(Eval.Test(rule, Context(roles: ["core"])).Denied);
    }

    [Fact]
    public void Description_contains_is_a_substring_match()
    {
        const string rule = """{"action":"deny","when":{"description_contains":["bgp"]}}""";

        Assert.True(Eval.Test(rule, Context(description: "Reset the BGP session")).Denied);
        Assert.False(Eval.Test(rule, Context(description: "Rotate credentials")).Denied);
        Assert.False(Eval.Test(rule, Context(description: null)).Denied);
    }

    [Fact]
    public void Snippet_types_are_matched()
    {
        const string rule = """{"action":"deny","when":{"snippet_type":["ssh"]}}""";

        Assert.True(Eval.Test(rule, Context(types: ["transform", "ssh"])).Denied);
        Assert.False(Eval.Test(rule, Context(types: ["transform"])).Denied);
    }

    // A guardrail that silently stops guarding is the worst outcome, so an
    // unparseable rule denies and names itself rather than being skipped.
    [Fact]
    public void An_unparseable_rule_denies_and_names_the_policy()
    {
        var decision = Eval.Test("{not json", Context(), "freeze-window");

        Assert.True(decision.Denied);
        Assert.Equal("freeze-window", decision.PolicyName);
        Assert.Contains("not valid JSON", decision.Reason);
    }

    [Fact]
    public void A_rule_that_is_not_an_object_allows()
    {
        // An empty or array-shaped rule expresses no prohibition.
        Assert.False(Eval.Test("[]", Context()).Denied);
        Assert.False(Eval.Test("{}", Context()).Denied);
        Assert.False(Eval.Test("", Context()).Denied);
    }

    [Fact]
    public void A_denial_without_a_stated_reason_still_says_something()
    {
        var decision = Eval.Test("""{"action":"deny","when":{}}""", Context());
        Assert.True(decision.Denied);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }
}
