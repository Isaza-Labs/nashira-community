using System.Text.Json;

namespace nashira_backend.Services.Workflow;

// Idempotency tier of a node's action, reified from flow-weaver:
//   Idempotent            — safe to re-run with the same input.
//   RequiresCompensation  — has side effects; a failure edge must wire the reversal.
//   NonReversible         — no automatic compensation possible (email, commit, push).
// The tier is a property of the SNIPPET, not of the node (execution/SPEC.md §2). The
// enum is ordered so that `>` means "stricter", which is what the stricter-only
// override rule below compares.
public enum IdempotencyKind
{
    Idempotent = 0,
    RequiresCompensation = 1,
    NonReversible = 2,
}

public static class Idempotency
{
    public const string Idempotent = "idempotent";
    public const string RequiresCompensation = "requires_compensation";
    public const string NonReversible = "non_reversible";

    public static IdempotencyKind Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        Idempotent => IdempotencyKind.Idempotent,
        RequiresCompensation => IdempotencyKind.RequiresCompensation,
        NonReversible => IdempotencyKind.NonReversible,
        _ => IdempotencyKind.RequiresCompensation, // conservative default for an unannotated node
    };

    public static string ToWire(IdempotencyKind kind) => kind switch
    {
        IdempotencyKind.Idempotent => Idempotent,
        IdempotencyKind.NonReversible => NonReversible,
        _ => RequiresCompensation,
    };

    // Reversible = can be automatically rolled back (idempotent replay or compensation).
    public static bool IsReversible(IdempotencyKind kind) => kind != IdempotencyKind.NonReversible;

    /// <summary>
    /// The tier a node's <c>config_overrides.idempotency</c> asks for, or null when it
    /// asks for nothing. Only ever read through <see cref="Effective"/>, which applies
    /// the stricter-only rule — this is the raw read, exposed so a caller can say that
    /// an override was ignored.
    /// </summary>
    public static IdempotencyKind? DeclaredOverride(JsonElement configOverrides)
    {
        if (configOverrides.ValueKind != JsonValueKind.Object
            || !configOverrides.TryGetProperty("idempotency", out var v)
            || v.ValueKind != JsonValueKind.String) return null;
        var raw = v.GetString()?.Trim().ToLowerInvariant();
        // An unrecognised word is not an override. Parse() answers RequiresCompensation
        // for anything it does not know, which would silently RAISE an idempotent
        // snippet on a typo.
        return raw is Idempotent or RequiresCompensation or NonReversible ? Parse(raw) : null;
    }

    // The tier that actually applies to a step: the handler's default unless the
    // snippet declares otherwise.
    //
    // One asymmetry, and it is the load-bearing part: a NonReversible handler is
    // absolute. Sending an email or pushing a commit has no compensation, so an
    // author must not be able to declare one — the rollback planner would then
    // promise a reversal that does not exist, and a failed production run would
    // report "rolled_back" having undone nothing.
    //
    // Below that ceiling the author is trusted, because they are the only one who
    // can know: `integration_action` defaults to RequiresCompensation since the
    // handler cannot tell a GET from a DELETE, and an author who wrapped a
    // verified read is right to mark it idempotent.
    // The one place a tier is decided. `config_overrides.idempotency` is an accepted
    // STRICTER-ONLY override (execution/SPEC.md §2): it may raise the snippet's tier,
    // never lower it. There used to be a second function reading only the node, which
    // meant the executor and the rollback analyser scored an `email_send` node
    // reversible while the step executor scored it non-reversible — and the run
    // reported `rolled_back` for an email it could not unsend.
    public static IdempotencyKind Effective(
        Data.Models.Snippet snippet, IdempotencyKind handlerFloor, JsonElement configOverrides = default)
    {
        var tier = handlerFloor == IdempotencyKind.NonReversible
            ? IdempotencyKind.NonReversible
            : string.IsNullOrWhiteSpace(snippet.Idempotency) ? handlerFloor : Parse(snippet.Idempotency);

        var declared = DeclaredOverride(configOverrides);
        return declared is { } d && d > tier ? d : tier;
    }
}
