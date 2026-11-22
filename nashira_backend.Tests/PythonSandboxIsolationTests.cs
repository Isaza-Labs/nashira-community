using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

// python_snippet runs author-written code as a child of the backend process. Without an OS
// sandbox in front of the interpreter that child has the backend's filesystem — including
// the Data Protection keyring — so the default has to be to refuse, and running without one
// has to be a deliberate, named setting.
//
// The tests that need an interpreter no-op when none is on PATH, like PythonSandboxTests;
// the refusal tests do not need one, because refusing happens before anything is spawned.
public class PythonSandboxIsolationTests
{
    private static string? Exe => PythonSandboxTests.Exe;

    private static PythonSandbox Sandbox(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["Python:Executable"] = Exe ?? "python3" };
        foreach (var (key, value) in settings) values[key] = value;
        return new PythonSandbox(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            NullLogger<PythonSandbox>.Instance);
    }

    private static Task<PythonRunResult> RunAsync(PythonSandbox sandbox, string code)
    {
        using var input = JsonDocument.Parse("{}");
        return sandbox.RunAsync(new PythonRunRequest(
            Code: code,
            Input: input.RootElement.Clone(),
            TimeoutSeconds: 30,
            NetworkEnabled: false,
            AllowedModules: ["json"]), CancellationToken.None);
    }

    [Fact]
    public async Task Snippets_are_refused_when_no_sandbox_command_is_configured()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"nashira-sandbox-{Guid.NewGuid():N}");
        try
        {
            // If this ever runs it leaves a file behind, which is how the test can tell
            // "refused" from "ran and failed for some other reason".
            var r = await RunAsync(Sandbox(),
                $"open({JsonSerializer.Serialize(marker)}, 'w').write('ran')\nresult = 1");

            Assert.False(r.Ok);
            Assert.Equal("sandbox_not_configured", r.ErrorKind);
            Assert.False(File.Exists(marker), "the snippet ran although no sandbox was configured");
            // The message is what the person running the workflow sees, so it names the
            // setting that fixes it.
            Assert.Contains("Python:SandboxCommand", r.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    [Fact]
    public async Task A_blank_sandbox_command_is_the_same_as_none()
    {
        var r = await RunAsync(Sandbox(("Python:SandboxCommand", "  ; ")), "result = 1");

        Assert.False(r.Ok);
        Assert.Equal("sandbox_not_configured", r.ErrorKind);
    }

    [Fact]
    public async Task Running_without_a_sandbox_needs_the_explicit_opt_in()
    {
        if (Exe is null) return;

        var r = await RunAsync(Sandbox(("Python:AllowUnsandboxed", "true")), "result = {'ok': True}");

        Assert.True(r.Ok, r.Stderr);
        Assert.True(r.Output.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_configured_sandbox_command_runs_without_the_opt_in()
    {
        if (Exe is null || OperatingSystem.IsWindows()) return;

        // `env` stands in for bwrap or a container shim: it runs the rest of the command
        // line, which is all the wrapper contract asks of it.
        var r = await RunAsync(Sandbox(("Python:SandboxCommand", "env")), "result = {'ok': True}");

        Assert.True(r.Ok, r.Stderr);
        Assert.True(r.Output.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Snippet_code_has_no_open_builtin()
    {
        if (Exe is null) return;

        var host = Path.Combine(Path.GetTempPath(), $"nashira-host-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(host, "host-file-contents");
        try
        {
            var r = await RunAsync(Sandbox(("Python:AllowUnsandboxed", "true")),
                $$"""
                try:
                    result = {"read": open({{JsonSerializer.Serialize(host)}}).read()}
                except NameError as e:
                    result = {"blocked": str(e)}
                """);

            Assert.True(r.Ok, r.Stderr);
            Assert.False(r.Output.TryGetProperty("read", out var read),
                $"the snippet read a host file: {read}");
            Assert.Contains("open", r.Output.GetProperty("blocked").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(host);
        }
    }

    [Fact]
    public async Task Everyday_builtins_still_work_without_open()
    {
        if (Exe is null) return;

        var r = await RunAsync(Sandbox(("Python:AllowUnsandboxed", "true")),
            """
            import json
            rows = sorted({"b": 2, "a": 1}.items())
            try:
                int("x")
            except ValueError as e:
                err = type(e).__name__
            print("printed")
            result = {
                "rows": rows,
                "total": sum(v for _, v in rows),
                "fmt": f"{len(rows):03d}",
                "err": err,
                "json": json.dumps({"k": [1, 2]}),
                "isinst": isinstance(rows, list),
                "span": list(range(3)),
            }
            """);

        Assert.True(r.Ok, r.Stderr);
        Assert.Equal(3, r.Output.GetProperty("total").GetInt32());
        Assert.Equal("002", r.Output.GetProperty("fmt").GetString());
        Assert.Equal("ValueError", r.Output.GetProperty("err").GetString());
        Assert.Equal("printed", r.Stdout.Trim());
    }

    [Fact]
    public async Task The_default_allowlist_offers_no_process_or_filesystem_module()
    {
        // The default list is the only thing standing between a fresh install and a snippet
        // that can spawn processes. An administrator can still add a module deliberately;
        // what must not happen is one arriving by default.
        var database = $"allowlist-{Guid.NewGuid()}";   // one name: each scope builds its own context
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
        using var provider = services.BuildServiceProvider();

        await PythonModuleSeeder.SeedAsync(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        using var scope = provider.CreateScope();
        var seeded = scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .AllowedPythonModules.Select(m => m.Module.ToLowerInvariant()).ToList();

        Assert.NotEmpty(seeded);
        string[] reachesTheHost =
        [
            "subprocess", "os", "sys", "shutil", "pty", "multiprocessing", "ctypes", "importlib",
            "pathlib", "io", "tempfile", "glob", "fileinput", "shelve", "pickle", "marshal",
            "runpy", "code", "posix", "nt",
        ];
        Assert.Empty(seeded.Intersect(reachesTheHost));
    }
}
