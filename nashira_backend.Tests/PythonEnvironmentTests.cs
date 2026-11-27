using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Device;
using nashira_backend.Data.DTos.Python;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Ssh;
using nashira_backend.Services.Worker;
using nashira_backend.Services.Worker.Python;
using nashira_backend.Services.Workflow;

namespace nashira_backend.Tests;

// Python environments built with uv: the row rules (name, single SSH fallback, status
// reset, credential masking), the resolver's refusals, the SSH fallback decision, and
// the environment reference on devices and snippets.
//
// Why the feature exists: paramiko 5 removed `ssh-rsa`, and a switch offering only that
// host key (Extreme EXOS on OpenSSH 8.1) fails with "Incompatible ssh peer (no
// acceptable host key)". An environment pins paramiko < 5 for the devices that need it.
public class PythonEnvironmentTests
{
    private static AppDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name ?? $"python-env-{Guid.NewGuid()}")
            .Options);

    private static PythonEnvironmentService Service(AppDbContext db) => new(db, new HardeningTestUser("admin"));

    private static PythonEnvironmentDraft Draft(
        string name = "ssh-legacy", string requirements = "paramiko>=3.5,<5", bool? fallback = null,
        string? version = null) =>
        new(Name: name, Requirements: requirements, IsSshFallback: fallback, PythonVersion: version);

    // ── name ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ssh-legacy")]
    [InlineData("abc")]
    [InlineData("py312")]
    [InlineData("a-1")]
    [InlineData("0ab")]
    public void A_slug_name_is_accepted(string name)
    {
        Assert.Equal(name, PythonEnvironmentRules.RequireName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]                       // too short: 3 characters minimum
    [InlineData("SSH-legacy")]               // uppercase
    [InlineData("-ssh")]                     // leading dash
    [InlineData("ssh-")]                     // trailing dash
    [InlineData("ssh_legacy")]               // underscore
    [InlineData("ssh.legacy")]               // a dot would collide with build directories
    [InlineData("../etc")]                   // a path
    [InlineData("ssh legacy")]
    [InlineData("a23456789012345678901234567890123456789012345678901")] // 51 characters
    public void Anything_else_is_refused(string name)
    {
        var ex = Assert.Throws<ValidationException>(() => PythonEnvironmentRules.RequireName(name));
        Assert.Equal("python_env_name_invalid", ex.Code);
    }

    [Fact]
    public void Fifty_characters_is_the_longest_name()
    {
        var name = "a" + new string('b', 48) + "c";
        Assert.Equal(50, name.Length);
        Assert.Equal(name, PythonEnvironmentRules.RequireName(name));
    }

    [Fact]
    public async Task Two_active_environments_cannot_share_a_name_but_a_deleted_one_frees_it()
    {
        using var db = NewDb();
        var service = Service(db);
        var first = await service.CreateAsync(Draft("tools"), default);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.CreateAsync(Draft("tools"), default));
        Assert.Equal("python_env_name_taken", ex.Code);

        await service.DeleteAsync(first.PythonEnvironmentId, default);
        var again = await service.CreateAsync(Draft("tools"), default);
        Assert.NotEqual(first.PythonEnvironmentId, again.PythonEnvironmentId);
    }

    [Fact]
    public async Task The_name_cannot_change_once_created()
    {
        using var db = NewDb();
        var service = Service(db);
        var row = await service.CreateAsync(Draft("tools"), default);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            service.UpdateAsync(row.PythonEnvironmentId, new PythonEnvironmentDraft(Name: "other"), default));
        Assert.Equal("python_env_name_immutable", ex.Code);
    }

    [Theory]
    [InlineData("-r /etc/passwd")]
    [InlineData("--requirement=/tmp/x.txt")]
    [InlineData("-c constraints.txt")]
    [InlineData("-e ./local")]
    [InlineData("-r/etc/shadow")]
    public void Requirements_cannot_pull_in_another_file_on_the_server(string line)
    {
        var ex = Assert.Throws<ValidationException>(() =>
            PythonEnvironmentRules.NormalizeRequirements($"netmiko>=4,<5\n{line}"));
        Assert.Equal("python_env_requirements_invalid", ex.Code);
    }

    [Fact]
    public void Index_urls_are_allowed_in_requirements()
    {
        var text = "--index-url https://pypi.example/simple\n--extra-index-url https://u:p@other/simple\nparamiko<5";
        Assert.Equal(text, PythonEnvironmentRules.NormalizeRequirements(text));
    }

    [Theory]
    [InlineData("3.12", "3.12")]
    [InlineData(" 3.11.9 ", "3.11.9")]
    [InlineData("cpython@3.12", "cpython@3.12")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_python_version_is_a_version_or_nothing(string? raw, string? expected)
    {
        Assert.Equal(expected, PythonEnvironmentRules.NormalizePythonVersion(raw));
    }

    [Theory]
    [InlineData("/usr/bin/python3")]
    [InlineData("3.12; rm -rf /")]
    [InlineData("--python")]
    public void A_python_version_is_never_a_path_or_a_flag(string raw)
    {
        Assert.Throws<ValidationException>(() => PythonEnvironmentRules.NormalizePythonVersion(raw));
    }

    // ── single SSH fallback ─────────────────────────────────────────────────

    [Fact]
    public async Task Only_one_active_environment_can_be_the_ssh_fallback()
    {
        using var db = NewDb();
        var service = Service(db);
        await service.CreateAsync(Draft("ssh-legacy", fallback: true), default);

        var created = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateAsync(Draft("ssh-other", fallback: true), default));
        Assert.Equal("python_env_fallback_taken", created.Code);

        var other = await service.CreateAsync(Draft("ssh-other"), default);
        var updated = await Assert.ThrowsAsync<ConflictException>(() =>
            service.UpdateAsync(other.PythonEnvironmentId, new PythonEnvironmentDraft(IsSshFallback: true), default));
        Assert.Equal("python_env_fallback_taken", updated.Code);

        Assert.Equal(1, await db.PythonEnvironments.CountAsync(e => e.IsActive && e.IsSshFallback));
    }

    [Fact]
    public async Task Re_saving_the_current_fallback_is_not_a_conflict_and_deleting_it_frees_the_flag()
    {
        using var db = NewDb();
        var service = Service(db);
        var legacy = await service.CreateAsync(Draft("ssh-legacy", fallback: true), default);

        await service.UpdateAsync(legacy.PythonEnvironmentId,
            new PythonEnvironmentDraft(IsSshFallback: true, Description: "still the one"), default);

        await service.DeleteAsync(legacy.PythonEnvironmentId, default);
        var next = await service.CreateAsync(Draft("ssh-next", fallback: true), default);
        Assert.True(next.IsSshFallback);
        Assert.False((await db.PythonEnvironments.SingleAsync(e => e.Name == "ssh-legacy")).IsSshFallback);
    }

    // ── credential masking ──────────────────────────────────────────────────

    [Fact]
    public void Credentials_in_index_urls_are_masked()
    {
        var text = "--extra-index-url https://deploy:s3cr3t@pypi.example.com/simple\n"
                   + "--index-url https://token@pypi.org/simple\n"
                   + "paramiko>=3.5,<5  # maintainer: ops@example.com";

        var masked = PythonEnvironmentRules.MaskCredentials(text)!;

        Assert.DoesNotContain("s3cr3t", masked);
        Assert.DoesNotContain("deploy:", masked);
        Assert.DoesNotContain("token@", masked);
        Assert.Contains("https://***@pypi.example.com/simple", masked);
        Assert.Contains("https://***@pypi.org/simple", masked);
        // An address outside a URL is not a credential.
        Assert.Contains("ops@example.com", masked);
    }

    [Fact]
    public void Responses_mask_requirements_errors_and_freeze_output()
    {
        var row = new PythonEnvironment
        {
            Name = "private",
            Requirements = "--index-url https://u:pw@idx.example/simple\nfoo",
            LastError = "uv pip install failed: could not reach https://u:pw@idx.example/simple/foo/",
            ResolvedPackages = "foo @ https://u:pw@idx.example/foo-1.0.whl",
        };

        var response = PythonEnvironmentResponse.From(row);

        Assert.DoesNotContain("pw@", response.Requirements);
        Assert.DoesNotContain("pw@", response.LastError);
        Assert.DoesNotContain("pw@", response.ResolvedPackages);
        // The stored row keeps them: the build needs them.
        Assert.Contains("u:pw@", row.Requirements);
    }

    [Fact]
    public async Task Saving_the_masked_requirements_back_keeps_the_stored_credentials_and_the_build()
    {
        using var db = NewDb();
        var service = Service(db);
        var row = await service.CreateAsync(Draft(requirements: "--index-url https://u:pw@idx.example/simple\nfoo"), default);
        row.Status = PythonEnvironment.StatusReady;
        await db.SaveChangesAsync();

        var masked = PythonEnvironmentResponse.From(row).Requirements;
        var updated = await service.UpdateAsync(row.PythonEnvironmentId,
            new PythonEnvironmentDraft(Requirements: masked), default);

        Assert.Contains("https://u:pw@idx.example/simple", updated.Requirements);
        // Unchanged in substance, so not a new environment: no rebuild.
        Assert.Equal(PythonEnvironment.StatusReady, updated.Status);
    }

    [Fact]
    public void A_retyped_credential_replaces_the_stored_one()
    {
        var restored = PythonEnvironmentRules.RestoreMaskedCredentials(
            "--index-url https://new:cred@idx.example/simple\n--extra-index-url https://***@other.example/simple",
            "--index-url https://u:pw@idx.example/simple\n--extra-index-url https://a:b@other.example/simple");

        Assert.Contains("https://new:cred@idx.example/simple", restored);
        Assert.Contains("https://a:b@other.example/simple", restored);
    }

    // ── status reset ────────────────────────────────────────────────────────

    private static async Task<PythonEnvironment> ReadyRowAsync(AppDbContext db, PythonEnvironmentService service)
    {
        var row = await service.CreateAsync(Draft(), default);
        row.Status = PythonEnvironment.StatusFailed;
        row.LastError = "boom";
        row.ClaimedBy = "host:1";
        row.ClaimedAt = DateTime.UtcNow;
        row.Status = PythonEnvironment.StatusReady;
        await db.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task Editing_requirements_puts_the_environment_back_to_pending()
    {
        using var db = NewDb();
        var service = Service(db);
        var row = await ReadyRowAsync(db, service);

        var updated = await service.UpdateAsync(row.PythonEnvironmentId,
            new PythonEnvironmentDraft(Requirements: "paramiko==4.0.0"), default);

        Assert.Equal(PythonEnvironment.StatusPending, updated.Status);
        Assert.Null(updated.LastError);
        Assert.Null(updated.ClaimedBy);
        Assert.Null(updated.ClaimedAt);
    }

    [Fact]
    public async Task Editing_the_python_version_puts_the_environment_back_to_pending()
    {
        using var db = NewDb();
        var service = Service(db);
        var row = await ReadyRowAsync(db, service);

        var updated = await service.UpdateAsync(row.PythonEnvironmentId,
            new PythonEnvironmentDraft(PythonVersion: "3.12"), default);
        Assert.Equal(PythonEnvironment.StatusPending, updated.Status);
        Assert.Equal("3.12", updated.PythonVersion);

        updated.Status = PythonEnvironment.StatusReady;
        await db.SaveChangesAsync();
        // "" is "back to the image's python3" — also a different environment.
        updated = await service.UpdateAsync(row.PythonEnvironmentId, new PythonEnvironmentDraft(PythonVersion: ""), default);
        Assert.Equal(PythonEnvironment.StatusPending, updated.Status);
        Assert.Null(updated.PythonVersion);
    }

    [Fact]
    public async Task Editing_only_the_description_or_the_fallback_flag_does_not_rebuild()
    {
        using var db = NewDb();
        var service = Service(db);
        var row = await ReadyRowAsync(db, service);

        var updated = await service.UpdateAsync(row.PythonEnvironmentId,
            new PythonEnvironmentDraft(Description: "why", IsSshFallback: true,
                Requirements: row.Requirements + "\r\n"), default);

        Assert.Equal(PythonEnvironment.StatusReady, updated.Status);
        Assert.Equal("why", updated.Description);
        Assert.True(updated.IsSshFallback);
    }

    [Fact]
    public async Task Rebuild_puts_the_environment_back_to_pending_and_releases_the_claim()
    {
        using var db = NewDb();
        var service = Service(db);
        var row = await service.CreateAsync(Draft(), default);
        row.Status = PythonEnvironment.StatusFailed;
        row.LastError = "uv pip install failed";
        row.ClaimedBy = "host:1";
        row.ClaimedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var rebuilt = await service.RebuildAsync(row.PythonEnvironmentId, default);

        Assert.Equal(PythonEnvironment.StatusPending, rebuilt.Status);
        Assert.Null(rebuilt.LastError);
        Assert.Null(rebuilt.ClaimedBy);
        Assert.Null(rebuilt.ClaimedAt);
    }

    [Fact]
    public async Task An_environment_still_in_use_cannot_be_deleted()
    {
        using var db = NewDb();
        var service = Service(db);
        var row = await service.CreateAsync(Draft(), default);
        db.Devices.Add(new Device
        {
            DeviceId = Guid.NewGuid(), DeviceName = "exos-1", IpAddress = "10.0.0.1",
            PythonEnvironmentId = row.PythonEnvironmentId,
        });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ConflictException>(() => service.DeleteAsync(row.PythonEnvironmentId, default));
        Assert.Equal("python_env_in_use", ex.Code);
        Assert.Contains("exos-1", ex.Message);
    }

    // ── seed ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_empty_table_gets_ssh_legacy_as_the_fallback_once()
    {
        using var db = NewDb();

        Assert.True(await PythonEnvironmentSeeder.SeedAsync(db));
        Assert.False(await PythonEnvironmentSeeder.SeedAsync(db));

        var row = await db.PythonEnvironments.SingleAsync();
        Assert.Equal("ssh-legacy", row.Name);
        Assert.True(row.IsSshFallback);
        Assert.Equal(PythonEnvironment.StatusPending, row.Status);
        Assert.Contains("paramiko>=3.5,<5", row.Requirements);
        Assert.Contains("netmiko>=4,<5", row.Requirements);
        Assert.Contains("ssh-rsa", row.Description);
        // The seeded definition passes the same rules an admin's would.
        Assert.Equal(row.Requirements, PythonEnvironmentRules.NormalizeRequirements(row.Requirements));
    }

    [Fact]
    public async Task A_deleted_seed_is_not_brought_back()
    {
        using var db = NewDb();
        await PythonEnvironmentSeeder.SeedAsync(db);
        var row = await db.PythonEnvironments.SingleAsync();
        row.IsActive = false;
        await db.SaveChangesAsync();

        Assert.False(await PythonEnvironmentSeeder.SeedAsync(db));
        Assert.Equal(0, await db.PythonEnvironments.CountAsync(e => e.IsActive));
    }

    // ── resolver ────────────────────────────────────────────────────────────

    private sealed class ResolverWorld : IDisposable
    {
        private readonly ServiceProvider _sp;
        public string Root { get; } = Directory.CreateTempSubdirectory("nashira-pyenv-").FullName;
        public PythonEnvironmentResolver Resolver { get; }

        public ResolverWorld()
        {
            var name = $"python-env-resolver-{Guid.NewGuid()}";
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Python:PackagesDir"] = Root })
                .Build();
            var services = new ServiceCollection();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(name));
            _sp = services.BuildServiceProvider();
            Resolver = new PythonEnvironmentResolver(
                _sp.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<PythonEnvironmentResolver>.Instance);
        }

        public async Task<PythonEnvironment> AddAsync(
            string name, string status, bool active = true, bool fallback = false, bool built = false)
        {
            var row = new PythonEnvironment
            {
                PythonEnvironmentId = Guid.NewGuid(), Name = name, Requirements = "x",
                Status = status, IsActive = active, IsSshFallback = fallback,
            };
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PythonEnvironments.Add(row);
            await db.SaveChangesAsync();
            if (built)
            {
                var envDir = Path.Combine(Root, PythonPackagesLayout.EnvsDirName, name);
                var interpreter = PythonPackagesLayout.Interpreter(envDir);
                Directory.CreateDirectory(Path.GetDirectoryName(interpreter)!);
                await File.WriteAllTextAsync(interpreter, string.Empty);
            }
            return row;
        }

        public void Dispose()
        {
            _sp.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task A_ready_built_environment_resolves_to_its_interpreter()
    {
        using var w = new ResolverWorld();
        var row = await w.AddAsync("ssh-legacy", PythonEnvironment.StatusReady, built: true);

        var env = await w.Resolver.ResolveAsync(row.PythonEnvironmentId, default);

        Assert.Equal("ssh-legacy", env.Name);
        Assert.Equal(Path.Combine(w.Root, "envs", "ssh-legacy"), env.Directory);
        Assert.Equal(PythonPackagesLayout.Interpreter(env.Directory), env.Interpreter);
        Assert.Equal(PythonPackagesLayout.BinDir(env.Directory), env.BinDir);
    }

    [Theory]
    [InlineData(PythonEnvironment.StatusPending, "not ready")]
    [InlineData(PythonEnvironment.StatusBuilding, "not ready")]
    [InlineData(PythonEnvironment.StatusFailed, "failed to build")]
    public async Task An_environment_that_is_not_ready_is_refused_by_name(string status, string says)
    {
        using var w = new ResolverWorld();
        var row = await w.AddAsync("tools", status, built: true);

        var ex = await Assert.ThrowsAsync<PythonEnvironmentUnavailableException>(() =>
            w.Resolver.ResolveAsync(row.PythonEnvironmentId, default));
        Assert.Contains("'tools'", ex.Message);
        Assert.Contains(says, ex.Message);
    }

    [Fact]
    public async Task A_ready_environment_whose_build_is_missing_is_refused()
    {
        using var w = new ResolverWorld();
        var row = await w.AddAsync("tools", PythonEnvironment.StatusReady, built: false);

        var ex = await Assert.ThrowsAsync<PythonEnvironmentUnavailableException>(() =>
            w.Resolver.ResolveAsync(row.PythonEnvironmentId, default));
        Assert.Contains("missing from disk", ex.Message);
    }

    [Fact]
    public async Task A_deleted_or_unknown_environment_is_refused()
    {
        using var w = new ResolverWorld();
        var deleted = await w.AddAsync("gone", PythonEnvironment.StatusReady, active: false, built: true);

        var del = await Assert.ThrowsAsync<PythonEnvironmentUnavailableException>(() =>
            w.Resolver.ResolveAsync(deleted.PythonEnvironmentId, default));
        Assert.Contains("deleted", del.Message);

        var unknown = await Assert.ThrowsAsync<PythonEnvironmentUnavailableException>(() =>
            w.Resolver.ResolveAsync(Guid.NewGuid(), default));
        Assert.Contains("does not exist", unknown.Message);
    }

    [Fact]
    public async Task The_ssh_fallback_is_found_only_when_usable()
    {
        using var w = new ResolverWorld();
        Assert.Null(await w.Resolver.FindSshFallbackAsync(default));

        var pending = await w.AddAsync("ssh-legacy", PythonEnvironment.StatusPending, fallback: true, built: true);
        Assert.Null(await w.Resolver.FindSshFallbackAsync(default));

        using var w2 = new ResolverWorld();
        var ready = await w2.AddAsync("ssh-legacy", PythonEnvironment.StatusReady, fallback: true, built: true);
        var found = await w2.Resolver.FindSshFallbackAsync(default);
        Assert.Equal(ready.PythonEnvironmentId, found?.Id);
        Assert.NotEqual(pending.PythonEnvironmentId, found?.Id);
    }

    // ── SSH fallback decision ───────────────────────────────────────────────

    private static readonly ResolvedPythonEnvironment Legacy =
        new(Guid.NewGuid(), "ssh-legacy", "/app/pyenv/envs/ssh-legacy.abcd1234",
            "/app/pyenv/envs/ssh-legacy.abcd1234/bin/python", "/app/pyenv/envs/ssh-legacy.abcd1234/bin");

    private const string ExosError =
        "connect failed: Incompatible ssh peer (no acceptable host key)";

    [Fact]
    public void Paramikos_incompatible_host_key_error_retries_under_the_fallback()
    {
        var outcome = SshRunOutcome.Fail("connect_failed", ExosError, 5);

        Assert.True(SshEnvironmentFallback.IsIncompatibleHostKey(outcome));
        Assert.True(SshEnvironmentFallback.ShouldRetry(outcome, usedEnvironmentId: null, Legacy));
    }

    [Fact]
    public void Either_half_of_the_message_is_enough()
    {
        Assert.True(SshEnvironmentFallback.IsIncompatibleHostKey(
            SshRunOutcome.Fail("connect_failed", "IncompatiblePeer: incompatible SSH peer")));
        Assert.True(SshEnvironmentFallback.IsIncompatibleHostKey(
            SshRunOutcome.Fail("connect_failed", "negotiation: No acceptable host key")));
    }

    [Fact]
    public void The_error_is_also_found_on_a_command_result()
    {
        var outcome = SshRunOutcome.Success(new SshRunResult
        {
            Results = [new SshCommandResult { Command = "show version", Ok = false, Error = ExosError }],
        });
        Assert.True(SshEnvironmentFallback.ShouldRetry(outcome, null, Legacy));
    }

    [Fact]
    public void No_fallback_environment_means_no_retry()
    {
        Assert.False(SshEnvironmentFallback.ShouldRetry(SshRunOutcome.Fail("connect_failed", ExosError), null, null));
    }

    [Fact]
    public void The_environment_that_just_failed_is_not_retried()
    {
        var outcome = SshRunOutcome.Fail("connect_failed", ExosError);
        Assert.False(SshEnvironmentFallback.ShouldRetry(outcome, Legacy.Id, Legacy));
        Assert.True(SshEnvironmentFallback.ShouldRetry(outcome, Guid.NewGuid(), Legacy));
    }

    [Theory]
    [InlineData("auth_failed", "authentication failed: bad password")]
    [InlineData("connect_timeout", "connection timeout: timed out")]
    [InlineData("host_key_mismatch", "host key mismatch for 10.0.0.1: expected SHA256:a, got SHA256:b")]
    public void Any_other_failure_is_not_retried(string kind, string error)
    {
        Assert.False(SshEnvironmentFallback.ShouldRetry(SshRunOutcome.Fail(kind, error), null, Legacy));
    }

    [Fact]
    public void A_successful_session_is_not_retried()
    {
        var outcome = SshRunOutcome.Success(new SshRunResult
        {
            Results = [new SshCommandResult { Command = "show version", Ok = true, Output = "ExtremeXOS" }],
        });
        Assert.False(SshEnvironmentFallback.ShouldRetry(outcome, null, Legacy));
    }

    [Fact]
    public void The_outcome_reports_the_environment_and_the_original_error()
    {
        var plain = SshRunOutcome.Fail("connect_failed", ExosError);
        Assert.Equal("system", plain.PythonEnvironment);
        Assert.False(plain.SshEnvFallback);

        var retried = SshRunOutcome.Success(new SshRunResult()).WithEnvironment("ssh-legacy", fallbackFrom: ExosError);
        Assert.True(retried.Ok);
        Assert.Equal("ssh-legacy", retried.PythonEnvironment);
        Assert.True(retried.SshEnvFallback);
        Assert.Equal(ExosError, retried.SshEnvFallbackError);
    }

    [Fact]
    public void The_environment_id_never_reaches_the_runners_stdin()
    {
        var json = SshCommandRunner.SerializeRequest(new SshRunRequest
        {
            Host = "10.0.0.1", Username = "u", Commands = ["show version"],
            PythonEnvironmentId = Guid.NewGuid(),
        });
        Assert.DoesNotContain("python_environment", json);
    }

    // ── device and snippet round-trip ───────────────────────────────────────

    private static async Task<Guid> SeedEnvironmentAsync(AppDbContext db, bool active = true)
    {
        var id = Guid.NewGuid();
        db.PythonEnvironments.Add(new PythonEnvironment
        {
            PythonEnvironmentId = id, Name = $"env-{id:N}"[..12], Requirements = "x", IsActive = active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static T Value<T>(ActionResult<T> result) where T : class =>
        result.Value ?? (T)((ObjectResult)result.Result!).Value!;

    [Fact]
    public async Task A_device_keeps_its_environment_and_can_go_back_to_the_system_python()
    {
        using var db = NewDb();
        var envId = await SeedEnvironmentAsync(db);
        var controller = new DeviceController(db, new HardeningTestUser("admin"));

        var created = Value(await controller.Post(new CreateDevice
        {
            DeviceName = "exos-1", IpAddress = "10.0.0.9", Platform = "exos", PythonEnvironmentId = envId,
        }, default));
        Assert.Equal(envId, created.PythonEnvironmentId);

        var read = Value(await controller.GetById(created.DeviceId, default));
        Assert.Equal(envId, read.PythonEnvironmentId);

        // An update that does not mention it leaves it alone.
        var renamed = Value(await controller.Update(created.DeviceId, new UpdateDevice { Site = "dc2" }, default));
        Assert.Equal(envId, renamed.PythonEnvironmentId);

        var cleared = Value(await controller.Update(created.DeviceId,
            new UpdateDevice { ClearPythonEnvironment = true }, default));
        Assert.Null(cleared.PythonEnvironmentId);
        Assert.Null((await db.Devices.SingleAsync()).PythonEnvironmentId);
    }

    [Fact]
    public async Task A_device_cannot_point_at_an_environment_that_does_not_exist()
    {
        using var db = NewDb();
        var deleted = await SeedEnvironmentAsync(db, active: false);
        var controller = new DeviceController(db, new HardeningTestUser("admin"));

        var ex = await Assert.ThrowsAsync<ValidationException>(() => controller.Post(new CreateDevice
        {
            DeviceName = "exos-1", IpAddress = "10.0.0.9", PythonEnvironmentId = deleted,
        }, default));
        Assert.Equal("python_env_not_found", ex.Code);
    }

    private sealed class Registry(params string[] types) : ISnippetHandlerRegistry
    {
        public IReadOnlyCollection<string> KnownTypes => types;
        public ISnippetHandler? Resolve(string type, IServiceProvider scope) => null;
    }

    private static SnippetController SnippetController(AppDbContext db)
    {
        var user = new HardeningTestUser("operator");
        var registry = new Registry(Snippet.TypePythonSnippet, Snippet.TypeSsh, Snippet.TypeAnsiblePlaybook);
        var sp = new ServiceCollection().BuildServiceProvider();
        return new SnippetController(db, user, registry, sp, new SnippetCatalog(db, user, registry));
    }

    [Fact]
    public async Task A_python_snippet_keeps_its_environment_and_can_go_back_to_the_system_python()
    {
        using var db = NewDb();
        var envId = await SeedEnvironmentAsync(db);
        var controller = SnippetController(db);

        var created = Value(await controller.Post(new CreateSnippet
        {
            Name = "parse", Type = Snippet.TypePythonSnippet, Code = "result = 1", ChangesState = false,
            LogicDiagramMermaid = "graph TD\n  A[input] --> B[result]",
            PythonEnvironmentId = envId,
        }, default));
        Assert.Equal(envId, created.PythonEnvironmentId);
        Assert.Equal(envId, Value(await controller.GetById(created.SnippetId, default)).PythonEnvironmentId);

        var untouched = Value(await controller.Update(created.SnippetId, new UpdateSnippet { Description = "d" }, default));
        Assert.Equal(envId, untouched.PythonEnvironmentId);

        var cleared = Value(await controller.Update(created.SnippetId,
            new UpdateSnippet { ClearPythonEnvironment = true }, default));
        Assert.Null(cleared.PythonEnvironmentId);
    }

    [Fact]
    public async Task Only_snippets_that_start_python_can_carry_an_environment()
    {
        using var db = NewDb();
        var envId = await SeedEnvironmentAsync(db);
        var controller = SnippetController(db);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => controller.Post(new CreateSnippet
        {
            Name = "show", Type = Snippet.TypeSsh, PythonEnvironmentId = envId,
        }, default));
        Assert.Equal("python_env_not_applicable", ex.Code);
    }

    // ── python_snippet runs under the environment, and only a ready one ────────

    private sealed class FakeResolver(ResolvedPythonEnvironment? env, string? refusal = null) : IPythonEnvironmentResolver
    {
        public Task<ResolvedPythonEnvironment> ResolveAsync(Guid environmentId, CancellationToken ct) =>
            refusal is not null
                ? throw new PythonEnvironmentUnavailableException(refusal)
                : Task.FromResult(env!);

        public Task<ResolvedPythonEnvironment?> FindSshFallbackAsync(CancellationToken ct) =>
            Task.FromResult<ResolvedPythonEnvironment?>(null);
    }

    private sealed class CapturingSandbox : IPythonSandbox
    {
        public PythonRunRequest? Seen { get; private set; }

        public Task<PythonRunResult> RunAsync(PythonRunRequest request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new PythonRunResult(
                true, System.Text.Json.JsonDocument.Parse("1").RootElement.Clone(), "", "", null, 0));
        }
    }

    private sealed class NoSecrets : nashira_backend.Services.Ai.Secrets.ISecretResolver
    {
        public Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string> SubstituteAsync(string template, CancellationToken ct) => Task.FromResult(template);
        public Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct) =>
            Task.FromResult(template);
    }

    private static SnippetRequest PythonStep(Guid? environmentId) => new()
    {
        NodeId = "n1", WorkflowId = Guid.Empty, SnippetId = Guid.NewGuid(),
        SnippetType = Snippet.TypePythonSnippet, Code = "result = 1",
        Input = System.Text.Json.JsonDocument.Parse("{}").RootElement.Clone(),
        PythonEnvironmentId = environmentId,
    };

    [Fact]
    public async Task A_python_snippet_runs_under_its_environments_interpreter()
    {
        using var db = NewDb();
        var sandbox = new CapturingSandbox();
        var handler = new nashira_backend.Services.Worker.Handlers.PythonSnippetHandler(
            db, sandbox, new NoSecrets(), new ConfigurationBuilder().Build(),
            NullLogger<nashira_backend.Services.Worker.Handlers.PythonSnippetHandler>.Instance,
            new FakeResolver(Legacy));

        var result = await handler.ExecuteAsync(PythonStep(Legacy.Id), default);

        Assert.True(result.Success);
        Assert.Equal(Legacy.Interpreter, sandbox.Seen?.Interpreter);
    }

    [Fact]
    public async Task A_python_snippet_without_an_environment_keeps_the_system_python()
    {
        using var db = NewDb();
        var sandbox = new CapturingSandbox();
        var handler = new nashira_backend.Services.Worker.Handlers.PythonSnippetHandler(
            db, sandbox, new NoSecrets(), new ConfigurationBuilder().Build(),
            NullLogger<nashira_backend.Services.Worker.Handlers.PythonSnippetHandler>.Instance,
            new FakeResolver(Legacy));

        await handler.ExecuteAsync(PythonStep(null), default);

        Assert.Null(sandbox.Seen?.Interpreter);
    }

    [Fact]
    public async Task A_python_snippet_whose_environment_is_not_ready_fails_before_anything_runs()
    {
        using var db = NewDb();
        var sandbox = new CapturingSandbox();
        var handler = new nashira_backend.Services.Worker.Handlers.PythonSnippetHandler(
            db, sandbox, new NoSecrets(), new ConfigurationBuilder().Build(),
            NullLogger<nashira_backend.Services.Worker.Handlers.PythonSnippetHandler>.Instance,
            new FakeResolver(null, "Python environment 'tools' is not ready yet (status: building)"));

        var result = await handler.ExecuteAsync(PythonStep(Guid.NewGuid()), default);

        Assert.False(result.Success);
        Assert.Equal(PythonEnvironmentUnavailableException.Code, result.ErrorCode);
        Assert.Contains("'tools'", result.Error);
        Assert.Null(sandbox.Seen);
    }
}
