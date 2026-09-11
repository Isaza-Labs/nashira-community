using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;
using SnippetEntity = nashira_backend.Data.Models.Snippet;

namespace nashira_backend.Tests;

// Which tier actually applies to a step. It drives the audit event, the rollback
// plan and what a promotion reviewer is warned about, so an override that is
// honoured when it should not be is a promise of a reversal that does not exist.
public class IdempotencyTests
{
    private static SnippetEntity Snippet(string? declared = null) => new()
    {
        SnippetId = Guid.NewGuid(),
        Name = "s",
        Type = "rest_call",
        Idempotency = declared,
    };

    [Fact]
    public void With_no_declaration_the_handler_default_applies()
    {
        Assert.Equal(
            IdempotencyKind.RequiresCompensation,
            Idempotency.Effective(Snippet(), IdempotencyKind.RequiresCompensation));
    }

    // The reason the override exists: rest_call and integration_action reach the
    // same code for a GET and a DELETE, so only the author knows which it is.
    [Fact]
    public void An_author_may_downgrade_a_compensable_handler_to_idempotent()
    {
        Assert.Equal(
            IdempotencyKind.Idempotent,
            Idempotency.Effective(Snippet(Idempotency.Idempotent), IdempotencyKind.RequiresCompensation));
    }

    [Fact]
    public void An_author_may_also_escalate()
    {
        Assert.Equal(
            IdempotencyKind.NonReversible,
            Idempotency.Effective(Snippet(Idempotency.NonReversible), IdempotencyKind.Idempotent));
    }

    // The one hard ceiling. Sending an email or pushing a commit has no
    // compensation; letting an author declare one would make a failed production
    // run report "rolled_back" having undone nothing.
    [Theory]
    [InlineData(Idempotency.Idempotent)]
    [InlineData(Idempotency.RequiresCompensation)]
    public void A_non_reversible_handler_cannot_be_downgraded(string declared)
    {
        Assert.Equal(
            IdempotencyKind.NonReversible,
            Idempotency.Effective(Snippet(declared), IdempotencyKind.NonReversible));
    }

    [Fact]
    public void An_unrecognised_declaration_falls_back_to_the_conservative_tier()
    {
        // Parse's default. A typo must not read as "safe".
        Assert.Equal(
            IdempotencyKind.RequiresCompensation,
            Idempotency.Effective(Snippet("whatever"), IdempotencyKind.Idempotent));
    }

    // The change signal, which used to be inferred here and is now resolved.
    //
    // What this replaces: `AggregateChanged(anyChanged, anyUnspecified, tier)`, which read
    // `anyChanged || (anyUnspecified && tier != Idempotent)`. Silence meant "changed" unless
    // the handler was idempotent, and most handlers are not — so most steps were recorded as
    // having mutated something because nobody said otherwise, and the audit trail, the
    // rollback plan and the run's final state were all computed on top of that.
    //
    // The tier is no longer consulted. A tier says whether an action COULD be undone; this
    // says whether anything WAS done.

    private static WorkflowNode Node(string? overrides = null) =>
        new("n1", Guid.NewGuid().ToString(), "task",
            overrides is null
                ? default
                : System.Text.Json.JsonDocument.Parse(overrides).RootElement.Clone());

    private static SnippetEntity Deferring(bool? declares = null) => new()
    {
        SnippetId = Guid.NewGuid(), Name = "s", Type = "ssh", ChangesState = declares,
    };

    [Theory]
    [InlineData(StepChange.Changed, true)]
    [InlineData(StepChange.Unchanged, false)]
    public void A_handler_that_answers_is_believed(StepChange reported, bool expected)
    {
        // Whatever the author or the tier might say, a handler that measured wins.
        Assert.Equal(expected,
            Services.Workflow.SnippetNodeExecutor.ResolveChange(reported, Deferring(declares: !expected), Node()));
    }

    [Fact]
    public void Where_the_handler_cannot_know_the_node_declares()
    {
        Assert.True(Services.Workflow.SnippetNodeExecutor.ResolveChange(
            StepChange.AuthorDecides, Deferring(), Node("""{"changes":true}""")));
        Assert.False(Services.Workflow.SnippetNodeExecutor.ResolveChange(
            StepChange.AuthorDecides, Deferring(), Node("""{"changes":false}""")));
    }

    [Fact]
    public void The_node_wins_over_the_snippet_being_more_specific()
    {
        // The same ssh snippet runs `show version` on one node and `configure terminal` on
        // another, so the node is where the action actually lives.
        Assert.True(Services.Workflow.SnippetNodeExecutor.ResolveChange(
            StepChange.AuthorDecides, Deferring(declares: false), Node("""{"changes":true}""")));
    }

    [Fact]
    public void The_snippet_declares_for_the_types_whose_code_it_carries()
    {
        Assert.True(Services.Workflow.SnippetNodeExecutor.ResolveChange(
            StepChange.AuthorDecides, Deferring(declares: true), Node()));
    }

    [Fact]
    public void Nobody_declaring_is_a_defect_not_a_default()
    {
        // The whole point. The old code answered here, from the tier, and was wrong most of
        // the time. Null means the step fails and names what to set — louder, and true.
        Assert.Null(Services.Workflow.SnippetNodeExecutor.ResolveChange(
            StepChange.AuthorDecides, Deferring(), Node()));
    }

    [Fact]
    public void A_malformed_declaration_is_not_a_declaration()
    {
        // A typo must not be coerced into an answer — that is the failure mode being removed.
        Assert.Null(Services.Workflow.SnippetNodeExecutor.ResolveChange(
            StepChange.AuthorDecides, Deferring(), Node("""{"changes":"yes"}""")));
    }

    [Fact]
    public void Only_non_reversible_blocks_rollback()
    {
        Assert.True(Idempotency.IsReversible(IdempotencyKind.Idempotent));
        Assert.True(Idempotency.IsReversible(IdempotencyKind.RequiresCompensation));
        Assert.False(Idempotency.IsReversible(IdempotencyKind.NonReversible));
    }

    // ── the two floors that were above the contract's default ────────────

    // python_snippet and mcp_call used to ship NonReversible. Because that tier is
    // absolute in Effective(), the declaration below could not take effect: a workflow
    // whose author had said "this is compensable" ran as if they had not, and a failed
    // run reported `failed` where the contract's oracle reports `rolled_back`.
    [Fact]
    public void A_compensable_declaration_now_takes_effect_on_a_script_step()
    {
        var snippet = new SnippetEntity
        {
            SnippetId = Guid.NewGuid(),
            Name = "s",
            Type = "python_snippet",
            Idempotency = Idempotency.RequiresCompensation,
        };

        Assert.Equal(
            IdempotencyKind.RequiresCompensation,
            Idempotency.Effective(snippet, IdempotencyKind.RequiresCompensation));
    }

    // Lowering the floor did not remove the author's ability to raise it, which is the
    // half that protects a genuinely irreversible tool.
    [Fact]
    public void An_author_may_still_declare_a_script_step_irreversible()
    {
        var snippet = new SnippetEntity
        {
            SnippetId = Guid.NewGuid(),
            Name = "s",
            Type = "python_snippet",
            Idempotency = Idempotency.NonReversible,
        };

        Assert.Equal(
            IdempotencyKind.NonReversible,
            Idempotency.Effective(snippet, IdempotencyKind.RequiresCompensation));
    }
}
