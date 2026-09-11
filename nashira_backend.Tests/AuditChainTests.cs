using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;

namespace nashira_backend.Tests;

// The audit hash chain is the tamper-evidence guarantee (§7.1-bis): deterministic
// per-row hashes linked by PrevHash, so any edit or reorder is detectable.
public class AuditChainTests
{
    private static readonly DateTime T0 = new(2026, 7, 3, 10, 0, 0, DateTimeKind.Utc);

    private static AuditEvent Make(long seq, string? prev, string action, string? after, DateTime at)
        => Make(seq, prev, action, after, at, actor: null, version: AuditHashVersion.Current);

    private static AuditEvent Make(
        long seq, string? prev, string action, string? after, DateTime at,
        string? actor, int version)
    {
        var hash = AuditChain.ComputeHash(
            seq, prev, null, actor, "agent.tool", null, action, null, after, at, version);
        return new AuditEvent
        {
            AuditEventId = Guid.NewGuid(),
            Sequence = seq,
            EntityType = "agent.tool",
            Action = action,
            Actor = actor,
            HashVersion = version,
            AfterJson = after,
            At = at,
            PrevHash = prev,
            Hash = hash,
        };
    }

    // Rows written before Actor existed were signed without it. Verifying them under
    // the new canonical form would report the entire trail as tampered with the moment
    // the column landed — so each row is checked against the form it declares.
    [Fact]
    public void A_v1_row_still_verifies_after_actor_was_added()
    {
        var v1 = Make(1, null, "list_apis", null, T0, actor: null, version: AuditHashVersion.WithoutActor);
        var result = AuditChain.Verify([v1]);
        Assert.True(result.Valid);
    }

    // A trail that spans the change verifies end to end: old rows under v1, new ones
    // under v2, linked by the same prev_hash chain.
    [Fact]
    public void A_chain_mixing_both_versions_verifies()
    {
        var v1 = Make(1, null, "list_apis", null, T0, actor: null, version: AuditHashVersion.WithoutActor);
        var v2 = Make(2, v1.Hash, "run_workflow", null, T0.AddSeconds(1),
            actor: "workflow-runner", version: AuditHashVersion.WithActor);

        var result = AuditChain.Verify([v1, v2]);
        Assert.True(result.Valid);
    }

    // The point of binding Actor into v2: on a row with no user, Actor is the only
    // statement of who acted, so it has to be as tamper-evident as the rest.
    [Fact]
    public void Rewriting_the_actor_of_a_v2_row_breaks_it()
    {
        var row = Make(1, null, "run_workflow", null, T0,
            actor: "workflow-runner", version: AuditHashVersion.WithActor);

        row.Actor = "admin";

        var result = AuditChain.Verify([row]);
        Assert.False(result.Valid);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    // …and it is genuinely bound, not merely stored: the same content under a
    // different actor hashes differently.
    [Fact]
    public void Actor_changes_the_v2_hash()
    {
        var a = AuditChain.ComputeHash(1, null, null, "workflow-runner", "agent.tool", null, "run", null, null, T0);
        var b = AuditChain.ComputeHash(1, null, null, "scheduler", "agent.tool", null, "run", null, null, T0);
        Assert.NotEqual(a, b);
    }

    // v1's canonical form does not cover Actor, so an Actor written onto a v1 row
    // afterwards would be invisible to its hash and the chain would certify the row as
    // intact. On rows with no user that field is the only statement of who acted, which
    // makes it precisely the forgery worth making.
    [Fact]
    public void A_v1_row_carrying_an_actor_is_refused()
    {
        var row = Make(1, null, "run_workflow", null, T0, actor: null, version: AuditHashVersion.WithoutActor);

        // What an UPDATE against the table would do.
        row.Actor = "alice";

        var result = AuditChain.Verify([row]);
        Assert.False(result.Valid);
        Assert.Equal(1, result.BrokenAtSequence);
    }

    // A row claiming a canonical form this build does not implement cannot be
    // verified, and must not pass by omission.
    [Fact]
    public void An_unknown_hash_version_is_refused()
    {
        var row = Make(1, null, "list_apis", null, T0, actor: null, version: 99);
        var result = AuditChain.Verify([row]);
        Assert.False(result.Valid);
        Assert.Contains("hash version", result.Reason ?? string.Empty);
    }

    [Fact]
    public void ComputeHash_is_deterministic_and_content_sensitive()
    {
        var h1 = AuditChain.ComputeHash(1, null, null, null, "agent.tool", null, "device_connect", null, "{\"ok\":true}", T0);
        var h2 = AuditChain.ComputeHash(1, null, null, null, "agent.tool", null, "device_connect", null, "{\"ok\":true}", T0);
        var h3 = AuditChain.ComputeHash(1, null, null, null, "agent.tool", null, "device_connect", null, "{\"ok\":false}", T0);

        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.Equal(64, h1.Length); // sha256 hex
    }

    [Fact]
    public void Verify_accepts_an_intact_chain()
    {
        var e1 = Make(1, null, "a", "{\"n\":1}", T0);
        var e2 = Make(2, e1.Hash, "b", "{\"n\":2}", T0.AddSeconds(1));
        var e3 = Make(3, e2.Hash, "c", "{\"n\":3}", T0.AddSeconds(2));

        var r = AuditChain.Verify([e1, e2, e3]);

        Assert.True(r.Valid);
        Assert.Equal(3, r.Count);
        Assert.Null(r.BrokenAtSequence);
    }

    [Fact]
    public void Verify_detects_content_tampering()
    {
        var e1 = Make(1, null, "a", "{\"n\":1}", T0);
        var e2 = Make(2, e1.Hash, "b", "{\"n\":2}", T0.AddSeconds(1));
        e2.Action = "TAMPERED"; // hash no longer matches the row content

        var r = AuditChain.Verify([e1, e2]);

        Assert.False(r.Valid);
        Assert.Equal(2, r.BrokenAtSequence);
    }

    [Fact]
    public void Verify_detects_broken_linkage()
    {
        var e1 = Make(1, null, "a", null, T0);
        var e2 = Make(2, "deadbeef", "b", null, T0.AddSeconds(1)); // prev hash doesn't link to e1

        var r = AuditChain.Verify([e1, e2]);

        Assert.False(r.Valid);
        Assert.Equal(2, r.BrokenAtSequence);
    }

    // The tests above all use whole-second timestamps, which is why they stayed green
    // while every PostgreSQL deployment reported its chain broken at row 1: a real
    // DateTime.UtcNow carries 100-nanosecond ticks and the column only keeps microseconds,
    // so the row that came back was never the row that was hashed.
    private static DateTime AsStoredByPostgres(DateTime at) =>
        new(at.Ticks - (at.Ticks % 10), DateTimeKind.Utc);

    [Fact]
    public void Verify_accepts_a_chain_whose_timestamps_lost_precision_in_the_database()
    {
        // .5873634 — a tick-precision instant, exactly what DateTime.UtcNow produces.
        var at = new DateTime(2026, 8, 13, 18, 21, 59, DateTimeKind.Utc).AddTicks(5_873_634);
        Assert.NotEqual(at, AsStoredByPostgres(at)); // the write really does lose a digit

        var e1 = Make(1, null, "a", "{\"n\":1}", at);
        var e2 = Make(2, e1.Hash, "b", "{\"n\":2}", at.AddTicks(7));

        // Read back: the sub-microsecond digit is gone.
        e1.At = AsStoredByPostgres(e1.At);
        e2.At = AsStoredByPostgres(e2.At);

        var r = AuditChain.Verify([e1, e2]);

        Assert.True(r.Valid, r.Reason);
    }

    [Fact]
    public void ComputeHash_ignores_precision_the_database_cannot_store()
    {
        var at = new DateTime(2026, 8, 13, 18, 21, 59, DateTimeKind.Utc).AddTicks(5_873_630);

        var h1 = AuditChain.ComputeHash(1, null, null, null, "agent.tool", null, "a", null, null, at);
        var h2 = AuditChain.ComputeHash(1, null, null, null, "agent.tool", null, "a", null, null, at.AddTicks(4));

        Assert.Equal(h1, h2);
    }

    // Tolerating the lost digit must not tolerate an edit: the ten candidate timestamps
    // are ten guesses at the original input, not ten chances to match different content.
    [Fact]
    public void Verify_still_detects_tampering_on_a_row_written_before_the_fix()
    {
        var at = new DateTime(2026, 8, 13, 18, 21, 59, DateTimeKind.Utc).AddTicks(5_873_634);

        var legacy = Make(1, null, "device_connect", "{\"ok\":true}", at);
        legacy.At = AsStoredByPostgres(legacy.At);
        Assert.True(AuditChain.Verify([legacy]).Valid); // intact before the edit

        legacy.AfterJson = "{\"ok\":false}";

        var r = AuditChain.Verify([legacy]);

        Assert.False(r.Valid);
        Assert.Equal(1, r.BrokenAtSequence);
    }
}
