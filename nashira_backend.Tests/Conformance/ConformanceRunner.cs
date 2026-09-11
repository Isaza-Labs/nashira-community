using System.Text;
using System.Text.Json;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests.Conformance;

public sealed record ConformanceResult(string Id, string Family, string Status, string? Detail);

public sealed class ConformanceReport
{
    private readonly List<ConformanceResult> _results = [];

    public void Add(string id, string family, string status, string? detail) =>
        _results.Add(new ConformanceResult(id, family, status, detail));

    public int Passed => _results.Count(r => r.Status == "pass");
    public int Failed => _results.Count(r => r.Status == "fail");
    public int Skipped => _results.Count(r => r.Status == "skip");
    public int PassedInFamily(string family) => _results.Count(r => r.Family == family && r.Status == "pass");

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CONFORMANCE workflow.v1  pass={Passed} fail={Failed} skip={Skipped}");
        foreach (var family in _results.Select(r => r.Family).Distinct().OrderBy(f => f, StringComparer.Ordinal))
        {
            var fam = _results.Where(r => r.Family == family).ToList();
            sb.AppendLine($"  {family,-16} {fam.Count(r => r.Status == "pass")} pass  {fam.Count(r => r.Status == "fail")} fail  {fam.Count(r => r.Status == "skip")} skip");
        }
        foreach (var r in _results.Where(r => r.Status == "fail"))
            // ONE line per failure. A vector's `expected` is pretty-printed in its file and
            // arrives here spanning several lines, so writing it verbatim split a single
            // failure across many lines — and any consumer that reads this per line, including
            // the gate's own grouped report, then saw only the first fragment.
            sb.AppendLine($"  FAIL {r.Id}: {System.Text.RegularExpressions.Regex.Replace(r.Detail ?? string.Empty, @"\s+", " ").Trim()}");
        return sb.ToString();
    }
}

// The thin, language-specific adapter: maps a vector's input to Nashira's engine and
// normalizes the output to the vector's expected shape.
//
// Returning null means "this adapter does not handle that family", and the runner
// treats that as a FAILURE, not a skip. A family the engine genuinely cannot answer
// has to be declared by the vector itself (`"not_implemented": true`) — the kit's
// documented escape hatch. That asymmetry is the whole point: an adapter that
// silently answered nothing is how `subflow` was claimed and not executed, how a
// rollback reported `rolled_back` for a sent email, and how conditions failed open,
// all under a green suite. Silence is not a skip.
//
// The per-family methods live in NashiraAdapter.<Family>.cs.
public sealed partial class NashiraAdapter
{
    private readonly WorkflowSchemaValidator _schema;

    // Shared instance: JsonSchema.Net's schema registry is process-wide and refuses a
    // second registration of the same `$id`, so building one here as well would break
    // whichever suite happened to run second. See TestSchemas.
    public NashiraAdapter(string schemaJson) => _schema = TestSchemas.For(schemaJson);

    public JsonElement? Run(string family, JsonElement input) => family switch
    {
        "schema" => Schema(input),
        "canonicalization" => Canonicalization(input),
        "gate" => Gate(input),
        "templates" => Templates(input),
        "snippets" => Snippets(input),
        "executor" => Executor(input),
        "bundle" => Bundle(input),
        _ => null,
    };

    private static JsonElement? Gate(JsonElement input)
    {
        // Operation-level permission classification is not part of the reified contract
        // (idempotency is snippet-scoped in the oracle). None is authored; one that were
        // would now FAIL rather than vanish, and would have to declare
        // `"not_implemented": true` to be a legitimate skip.
        if (input.TryGetProperty("ops", out _)) return null;

        var state = Str(input, "workflow_state") ?? "";
        var action = Str(input, "action") ?? "";
        GateSimulation? sim = null;
        if (input.TryGetProperty("simulation", out var s) && s.ValueKind == JsonValueKind.Object)
            sim = new GateSimulation(
                Str(s, "schema_hash"),
                s.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True);

        var r = PromotionGate.Check(action, state, sim, Str(input, "current_schema_hash"));
        return JsonSerializer.SerializeToElement(new { http_status = r.HttpStatus, code = r.Code });
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private JsonElement Schema(JsonElement input)
    {
        var r = _schema.Validate(input);
        return JsonSerializer.SerializeToElement(new
        {
            valid = r.Valid,
            errors = r.Errors.Select(e => new { keyword = e.Keyword, path = e.Path }),
        });
    }

    private static JsonElement Canonicalization(JsonElement input)
    {
        if (input.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
        {
            var hashes = new List<string>();
            var forms = new List<string>();
            foreach (var v in variants.EnumerateArray())
            {
                hashes.Add(Hash(v));
                forms.Add(WorkflowCanonicalizer.Canonical(v));
            }
            return JsonSerializer.SerializeToElement(new
            {
                all_schema_hashes_equal = hashes.Distinct().Count() <= 1,
                all_canonical_forms_equal = forms.Distinct().Count() <= 1,
            });
        }
        if (input.TryGetProperty("before", out var before) && input.TryGetProperty("after", out var after))
            return JsonSerializer.SerializeToElement(new { schema_hash_changed = Hash(before) != Hash(after) });

        return JsonSerializer.SerializeToElement(new { });
    }

    private static string Hash(JsonElement doc)
    {
        var nodes = doc.TryGetProperty("nodes", out var n) ? n : default;
        var edges = doc.TryGetProperty("edges", out var e) ? e : default;
        return WorkflowCanonicalizer.ComputeSchemaHash(nodes, edges);
    }
}

// Loads pure-JSON vectors, dispatches to the adapter, applies the equivalence mode, and
// tallies pass/fail/skip. Skips are legitimate (unimplemented engine families); failures
// are contract violations and block the merge in CI.
public sealed class ConformanceRunner
{
    private readonly NashiraAdapter _adapter;

    public ConformanceRunner(NashiraAdapter adapter) => _adapter = adapter;

    public ConformanceReport Run(IEnumerable<string> vectorFiles)
    {
        var report = new ConformanceReport();
        foreach (var file in vectorFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Path.GetFileName(file) : Path.GetFileName(file);
            var family = root.TryGetProperty("family", out var famEl) ? famEl.GetString() ?? "unknown" : "unknown";
            var input = root.GetProperty("input");
            var expected = root.GetProperty("expected");
            var equivalence = root.TryGetProperty("equivalence", out var eq) ? eq.GetString() ?? "exact" : "exact";

            // The kit's ONE legitimate skip: the vector itself declares that this
            // behaviour is not required of an implementation yet. An adapter that
            // simply has nothing to say is a failure (see NashiraAdapter).
            if (root.TryGetProperty("not_implemented", out var ni) && ni.ValueKind == JsonValueKind.True)
            {
                report.Add(id, family, "skip", "vector declares not_implemented");
                continue;
            }

            var normalize = root.TryGetProperty("normalize", out var nz) && nz.ValueKind == JsonValueKind.Array
                ? nz.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).ToList()
                : [];

            JsonElement? actual;
            try
            {
                actual = _adapter.Run(family, input);
            }
            catch (Exception ex)
            {
                report.Add(id, family, "fail", $"adapter threw: {ex.Message}");
                continue;
            }

            if (actual is null)
            {
                report.Add(id, family, "fail",
                    $"the adapter does not handle family '{family}'. A vector whose family the adapter "
                    + "cannot answer FAILS; declare \"not_implemented\": true on the vector if the skip is deliberate.");
                continue;
            }

            var expectedN = Normalizer.Apply(expected, normalize);
            var actualN = Normalizer.Apply(actual.Value, normalize);
            var ok = Compare(expectedN, actualN, equivalence);
            report.Add(id, family, ok ? "pass" : "fail",
                ok ? null : $"expected {expectedN.GetRawText()} got {actualN.GetRawText()}");
        }
        return report;
    }

    private static bool Compare(JsonElement expected, JsonElement actual, string equivalence) => equivalence switch
    {
        "subset" => IsSubset(expected, actual),
        // `fields-present` compares the PRESENCE and TYPE of what `expected` names, not
        // the values (ci/run-conformance.md). It is what the snippets family needs for a
        // portable output set: the contract fixes the field names, never the numbers a
        // real handler produced.
        "fields-present" => FieldsPresent(expected, actual),
        // `normalized` is `exact` after the vector's normalize rules, which the caller
        // has already applied.
        _ => WorkflowCanonicalizer.Canonical(expected) == WorkflowCanonicalizer.Canonical(actual), // exact | normalized
    };

    // Every member `expected` names must exist in `actual` with the same JSON type.
    // Arrays compare element-wise by type over the elements `expected` lists.
    private static bool FieldsPresent(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (actual.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in expected.EnumerateObject())
            {
                if (!actual.TryGetProperty(p.Name, out var av)) return false;
                if (!FieldsPresent(p.Value, av)) return false;
            }
            return true;
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            if (actual.ValueKind != JsonValueKind.Array) return false;
            var e = expected.EnumerateArray().ToList();
            var a = actual.EnumerateArray().ToList();
            if (a.Count < e.Count) return false;
            return !e.Where((t, i) => !FieldsPresent(t, a[i])).Any();
        }
        // A `null` in the expected shape means "this field may be null or absent-valued":
        // presence was already established above, so any kind satisfies it.
        if (expected.ValueKind == JsonValueKind.Null) return true;
        return SameKind(expected.ValueKind, actual.ValueKind);
    }

    private static bool SameKind(JsonValueKind a, JsonValueKind b) =>
        a == b || (a is JsonValueKind.True or JsonValueKind.False && b is JsonValueKind.True or JsonValueKind.False);

    // Every member of `expected` must be present and equal in `actual`; extra actual members are ignored.
    private static bool IsSubset(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (actual.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in expected.EnumerateObject())
            {
                if (!actual.TryGetProperty(p.Name, out var av)) return false;
                if (!IsSubset(p.Value, av)) return false;
            }
            return true;
        }
        return WorkflowCanonicalizer.Canonical(expected) == WorkflowCanonicalizer.Canonical(actual);
    }
}
