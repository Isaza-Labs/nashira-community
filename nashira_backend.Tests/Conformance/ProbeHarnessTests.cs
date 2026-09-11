using System.Text.Json;
using nashira_backend.Tests.Conformance;

namespace nashira_backend.Tests;

// Self-tests for the conformance probe harnesses.
//
// A probe that cannot report a wrong answer is not a probe — it is a second copy of the
// implementation, agreeing with itself. These tests exist to prove the opposite for each
// harness before any vector relies on it: that it reads the product rather than a table
// kept beside it, and that it refuses rather than guesses when it has nothing to read.
//
// They are not conformance vectors and assert nothing about the contract. They assert that
// the instrument works.
public class ProbeHarnessTests
{
    private static NashiraAdapter Adapter()
    {
        var schemaPath = Path.Combine(
            AppContext.BaseDirectory, "conformance", "schema", "workflow.v1.schema.json");
        return new NashiraAdapter(File.ReadAllText(schemaPath));
    }

    private static string Tier(string type, string? declared, string? nodeOverride = null)
    {
        var input = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["declared_idempotency"] = declared,
        };
        if (nodeOverride is not null)
            input["config_overrides"] = new Dictionary<string, object?> { ["idempotency"] = nodeOverride };

        var actual = Adapter().Run("snippets", JsonSerializer.SerializeToElement(input));
        Assert.NotNull(actual);
        return actual!.Value.GetProperty("effective_idempotency").GetString()!;
    }

    // ── the harness reads the handler, not a copy of it ──────────────────────

    [Fact]
    public void An_undeclared_snippet_takes_the_handlers_own_floor()
    {
        // `report` declares Idempotent and `git` declares RequiresCompensation. Two
        // different answers from two different handlers is what shows the floor is being
        // read rather than defaulted: a harness returning a constant would pass one of
        // these and fail the other.
        Assert.Equal("idempotent", Tier("report", null));
        Assert.Equal("requires_compensation", Tier("git", null));
        Assert.Equal("non_reversible", Tier("ssh", null));
    }

    [Fact]
    public void The_probe_refuses_a_type_no_handler_declares()
    {
        // The alternative — answering the conservative default — is precisely how a
        // vector for a type this product does not implement would pass while proving
        // nothing. A skip has to be declared by the vector, never inferred here.
        var ex = Assert.Throws<InvalidOperationException>(() => Tier("not_a_real_type", null));
        Assert.Contains("not_a_real_type", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not_implemented", ex.Message, StringComparison.Ordinal);
    }

    // ── the harness reproduces the combination rule, both directions ─────────

    [Fact]
    public void An_irreversible_handler_cannot_be_softened()
    {
        // `email_send` ships non_reversible. Neither the snippet nor the node may talk it
        // down: a rollback plan that promised to unsend an email would report a step as
        // rolled back having undone nothing.
        Assert.Equal("non_reversible", Tier("email_send", "idempotent"));
        Assert.Equal("non_reversible", Tier("email_send", "requires_compensation"));
        Assert.Equal("non_reversible", Tier("email_send", null, nodeOverride: "idempotent"));
    }

    [Fact]
    public void Below_that_ceiling_the_snippet_may_declare_either_way()
    {
        // Both directions, because the rule is not "stricter only" here — the snippet's
        // author is the only party who can tell a verified read from a write, and both
        // products agree on that. A harness that enforced stricter-only would pass the
        // first of these and quietly fail the second.
        Assert.Equal("non_reversible", Tier("integration_action", "non_reversible"));
        Assert.Equal("idempotent", Tier("integration_action", "idempotent"));
    }

    [Fact]
    public void A_node_override_raises_and_never_lowers()
    {
        Assert.Equal("non_reversible", Tier("report", null, nodeOverride: "non_reversible"));
        Assert.Equal("requires_compensation", Tier("report", null, nodeOverride: "requires_compensation"));

        // Lowering is ignored: a node may not talk a shared snippet out of its tier.
        Assert.Equal("requires_compensation", Tier("git", null, nodeOverride: "idempotent"));
    }

    [Fact]
    public void An_unrecognised_tier_name_is_not_a_declaration()
    {
        // A typo must not be parsed into the conservative default, which would silently
        // RAISE an idempotent snippet and make a workflow stop rolling back.
        Assert.Equal("idempotent", Tier("report", null, nodeOverride: "idempotant"));
    }

    // ── the outbound-call probes ─────────────────────────────────────────────

    private static JsonElement Probe(string type, object probe, string? emit = null)
    {
        var input = new Dictionary<string, object?> { ["type"] = type, ["probe"] = probe };
        if (emit is not null) input["emit"] = emit;

        var actual = Adapter().Run("snippets", JsonSerializer.SerializeToElement(input));
        Assert.NotNull(actual);
        return actual!.Value;
    }

    [Fact]
    public void The_slack_probe_records_what_the_dispatcher_was_asked_to_send()
    {
        var r = Probe("slack_message", new { channel = "#netops", text = "audit finished" });
        var sent = r.GetProperty("sent");

        Assert.True(sent.GetProperty("called").GetBoolean());
        Assert.Equal("audit finished", sent.GetProperty("text").GetString());

        // Discriminating: a different node produces a different record. A probe that
        // returned a fixed shape would pass the assertion above and fail this one.
        var other = Probe("slack_message", new { channel = "#netops", text = "something else" });
        Assert.Equal("something else", other.GetProperty("sent").GetProperty("text").GetString());
    }

    [Fact]
    public void The_slack_probe_records_a_reply_reaching_its_thread()
    {
        // Inverted twice, and the second time is the interesting one. Before task 3.1 this
        // posted at the root of the channel and reported `ok: true` — output indistinguishable
        // from a correct threaded post, which is why a vector asserting output alone could
        // never have caught it.
        //
        // The harness now reaches for the transport that can thread, because that is the
        // adapter's job: a vector states what the contract requires and each product arranges
        // whatever it takes to answer. The webhook transport's refusal is this product's own
        // behaviour and is tested where it belongs, against the handler.
        var r = Probe("slack_message", new
        {
            channel = "#netops",
            text = "audit finished",
            thread_ts = "1724750400.000100",
        });

        var sent = r.GetProperty("sent");
        Assert.True(sent.GetProperty("called").GetBoolean());
        Assert.Equal("#netops", sent.GetProperty("channel").GetString());
        Assert.Equal("1724750400.000100", sent.GetProperty("thread_ts").GetString());
    }

    [Fact]
    public void The_slack_probe_records_rich_content_reaching_the_transport()
    {
        // `blocks` IS carriable by an incoming webhook, so it is delivered rather than
        // refused — the rule is honour-or-refuse, and refusing what the transport can do
        // would be the wrong half of it.
        var r = Probe("slack_message", new
        {
            channel = "#netops",
            text = "audit finished",
            blocks = new object[] { new { type = "section", text = new { type = "mrkdwn", text = "*done*" } } },
        });

        var blocks = r.GetProperty("sent").GetProperty("blocks");
        Assert.Equal(JsonValueKind.Array, blocks.ValueKind);
        Assert.Equal("section", blocks[0].GetProperty("type").GetString());
    }

    [Fact]
    public void The_rest_probe_records_which_operation_resolved()
    {
        var r = Probe("rest_call", new
        {
            source = "netbox",
            operation_id = "dcim_devices_list",
            query_params = new { site = "bog" },
        });
        var sent = r.GetProperty("sent");

        Assert.Equal("dcim_devices_list", sent.GetProperty("operation_id").GetString());

        // Inverted by task 3.4 (D5), which was the point of pinning it: the node's `source`
        // now reaches the executor and scopes the lookup, where before it was accepted and
        // ignored. Two catalogued specs sharing an id no longer resolve by load order.
        Assert.Equal("netbox", sent.GetProperty("source").GetString());

        // Discriminating.
        var other = Probe("rest_call", new { operation_id = "dcim_sites_list" });
        Assert.Equal("dcim_sites_list", other.GetProperty("sent").GetProperty("operation_id").GetString());
    }

    [Fact]
    public void The_ansible_probe_reports_every_key_the_play_receives()
    {
        // The whole resolved payload, targeting keys included — which is what both this
        // product and the oracle do, and what the contract was corrected to describe.
        var r = Probe("ansible_playbook", new
        {
            hosts = new[] { "core-1" },
            extra_vars = new { version = "15.2" },
            timeout_seconds = 30,
        });
        var vars = r.GetProperty("sent").GetProperty("play_variables");

        Assert.True(vars.TryGetProperty("hosts", out _));
        Assert.True(vars.TryGetProperty("timeout_seconds", out _));
        Assert.Equal("15.2", vars.GetProperty("extra_vars").GetProperty("version").GetString());

        // Discriminating: a key the node did not carry is not invented.
        Assert.False(vars.TryGetProperty("not_supplied", out _));
    }

    // ── python: a real interpreter, and both authoring conventions ───────────

    [Fact]
    public void The_python_probe_runs_a_real_interpreter_and_discriminates()
    {
        var three = Probe("python_snippet", new { total = 3 });
        var four = Probe("python_snippet", new { total = 4 });

        Assert.NotEqual(
            three.GetProperty("output").GetRawText(),
            four.GetProperty("output").GetRawText());
    }

    [Fact]
    public void Both_python_authoring_conventions_reach_the_same_output()
    {
        // Inverted by tasks 4.1 and 4.2b, and kept because it is the record of what those
        // changed. Before them:
        //   - the assigned value sat under `result`, so a portable template reading `.total`
        //     resolved to nothing;
        //   - a script authored against the oracle printed its JSON, assigned nothing, and
        //     had its value lost to the logs entirely.
        // The second is the one no output-shape vector could ever have caught: unwrapping
        // the envelope satisfies the shape while a printing script still returns nothing.
        var assigned = Probe("python_snippet", new { total = 3 }, emit: "assign");
        var printed = Probe("python_snippet", new { total = 3 }, emit: "print");

        Assert.Equal(3, assigned.GetProperty("output").GetProperty("total").GetInt32());
        Assert.Equal(3, printed.GetProperty("output").GetProperty("total").GetInt32());

        // The same script, said two ways, is the same step.
        Assert.Equal(
            assigned.GetProperty("output").GetRawText(),
            printed.GetProperty("output").GetRawText());
    }

    [Fact]
    public void The_python_probe_refuses_an_emit_mode_it_does_not_know()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Probe("python_snippet", new { total = 3 }, emit: "return"));
        Assert.Contains("assign", ex.Message, StringComparison.Ordinal);
    }
}
