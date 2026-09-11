using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Handlers;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

// A refused import has three possible causes with three different fixes, and only
// one of them is the snippet author's:
//
//   nobody approved it        → an admin adds it to the allowlist
//   approved, still installing → wait, or read the install error
//   approved, needs network    → an admin enables network on the SNIPPET
//
// The last one used to read as the first. `paramiko` and `socket`, both approved
// and both `ready`, were reported as "not on the allowlist" — sending an admin to
// look at a list they were already on, while the switch that mattered was on the
// snippet. The query filtered the network-gated rows out before the message was
// built, so by then the two cases were indistinguishable.
public class PythonModuleDenialMessageTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"pymodules-{Guid.NewGuid()}").Options);

    private sealed class UnusedSandbox : IPythonSandbox
    {
        public Task<PythonRunResult> RunAsync(PythonRunRequest request, CancellationToken ct)
            => throw new InvalidOperationException("the sandbox must not be reached when an import is refused");
    }

    // Every case here is refused before the input is ever looked at, so the resolver is
    // only here to let the handler be constructed.
    private sealed class UnusedSecrets : ISecretResolver
    {
        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct)
            => throw new InvalidOperationException("an import refusal must not resolve secrets");

        public Task<string> SubstituteAsync(string template, CancellationToken ct)
            => throw new InvalidOperationException("an import refusal must not resolve secrets");

        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct)
            => throw new InvalidOperationException("an import refusal must not resolve secrets");
    }

    private static PythonSnippetHandler Handler(AppDbContext db) =>
        new(db, new UnusedSandbox(), new UnusedSecrets(),
            new ConfigurationBuilder().Build(),
            NullLogger<PythonSnippetHandler>.Instance);

    private static void Allow(
        AppDbContext db, string module, bool requiresNetwork = false,
        string status = AllowedPythonModule.StatusReady)
    {
        db.AllowedPythonModules.Add(new AllowedPythonModule
        {
            AllowedPythonModuleId = Guid.NewGuid(),
            Module = module,
            Source = AllowedPythonModule.SourceStdlib,
            RequiresNetwork = requiresNetwork,
            Status = status,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private static Task<SnippetResult> RunAsync(AppDbContext db, string code, bool networkEnabled) =>
        Handler(db).ExecuteAsync(new SnippetRequest
        {
            NodeId = "py",
            WorkflowId = Guid.NewGuid(),
            SnippetId = Guid.NewGuid(),
            SnippetType = Snippet.TypePythonSnippet,
            Code = code,
            Input = JsonDocument.Parse("{}").RootElement.Clone(),
            TimeoutSeconds = 30,
            NetworkEnabled = networkEnabled,
        }, CancellationToken.None);

    [Fact]
    public async Task An_approved_network_module_without_the_snippet_flag_is_not_called_unapproved()
    {
        using var db = NewDb();
        Allow(db, "paramiko", requiresNetwork: true);
        Allow(db, "socket", requiresNetwork: true);

        var r = await RunAsync(db, "import paramiko\nimport socket\nresult = 1", networkEnabled: false);

        Assert.False(r.Success);
        Assert.Equal("module_denied", r.ErrorCode);
        Assert.Contains("approved", r.Error);
        Assert.Contains("network", r.Error);
        Assert.Contains("network_enabled", r.Error);
        Assert.DoesNotContain("not on the allowlist", r.Error);
    }

    [Fact]
    public async Task The_same_snippet_with_the_flag_on_reaches_the_sandbox()
    {
        // The gate is the only thing standing between it and running: the fake
        // sandbox throws, which is how we know the import check passed.
        using var db = NewDb();
        Allow(db, "paramiko", requiresNetwork: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(db, "import paramiko\nresult = 1", networkEnabled: true));
    }

    [Fact]
    public async Task A_module_nobody_approved_is_still_reported_as_off_the_allowlist()
    {
        using var db = NewDb();
        Allow(db, "json");

        var r = await RunAsync(db, "import requests\nresult = 1", networkEnabled: true);

        Assert.False(r.Success);
        Assert.Contains("not on the allowlist", r.Error);
        Assert.Contains("requests", r.Error);
    }

    [Fact]
    public async Task An_approved_module_that_is_still_installing_says_so()
    {
        using var db = NewDb();
        Allow(db, "numpy", status: AllowedPythonModule.StatusPending);

        var r = await RunAsync(db, "import numpy\nresult = 1", networkEnabled: false);

        Assert.False(r.Success);
        Assert.Contains("still installing", r.Error);
        Assert.DoesNotContain("not on the allowlist", r.Error);
    }

    [Fact]
    public async Task A_failed_install_points_at_the_install_not_at_the_script()
    {
        using var db = NewDb();
        Allow(db, "numpy", status: AllowedPythonModule.StatusFailed);

        var r = await RunAsync(db, "import numpy\nresult = 1", networkEnabled: false);

        Assert.False(r.Success);
        Assert.Contains("install failed", r.Error);
    }

    [Fact]
    public async Task Each_cause_is_reported_separately_when_a_snippet_hits_several()
    {
        // The message has to carry all three, because fixing one and re-running to
        // discover the next is how a five-minute problem becomes an afternoon.
        using var db = NewDb();
        Allow(db, "paramiko", requiresNetwork: true);
        Allow(db, "numpy", status: AllowedPythonModule.StatusPending);

        var r = await RunAsync(
            db, "import paramiko\nimport numpy\nimport requests\nresult = 1", networkEnabled: false);

        Assert.False(r.Success);
        Assert.Contains("paramiko", r.Error);
        Assert.Contains("numpy", r.Error);
        Assert.Contains("requests", r.Error);
        Assert.Contains("network_enabled", r.Error);
        Assert.Contains("still installing", r.Error);
        Assert.Contains("not on the allowlist", r.Error);
    }
}
