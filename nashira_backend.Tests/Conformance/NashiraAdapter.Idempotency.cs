using System.Runtime.CompilerServices;
using System.Text.Json;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests.Conformance;

// Family `snippets`, third form (execution/SPEC.md §2).
//
//   { type, declared_idempotency, config_overrides? } -> { effective_idempotency }
//
// The tier a run would act on, given a snippet type and what the snippet declares. It is
// the one property of a snippet that no output can show: a step's payload looks identical
// whether the engine considered it reversible or not, and the difference only surfaces on
// the failure path, in production, as a run reporting `failed` where the oracle reported
// `rolled_back`.
//
// `declared_idempotency: null` means the snippet declares nothing, so the vector must be
// read by property PRESENCE and not by value — a JSON null is a declaration of absence
// here, not a missing key.
public sealed partial class NashiraAdapter
{
    private static JsonElement Tier(string type, JsonElement declared, JsonElement? configOverrides)
    {
        var snippet = new nashira_backend.Data.Models.Snippet
        {
            Type = type,
            // Anything that is not a string is "declares nothing". Idempotency.Effective
            // treats null/blank as absent, which is what a `null` in the vector means.
            Idempotency = declared.ValueKind == JsonValueKind.String ? declared.GetString() : null,
        };

        var effective = Idempotency.Effective(
            snippet, HandlerFloor(type), configOverrides ?? default);

        return JsonSerializer.SerializeToElement(new
        {
            effective_idempotency = Idempotency.ToWire(effective),
        });
    }

    // The floor as the HANDLER declares it, read off the handler itself rather than from a
    // table kept here.
    //
    // A copy of the tiers in the test project would be a second source of truth for the one
    // fact these vectors exist to pin, and it would keep passing on the day a handler's
    // declaration changed underneath it — the exact failure the kit is meant to make
    // impossible. So the property is read from an uninitialised instance: `DefaultIdempotency`
    // is a constant expression on every handler, so no constructor and no dependency is
    // needed to ask. A handler that ever made its tier depend on instance state would throw
    // here, loudly, which is the right answer — a floor that varies per instance is not a
    // floor.
    private static IdempotencyKind HandlerFloor(string type)
    {
        foreach (var t in typeof(ISnippetHandler).Assembly.GetTypes())
        {
            if (t.IsAbstract || t.IsInterface || !t.IsAssignableTo(typeof(ISnippetHandler))) continue;

            var probe = (ISnippetHandler)RuntimeHelpers.GetUninitializedObject(t);
            if (string.Equals(probe.Type, type, StringComparison.Ordinal))
                return probe.DefaultIdempotency;
        }

        throw new InvalidOperationException(
            $"no snippet handler declares the type '{type}', so its idempotency floor cannot be "
            + "read. A vector naming a type this product does not implement must declare "
            + "\"not_implemented\": true rather than be answered with a guess.");
    }
}
