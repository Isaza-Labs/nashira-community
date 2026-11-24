using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

// Runs the real sandbox against a real interpreter. These are the executor-side
// guarantees a workflow author leans on: the input arrives typed, `result` comes
// back typed, a snippet exception is a script_error and not a harness crash, and
// the import guard holds inside the interpreter.
//
// Test policy when no Python is on PATH: the optional tests here are marked
// [RequiresPythonFact] and are reported as skipped, with the reason, instead of
// passing without running. The secret-privacy acceptance tests (SecretAuthorizationTests,
// SecretOutputPrivacyTests) assert that an interpreter is available and fail without
// one, so a build agent must provide Python.
public class PythonSandboxTests
{
    internal static readonly string? Exe = FindPython();

    private static string? FindPython()
    {
        foreach (var exe in new[] { "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (p is null) continue;
                p.WaitForExit(5000);
                if (p.HasExited && p.ExitCode == 0) return exe;
            }
            catch (Exception) { /* not this one */ }
        }
        return null;
    }

    private static PythonSandbox Sandbox()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Python:Executable"] = Exe,
                // These tests exercise the harness (typing, the import guard, deadlines), not
                // OS isolation, so they opt in to running without a sandbox command. What
                // happens without the opt-in is pinned in PythonSandboxIsolationTests.
                ["Python:AllowUnsandboxed"] = "true",
            })
            .Build();
        return new PythonSandbox(config, NullLogger<PythonSandbox>.Instance, new HardeningTestUser());
    }

    private static Task<PythonRunResult> RunAsync(
        string code, string inputJson = "{}", int timeoutSeconds = 30,
        IReadOnlyCollection<string>? allowed = null)
    {
        using var input = JsonDocument.Parse(inputJson);
        return Sandbox().RunAsync(new PythonRunRequest(
            Code: code,
            Input: input.RootElement.Clone(),
            TimeoutSeconds: timeoutSeconds,
            NetworkEnabled: false,
            AllowedModules: allowed ?? ["json", "datetime", "collections"]), CancellationToken.None);
    }

    [RequiresPythonFact]
    public async Task Minimal_snippet_returns_its_result_typed()
    {

        var r = await RunAsync("""result = {"ok": True, "html": "<html><body>ok</body></html>"}""");

        Assert.True(r.Ok, r.Stderr);
        Assert.True(r.Output.GetProperty("ok").GetBoolean());
        Assert.Equal("<html><body>ok</body></html>", r.Output.GetProperty("html").GetString());
    }

    [RequiresPythonFact]
    public async Task Input_arrives_typed_and_is_addressable()
    {

        var r = await RunAsync(
            """result = {"total": sum(input["items"]), "name": input["meta"]["name"]}""",
            inputJson: """{"items": [1, 2, 3], "meta": {"name": "dashboard"}}""");

        Assert.True(r.Ok, r.Stderr);
        Assert.Equal(6, r.Output.GetProperty("total").GetInt32());
        Assert.Equal("dashboard", r.Output.GetProperty("name").GetString());
    }

    [RequiresPythonFact]
    public async Task Prints_stay_in_stdout_and_do_not_corrupt_the_envelope()
    {

        var r = await RunAsync("""
            print("log line one")
            print("log line two")
            result = {"ok": True}
            """);

        Assert.True(r.Ok, r.Stderr);
        Assert.True(r.Output.GetProperty("ok").GetBoolean());
        Assert.Contains("log line one", r.Stdout);
        Assert.Contains("log line two", r.Stdout);
        Assert.DoesNotContain("__NASHIRA_RESULT__", r.Stdout);
    }

    [RequiresPythonFact]
    public async Task A_snippet_exception_is_a_script_error_naming_the_exception()
    {

        var r = await RunAsync("""result = {"x": 1 / 0}""");

        Assert.False(r.Ok);
        Assert.Equal("script_error", r.ErrorKind);
        Assert.Contains("ZeroDivisionError", r.Stderr);
    }

    // The failure mode a workflow author hits when a template path is wrong: the
    // reference stays literal, the snippet indexes a string as if it were the
    // object, and the error must point at the snippet — not at the harness.
    [RequiresPythonFact]
    public async Task A_literal_unresolved_template_fails_inside_the_snippet_legibly()
    {

        var r = await RunAsync(
            """result = {"n": len(input["data"]["devices"])}""",
            inputJson: """{"data": "{{ steps.fetch.output }}"}""");

        Assert.False(r.Ok);
        Assert.Equal("script_error", r.ErrorKind);
        Assert.Contains("TypeError", r.Stderr);
    }

    // Regression, reported from a live instance: every pip package failed with
    //   ImportError: module 'builtins' is not on this deployment's allowlist
    // naming a module the author never wrote and could not meaningfully approve.
    //
    // The guard decided WHO was importing from the `globals` argument, treating
    // `globals is None` as snippet code. Library code routinely passes nothing —
    // `from dateutil import parser` reaches __import__ with globals=None from
    // inside the package — so an approved package was judged as if the snippet had
    // written its internal imports. It read as "the allowlist does not work for
    // network packages", because those are the ones that arrive via pip and have
    // lazy or indirect imports.
    //
    // The caller is now read from the calling frame, which is what the argument
    // was standing in for. `os` uses the same shape as dateutil for a module that
    // is always present, so the test needs nothing installed.
    [RequiresPythonFact]
    public async Task An_allowed_package_may_import_indirectly_without_passing_globals()
    {

        var r = await RunAsync("""
            import os.path
            result = {"joined": os.path.join("a", "b")}
            """, allowed: ["os"]);

        Assert.True(r.Ok, r.Stderr);
    }

    // The clause removed above was load-bearing: it was what stopped a snippet
    // calling __import__ directly, which passes no globals either. The frame check
    // has to keep that closed, or the fix trades one bug for a hole.
    [RequiresPythonFact]
    public async Task A_snippet_cannot_escape_the_guard_by_calling_import_directly()
    {

        var r = await RunAsync("""
            os = __import__("os")
            result = {"cwd": os.getcwd()}
            """);

        Assert.False(r.Ok);
        Assert.Equal("script_error", r.ErrorKind);
        Assert.Contains("allowlist", r.Stderr);
    }

    // The other way round the same corner: importlib is import machinery, and a
    // snippet reaching for it is asking to bypass the check rather than to use a
    // module.
    [RequiresPythonFact]
    public async Task A_snippet_cannot_escape_the_guard_through_importlib()
    {

        var r = await RunAsync("""
            import importlib
            result = {"m": str(importlib)}
            """);

        Assert.False(r.Ok);
        Assert.Contains("allowlist", r.Stderr);
    }

    [RequiresPythonFact]
    public async Task The_import_guard_blocks_a_module_off_the_allowlist()
    {

        var r = await RunAsync("""
            import os
            result = {"cwd": os.getcwd()}
            """);

        Assert.False(r.Ok);
        Assert.Equal("script_error", r.ErrorKind);
        Assert.Contains("allowlist", r.Stderr);
    }

    // Regression: the guard used to intercept the imports an allowed module makes
    // internally — `collections` reaching for `heapq`, `datetime` for `time` — so
    // allowlisting a module was not enough to import it, and whether it broke
    // depended on what the harness happened to have cached in sys.modules on that
    // interpreter version. The allowlist must govern what the SNIPPET imports;
    // an approved module's dependencies are its own business.
    [RequiresPythonFact]
    public async Task An_allowed_module_may_import_its_own_dependencies()
    {

        var r = await RunAsync("""
            import datetime
            import collections
            result = {
                "year": datetime.date(2026, 8, 20).year,
                "top": collections.Counter("aab").most_common(1)[0][0],
            }
            """, allowed: ["datetime", "collections", "json"]);

        Assert.True(r.Ok, r.Stderr);
        Assert.Equal(2026, r.Output.GetProperty("year").GetInt32());
        Assert.Equal("a", r.Output.GetProperty("top").GetString());
    }

    [RequiresPythonFact]
    public async Task An_allowed_module_imports_normally()
    {

        var r = await RunAsync("""
            import json
            from collections import Counter
            result = {"top": Counter("aab").most_common(1)[0][0]}
            """);

        Assert.True(r.Ok, r.Stderr);
        Assert.Equal("a", r.Output.GetProperty("top").GetString());
    }

    [RequiresPythonFact]
    public async Task A_runaway_loop_is_killed_at_the_deadline()
    {

        var r = await RunAsync("""
            while True:
                pass
            """, timeoutSeconds: 1);

        Assert.False(r.Ok);
        Assert.Equal("timeout", r.ErrorKind);
    }

    // A result comfortably under the stdout cap round-trips whole. Dashboards this
    // size are fine.
    [RequiresPythonFact]
    public async Task A_100kb_result_round_trips_intact()
    {

        var r = await RunAsync("""result = {"html": "<div>" + "x" * 100_000 + "</div>"}""");

        Assert.True(r.Ok, r.Stderr);
        Assert.Equal(100_000 + "<div></div>".Length, r.Output.GetProperty("html").GetString()!.Length);
    }

    // Pins the sharp edge, deliberately: the result envelope travels over stdout
    // and counts against the 200k output cap, so a result bigger than the cap can
    // NEVER succeed — the child is killed mid-envelope and the step fails with an
    // empty-ish stderr. If this test ever fails because the big result came back
    // intact, the cap was raised or rerouted: delete the test with the fix.
    [RequiresPythonFact]
    public async Task A_result_larger_than_the_output_cap_cannot_survive()
    {

        var r = await RunAsync("""result = {"html": "x" * 250_000}""");

        Assert.False(r.Ok);
        Assert.True(r.ErrorKind is "script_error" or "bad_output", $"kind={r.ErrorKind}");
    }
}
