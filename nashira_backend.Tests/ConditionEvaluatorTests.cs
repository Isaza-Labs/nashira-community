using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Engine;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// Conditional edges. Before these were evaluated, a branch ran both arms — the
// schema allowed the condition string and nothing read it.
public class ConditionEvaluatorTests
{
    private static readonly ConditionEvaluator Eval = new(NullLogger<ConditionEvaluator>.Instance);

    private static Dictionary<string, StepResult> Steps(params (string Node, string Output)[] steps) =>
        steps.ToDictionary(
            s => s.Node,
            s => new StepResult(JsonDocument.Parse(s.Output).RootElement.Clone()),
            StringComparer.Ordinal);

    [Theory]
    [InlineData("{{ steps.ping.output.reachable }} == true", true)]
    [InlineData("{{ steps.ping.output.reachable }} != true", false)]
    [InlineData("{{ steps.ping.output.latency_ms }} < 100", true)]
    [InlineData("{{ steps.ping.output.latency_ms }} > 100", false)]
    [InlineData("{{ steps.ping.output.latency_ms }} >= 42", true)]
    [InlineData("{{ steps.ping.output.latency_ms }} <= 41", false)]
    public void Comparisons_evaluate(string expression, bool expected)
    {
        var steps = Steps(("ping", """{"reachable":true,"latency_ms":42}"""));
        Assert.Equal(expected, Eval.Evaluate(expression, steps));
    }

    // String comparison would say "10" < "9". Numbers have to compare as numbers.
    [Fact]
    public void Numeric_comparison_does_not_fall_back_to_string_ordering()
    {
        var steps = Steps(("s", """{"n":10}"""));
        Assert.True(Eval.Evaluate("{{ steps.s.output.n }} > 9", steps));
    }

    [Fact]
    public void String_equality_works_quoted_and_unquoted()
    {
        var steps = Steps(("s", """{"status":"active"}"""));
        Assert.True(Eval.Evaluate("""{{ steps.s.output.status }} == "active" """, steps));
        Assert.True(Eval.Evaluate("{{ steps.s.output.status }} == active", steps));
    }

    [Fact]
    public void And_requires_every_part()
    {
        var steps = Steps(("rest", """{"status_code":204}"""));
        Assert.True(Eval.Evaluate(
            "{{ steps.rest.output.status_code }} >= 200 && {{ steps.rest.output.status_code }} < 300", steps));
        Assert.False(Eval.Evaluate(
            "{{ steps.rest.output.status_code }} >= 200 && {{ steps.rest.output.status_code }} < 204", steps));
    }

    [Fact]
    public void Or_requires_only_one_part()
    {
        var steps = Steps(("rest", """{"status_code":404}"""));
        Assert.True(Eval.Evaluate(
            "{{ steps.rest.output.status_code }} == 200 || {{ steps.rest.output.status_code }} == 404", steps));
    }

    [Theory]
    [InlineData("""{"v":true}""", true)]
    [InlineData("""{"v":false}""", false)]
    [InlineData("""{"v":1}""", true)]
    [InlineData("""{"v":0}""", false)]
    [InlineData("""{"v":"text"}""", true)]
    [InlineData("""{"v":""}""", false)]
    [InlineData("""{"v":null}""", false)]
    [InlineData("""{"v":[]}""", false)]
    public void A_bare_reference_is_evaluated_for_truthiness(string output, bool expected)
    {
        Assert.Equal(expected, Eval.Evaluate("{{ steps.s.output.v }}", Steps(("s", output))));
    }

    // Fail-closed. A conditional edge exists to gate something; firing it because
    // the gate could not be evaluated is the worse of the two failures.
    [Theory]
    [InlineData("{{ steps.missing.output.x }} == true")]
    [InlineData("{{ steps.s.output.absent }} == true")]
    [InlineData("{{ device.ip }} == 10.0.0.1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("total gibberish ===")]
    public void An_unevaluable_expression_is_false(string expression)
    {
        Assert.False(Eval.Evaluate(expression, Steps(("s", """{"v":1}"""))));
    }

    [Fact]
    public void Parentheses_are_unwrapped()
    {
        var steps = Steps(("s", """{"n":5}"""));
        Assert.True(Eval.Evaluate("({{ steps.s.output.n }} == 5)", steps));
    }

    // templates/SPEC.md §9 puts `'(' <expr> ')'` in the grammar. The splitter used to be
    // paren-blind (`string.Split`), so `(a || b) && c` became the fragments `(a` and
    // `b) && c` and the whole expression failed closed.
    //
    // The fixture matters as much as the assertion. Its predecessor used a:9, b:9 —
    // both OR arms false — so the grouped answer and the broken answer were BOTH false
    // and the test passed either way: it was named for the bug and pinned it. With a:1
    // the two diverge, so this can only pass if grouping actually works.
    [Fact]
    public void A_parenthesized_group_is_evaluated_as_a_group()
    {
        var steps = Steps(("s", """{"a":1,"b":9,"c":3}"""));

        Assert.True(Eval.Evaluate(
            "({{ steps.s.output.a }} == 1 || {{ steps.s.output.b }} == 2) && {{ steps.s.output.c }} == 3",
            steps));

        // And the group is load-bearing: same operands, failing right-hand side.
        Assert.False(Eval.Evaluate(
            "({{ steps.s.output.a }} == 1 || {{ steps.s.output.b }} == 2) && {{ steps.s.output.c }} == 4",
            steps));
    }

    // The same split was blind to quotes, and THAT one failed open: `== "a||b"` cut
    // inside the literal and left the fragment `b"` — a non-empty string, therefore
    // truthy, therefore the edge fired.
    [Fact]
    public void A_separator_or_operator_inside_a_quoted_literal_is_data()
    {
        var steps = Steps(("s", """{"v":"a||b","w":"x==y"}"""));

        Assert.True(Eval.Evaluate("""{{ steps.s.output.v }} == "a||b" """, steps));
        Assert.False(Eval.Evaluate("""{{ steps.s.output.v }} == "zzz" """, steps));
        // `>` is the operator the author wrote; the `==` inside the literal is not.
        Assert.False(Eval.Evaluate("""{{ steps.s.output.w }} > "x==z" """, steps));
    }

    // §9: any parse error is false. The old operator scan skipped a hit at index 0, so
    // a missing left operand fell through to bare-value truthiness and opened the gate.
    [Theory]
    [InlineData("""== "x" """)]
    [InlineData("{{ steps.s.output.v }} ==")]
    [InlineData("""{{ steps.s.output.v }} == "unterminated""")]
    public void A_malformed_expression_is_false(string expression)
    {
        Assert.False(Eval.Evaluate(expression, Steps(("s", """{"v":"a"}"""))));
    }

    // A step's output is data, never grammar. Substitution used to run four chained
    // passes, so a value spliced in by the steps pass was scanned again by the
    // input/run/device passes: a device echoing `{{ input.api_token }}` — a banner, a
    // command that prints its own arguments — got that reference resolved, and remote
    // text could steer the branch using the caller's secret.
    [Fact]
    public void A_reference_appearing_inside_step_output_is_not_resolved()
    {
        var steps = Steps(("s", """{"banner":"{{ input.api_token }}"}"""));
        var input = JsonDocument.Parse("""{"api_token":"SECRET"}""").RootElement.Clone();

        // Control: the same reference, written by the AUTHOR in the expression, resolves.
        Assert.True(Eval.Evaluate(
            """{{ input.api_token }} == "SECRET" """, steps, runInput: input));

        // The identical text, arriving inside a step's output, does not. Origin is the
        // whole difference, and it is the difference the chained passes erased.
        Assert.False(Eval.Evaluate(
            """{{ steps.s.output.banner }} == "SECRET" """, steps, runInput: input));
    }

    // Nor may output restructure the expression around it: the value is spliced in
    // quoted, so a separator inside it stays part of one operand.
    [Fact]
    public void Step_output_cannot_restructure_the_expression()
    {
        var steps = Steps(("s", """{"v":"x\" || \"y"}"""));

        Assert.False(Eval.Evaluate("""{{ steps.s.output.v }} == "nope" """, steps));
        Assert.True(Eval.Evaluate("""{{ steps.s.output.v }} == "x\" || \"y" """, steps));
    }

    [Fact]
    public void A_truthy_fragment_with_a_stray_paren_is_false()
    {
        // The exact shape of the old bug: `({{flag}} || x` → fragment `(true`.
        var steps = Steps(("s", """{"flag":true}"""));
        Assert.False(Eval.Evaluate("({{ steps.s.output.flag }} || {{ steps.s.output.flag }}", steps));
    }

    [Fact]
    public void Balanced_parens_inside_operands_still_compare()
    {
        // A legitimate value containing parentheses must not be caught by the
        // balance check — only cut-through groups are.
        var steps = Steps(("s", """{"status":"reload (planned)"}"""));
        Assert.True(Eval.Evaluate(
            """{{ steps.s.output.status }} == "reload (planned)" """, steps));
    }

    [Fact]
    public void Array_paths_resolve_the_same_way_as_in_a_payload()
    {
        // The two grammars must agree — otherwise an author writes a condition that
        // reads correctly and evaluates against nothing.
        var steps = Steps(("ssh", """{"results":[{"ok":true}]}"""));
        Assert.True(Eval.Evaluate("{{ steps.ssh.output.results[0].ok }} == true", steps));
    }
}

// The executor's edge firing, now that `conditional` means something.
public class ConditionalEdgeTests
{
    private sealed class FixedExecutor(Dictionary<string, NodeOutcome> outcomes) : INodeExecutor
    {
        public Task<NodeOutcome> ExecuteAsync(WorkflowNode node, CancellationToken ct) =>
            Task.FromResult(outcomes.GetValueOrDefault(node.Id, new NodeOutcome(NodeResult.NoChange)));
    }

    private static Dag BuildDag(string condition)
    {
        var nodes = JsonDocument.Parse("""
            [{"id":"probe","snippet_id":"__start__"},
             {"id":"remediate","snippet_id":"__end__"}]
            """).RootElement;
        var edges = JsonDocument.Parse($$"""
            [{"source":"probe","target":"remediate","type":"conditional","condition":"{{condition}}"}]
            """).RootElement;
        return Dag.Parse(nodes, edges);
    }

    private static IReadOnlyDictionary<string, StepResult> Outputs(string probeOutput) =>
        new Dictionary<string, StepResult>(StringComparer.Ordinal)
        {
            ["probe"] = new(JsonDocument.Parse(probeOutput).RootElement.Clone()),
        };

    [Fact]
    public async Task A_conditional_edge_whose_condition_holds_fires()
    {
        var dag = BuildDag("{{ steps.probe.output.reachable }} == false");
        var engine = new WorkflowExecutor(
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            () => Outputs("""{"reachable":false}"""));

        var result = await engine.RunAsync(
            dag, new FixedExecutor([]), new WorkflowExecutionContext(Guid.NewGuid(), "h", "t", DateTime.UtcNow),
            stopOnFailure: true, CancellationToken.None);

        Assert.DoesNotContain(result.Steps, s => s.NodeId == "remediate" && s.Result == NodeResult.Skipped);
    }

    [Fact]
    public async Task A_conditional_edge_whose_condition_fails_skips_the_target()
    {
        var dag = BuildDag("{{ steps.probe.output.reachable }} == false");
        var engine = new WorkflowExecutor(
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            () => Outputs("""{"reachable":true}"""));

        var result = await engine.RunAsync(
            dag, new FixedExecutor([]), new WorkflowExecutionContext(Guid.NewGuid(), "h", "t", DateTime.UtcNow),
            stopOnFailure: true, CancellationToken.None);

        Assert.Contains(result.Steps, s => s.NodeId == "remediate" && s.Result == NodeResult.Skipped);
    }

    // An evaluator but no run input or run context — the shape the conformance
    // harness and every pre-existing caller construct. Nothing resolves, so the
    // condition fails closed exactly as it did before the run context was plumbed
    // through; wiring the namespaces cannot change what these callers see.
    [Fact]
    public async Task With_an_evaluator_but_no_run_context_an_input_condition_fails_closed()
    {
        var dag = BuildDag("{{ input.apply }} == true");
        var engine = new WorkflowExecutor(
            new ConditionEvaluator(NullLogger<ConditionEvaluator>.Instance),
            () => Outputs("""{"reachable":true}"""));

        var result = await engine.RunAsync(
            dag, new FixedExecutor([]), new WorkflowExecutionContext(Guid.NewGuid(), "h", "t", DateTime.UtcNow),
            stopOnFailure: true, CancellationToken.None);

        Assert.Contains(result.Steps, s => s.NodeId == "remediate" && s.Result == NodeResult.Skipped);
    }

    // Adding an evaluator must not change how an existing workflow runs: with no
    // evaluator wired, `conditional` keeps firing like `success`. That is what the
    // conformance harness drives.
    [Fact]
    public async Task Without_an_evaluator_a_conditional_edge_still_fires_on_success()
    {
        var dag = BuildDag("{{ steps.probe.output.reachable }} == false");
        var engine = new WorkflowExecutor();

        var result = await engine.RunAsync(
            dag, new FixedExecutor([]), new WorkflowExecutionContext(Guid.NewGuid(), "h", "t", DateTime.UtcNow),
            stopOnFailure: true, CancellationToken.None);

        Assert.DoesNotContain(result.Steps, s => s.NodeId == "remediate" && s.Result == NodeResult.Skipped);
    }
}
