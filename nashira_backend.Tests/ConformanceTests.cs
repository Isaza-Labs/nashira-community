using nashira_backend.Tests.Conformance;
using Xunit.Abstractions;

namespace nashira_backend.Tests;

// Runs the workflow.v1 conformance kit against Nashira's adapter. This is the CI hard
// gate: a failing vector fails the build.
//
// Two rules make the gate mean something, and both were missing while the families
// that matter most sat at zero vectors:
//
//   1. A vector whose family the adapter cannot answer FAILS. Only the vector itself
//      may declare a skip (`"not_implemented": true`). An adapter that silently
//      returned nothing is exactly how `subflow` was claimed and never executed, how a
//      rollback reported `rolled_back` for an email that had been sent, and how
//      conditions failed open — all of it under a green suite.
//   2. Per-family PASS FLOORS. Failed == 0 is satisfied by a directory nobody copied
//      and by a family nobody wrote a vector for; a floor is what turns "no failures"
//      into "this many behaviours were actually checked".
//
// Vectors under a `_pending/` directory are deliberately NOT run: they are cases where
// the vector and the implementation disagree and the disagreement is a decision for a
// human, not something to be settled by bending either side (kit spec §4.3). They are
// parked, listed, and left out of the gate rather than added red.
public class ConformanceTests
{
    private readonly ITestOutputHelper _out;

    public ConformanceTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Workflow_v1_conformance_has_no_failures()
    {
        var baseDir = AppContext.BaseDirectory;
        var schemaPath = Path.Combine(baseDir, "conformance", "schema", "workflow.v1.schema.json");
        var vectorsDir = Path.Combine(baseDir, "conformance", "vectors");
        Assert.True(File.Exists(schemaPath), $"conformance schema missing at {schemaPath}");
        Assert.True(Directory.Exists(vectorsDir), $"conformance vectors missing at {vectorsDir}");

        var all = Directory.GetFiles(vectorsDir, "*.json", SearchOption.AllDirectories);
        var parked = all.Where(f => IsPending(f, vectorsDir)).ToList();
        var vectors = all.Where(f => !IsPending(f, vectorsDir)).ToList();

        var runner = new ConformanceRunner(new NashiraAdapter(File.ReadAllText(schemaPath)));
        var report = runner.Run(vectors);
        _out.WriteLine(report.Summary());

        // Parked vectors are NAMED, not merely excluded. Everything else in this file exists
        // to stop a check from disappearing quietly, and a directory whose contents vanish
        // from the report would be that same failure wearing the kit's own escape hatch as a
        // disguise. Zero of them is the normal state and prints nothing.
        if (parked.Count > 0)
        {
            _out.WriteLine($"PARKED {parked.Count} vector(s), excluded from the gate — each is a "
                + "decision someone owes, not a passing check:");
            foreach (var f in parked.OrderBy(f => f, StringComparer.Ordinal))
                _out.WriteLine($"  {Path.GetRelativePath(vectorsDir, f)}");
        }

        Assert.True(report.Failed == 0, report.Summary());

        // Floors track what is landed today. Raising one is a contract change like any
        // other; lowering one to make a red suite green is the thing this file exists
        // to prevent.
        AssertFloor(report, "schema", 4);
        AssertFloor(report, "canonicalization", 2);
        AssertFloor(report, "gate", 4);
        AssertFloor(report, "templates", 79);
        AssertFloor(report, "snippets", 51);
        AssertFloor(report, "executor", 33);
        AssertFloor(report, "bundle", 18);
    }

    private void AssertFloor(ConformanceReport report, string family, int floor)
    {
        var actual = report.PassedInFamily(family);
        Assert.True(
            actual >= floor,
            $"family '{family}' passed {actual} vectors, below its floor of {floor}. "
            + "Either a vector regressed, or vectors stopped being copied to the test output.\n"
            + report.Summary());
    }

    // The gate guarding the gate. Both halves of rule 1 are behaviour, so both are
    // tested: a family the adapter cannot answer must FAIL, and only a vector that says
    // so may be skipped. Without this, someone tightening the runner later has nothing
    // telling them which of the two they just broke — and the failure mode is silent by
    // construction, because a lost check reports success.
    [Fact]
    public void An_unhandled_family_fails_and_only_a_declared_skip_is_skipped()
    {
        var dir = Directory.CreateTempSubdirectory("workflow-v1-conformance-gate");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "unhandled.json"), """
                { "id": "gate.probe.unhandled", "family": "no_such_family",
                  "input": {}, "expected": { "anything": true }, "equivalence": "subset" }
                """);
            File.WriteAllText(Path.Combine(dir.FullName, "declared.json"), """
                { "id": "gate.probe.declared_skip", "family": "no_such_family",
                  "not_implemented": true,
                  "input": {}, "expected": { "anything": true }, "equivalence": "subset" }
                """);

            var schemaPath = Path.Combine(AppContext.BaseDirectory, "conformance", "schema", "workflow.v1.schema.json");
            var report = new ConformanceRunner(new NashiraAdapter(File.ReadAllText(schemaPath)))
                .Run(Directory.GetFiles(dir.FullName, "*.json"));

            Assert.Equal(1, report.Failed);
            Assert.Equal(1, report.Skipped);
            Assert.Contains("does not handle family", report.Summary(), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static bool IsPending(string file, string root) =>
        Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "_pending", StringComparison.Ordinal));
}
