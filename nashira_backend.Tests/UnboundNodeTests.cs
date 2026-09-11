using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// What a node does when its snippet reference is not a snippet.
//
// This branch used to return `no_change`, so a workflow whose nodes referenced
// nothing real ran to completion and reported "0 changed, 0 failed" — the same
// summary as a workflow that did its job and found everything already correct.
// An agent that invented `__ping__` as a snippet id produced a green run that
// pinged nothing, and no test noticed because none covered this path.
//
// The executor's other dependencies are never touched on this branch: it decides
// before the registry, the resolver or the service provider are consulted. They
// are passed as null deliberately, so the test fails loudly if that order ever
// changes and the branch starts reaching further than it should.
public class UnboundNodeTests
{
    private static SnippetNodeExecutor Executor()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"unbound-{Guid.NewGuid()}")
            .Options);

        return new SnippetNodeExecutor(
            db, null!, null!, null!, new WorkflowExecutionScope(),
            NullLogger<SnippetNodeExecutor>.Instance);
    }

    private static WorkflowNode Node(string id, string snippetId)
    {
        using var doc = JsonDocument.Parse(
            $$"""[{"id":"{{id}}","snippet_id":"{{snippetId}}"}]""");
        using var edges = JsonDocument.Parse("[]");
        return Dag.Parse(doc.RootElement.Clone(), edges.RootElement.Clone()).Nodes.Values.Single();
    }

    [Theory]
    [InlineData("__ping__")]
    [InlineData("ping")]
    [InlineData("__task__")]
    [InlineData("")]
    public async Task A_snippet_reference_that_is_not_an_id_fails_the_node(string snippetId)
    {
        var outcome = await Executor().ExecuteAsync(Node("n", snippetId), CancellationToken.None);

        Assert.Equal(NodeResult.Failed, outcome.Result);
        Assert.Equal("unbound_node", outcome.ErrorCode);
    }

    // The message has to name both the node and the reference: the author's next
    // move is to fix one of them, and "a node failed" does not say which.
    [Fact]
    public async Task The_failure_names_the_node_and_what_it_pointed_at()
    {
        var outcome = await Executor().ExecuteAsync(
            Node("ping_4222", "__ping__"), CancellationToken.None);

        Assert.NotNull(outcome.Output);
        using var doc = JsonDocument.Parse(outcome.Output!);
        var error = doc.RootElement.GetProperty("error").GetString();

        Assert.Contains("ping_4222", error);
        Assert.Contains("__ping__", error);
    }

    // Not retryable: running it again cannot bind a reference that does not exist,
    // and a retry loop over a typo is a slower way to reach the same failure.
    [Fact]
    public async Task An_unbound_node_is_not_retryable()
    {
        var outcome = await Executor().ExecuteAsync(Node("n", "nope"), CancellationToken.None);

        Assert.False(outcome.Retryable);
    }

    // `subflow` is the third literal workflow.v1 allows, and it names real work: another
    // workflow, identified by `config_overrides.subflow_workflow_id`. Without that key
    // there is no workflow to run, so the node fails rather than being skipped — a
    // skipped node stops every `success` edge out of it, and the downstream half of the
    // graph would silently never run while the run reported `completed`.
    //
    // This assertion used to read `Skipped` with no error code, pinning a build that
    // could not execute a subflow at all. Execution landed (execution/SPEC.md §5); what
    // is left on this path is the reference being absent.
    [Fact]
    public async Task A_subflow_without_a_workflow_id_fails_with_subflow_missing()
    {
        var outcome = await Executor().ExecuteAsync(Node("child", "subflow"), CancellationToken.None);

        Assert.Equal(NodeResult.Failed, outcome.Result);
        Assert.Equal("subflow_missing", outcome.ErrorCode);

        Assert.NotNull(outcome.Output);
        using var doc = JsonDocument.Parse(outcome.Output!);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.Contains("subflow_workflow_id", error);
        Assert.Contains("child", error);
    }

    // The structural bookends stay a genuine no-op — they are not bound to a
    // snippet because they are not meant to be, and failing them would break
    // every workflow rather than the broken ones.
    [Theory]
    [InlineData("__start__")]
    [InlineData("__end__")]
    public async Task Start_and_end_remain_no_change(string snippetId)
    {
        var outcome = await Executor().ExecuteAsync(Node("n", snippetId), CancellationToken.None);

        Assert.Equal(NodeResult.NoChange, outcome.Result);
        Assert.Null(outcome.ErrorCode);
    }
}
