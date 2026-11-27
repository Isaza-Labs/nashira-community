using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.BackgroundServices;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Python;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

// Install targets on the Python packages allowlist: a pip row goes to the system Python
// (InstallInSystem), to uv environments (python_module_environments), or both. An
// environment receives the packages through its EFFECTIVE requirements — its own list
// plus the specs of the rows that target it — so the builder installs one file, the hash
// covers it, and every change to what an environment includes puts it back to pending.
public class PythonModuleInstallTargetTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"python-targets-{Guid.NewGuid()}")
            .Options);

    private sealed class CapturingAudit : IAuditLogger
    {
        public List<(string Action, object? Before, object? After)> Entries { get; } = [];

        public Task LogAsync(string entityType, Guid? entityId, string action,
            object? before = null, object? after = null, CancellationToken ct = default)
        {
            Entries.Add((action, before, after));
            return Task.CompletedTask;
        }
    }

    private static AllowedPythonModuleController Modules(AppDbContext db, IAuditLogger? audit = null) =>
        new(db, new HardeningTestUser("admin"), audit ?? new CapturingAudit());

    private static T Value<T>(ActionResult<T> result) where T : class =>
        result.Value ?? (T)((ObjectResult)result.Result!).Value!;

    private static async Task<Guid> EnvAsync(
        AppDbContext db, string name, string status = PythonEnvironment.StatusReady, bool active = true,
        string requirements = "paramiko>=3.5,<5")
    {
        var id = Guid.NewGuid();
        db.PythonEnvironments.Add(new PythonEnvironment
        {
            PythonEnvironmentId = id, Name = name, Requirements = requirements, Status = status, IsActive = active,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<string> StatusOf(AppDbContext db, Guid envId) =>
        (await db.PythonEnvironments.AsNoTracking().SingleAsync(e => e.PythonEnvironmentId == envId)).Status;

    private static async Task SetReadyAsync(AppDbContext db, params Guid[] envIds)
    {
        foreach (var env in await db.PythonEnvironments.Where(e => envIds.Contains(e.PythonEnvironmentId)).ToListAsync())
            env.Status = PythonEnvironment.StatusReady;
        await db.SaveChangesAsync();
    }

    private static WriteAllowedModule Pip(
        string module, string? spec = null, bool? inSystem = null, params Guid[] envs) => new()
    {
        Module = module, Source = "pip", PipSpec = spec, InstallInSystem = inSystem,
        EnvironmentIds = envs.Length == 0 ? null : envs.ToList(),
    };

    // ── effective requirements ──────────────────────────────────────────────

    [Fact]
    public void Effective_requirements_are_the_own_list_followed_by_the_included_specs()
    {
        var effective = PythonEnvironmentRules.EffectiveRequirements(
            "paramiko>=3.5,<5\ntextfsm", ["netmiko==4.3.0", "jinja2"]);

        Assert.Equal(
            "paramiko>=3.5,<5\ntextfsm\n" + PythonEnvironmentRules.IncludedHeader + "\njinja2\nnetmiko==4.3.0",
            effective);
    }

    [Fact]
    public void Included_specs_are_deduplicated_against_each_other_and_the_own_list()
    {
        var effective = PythonEnvironmentRules.EffectiveRequirements(
            "# pinned for EXOS\nnetmiko == 4.3.0\n",
            ["Netmiko==4.3.0", " jinja2 ", "jinja2", "JINJA2", "", null]);

        // netmiko is already listed (case and spacing aside); jinja2 appears once.
        Assert.Equal(
            "# pinned for EXOS\nnetmiko == 4.3.0\n" + PythonEnvironmentRules.IncludedHeader + "\njinja2",
            effective);
        Assert.Equal(["jinja2"], PythonEnvironmentRules.NormalizeIncluded(["jinja2", "JINJA2 ", null]));
    }

    [Fact]
    public void A_spec_overlapping_an_own_line_only_by_package_name_is_kept_for_uv_to_intersect()
    {
        var effective = PythonEnvironmentRules.EffectiveRequirements("netmiko>=4,<5", ["netmiko==4.3.0"]);
        Assert.EndsWith("netmiko==4.3.0", effective);
    }

    [Fact]
    public void Nothing_included_keeps_the_hash_an_environment_was_built_with_before_targets()
    {
        Assert.Equal(
            PythonEnvironmentRules.Hash("3.12", "paramiko>=3.5,<5"),
            PythonEnvironmentRules.EffectiveHash("3.12", "paramiko>=3.5,<5", []));
    }

    [Fact]
    public void The_hash_covers_the_included_specs_and_ignores_their_order()
    {
        var own = PythonEnvironmentRules.EffectiveHash(null, "paramiko", []);
        var ab = PythonEnvironmentRules.EffectiveHash(null, "paramiko", ["a==1", "b==2"]);
        var ba = PythonEnvironmentRules.EffectiveHash(null, "paramiko", ["b==2", "a==1", "a==1"]);
        var other = PythonEnvironmentRules.EffectiveHash(null, "paramiko", ["a==1", "b==3"]);

        Assert.NotEqual(own, ab);
        Assert.Equal(ab, ba);
        Assert.NotEqual(ab, other);
    }

    [Fact]
    public async Task Only_active_pip_rows_that_target_the_environment_are_included()
    {
        using var db = NewDb();
        var env = await EnvAsync(db, "netops");
        var other = await EnvAsync(db, "other");
        var modules = Modules(db);

        await modules.Post(Pip("netmiko", "netmiko==4.3.0", envs: env), default);
        await modules.Post(Pip("jinja2", envs: [env, other]), default);
        var revoked = Value(await modules.Post(Pip("yaml", "pyyaml", envs: env), default));
        await modules.Delete(revoked.Id, default);
        await modules.Post(Pip("textfsm"), default); // system only

        Assert.Equal(["jinja2", "netmiko==4.3.0"], await PythonEnvironmentService.IncludedSpecsAsync(db, env, default));
        Assert.Equal(["jinja2"], await PythonEnvironmentService.IncludedSpecsAsync(db, other, default));
    }

    // ── which environments go back to pending ───────────────────────────────

    [Fact]
    public async Task Adding_a_target_marks_only_that_environment_pending()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var b = await EnvAsync(db, "env-b");

        var created = Value(await Modules(db).Post(Pip("netmiko", envs: a), default));

        Assert.Equal(PythonEnvironment.StatusPending, await StatusOf(db, a));
        Assert.Equal(PythonEnvironment.StatusReady, await StatusOf(db, b));
        var target = Assert.Single(created.Environments);
        Assert.Equal("env-a", target.Name);
        Assert.Equal(PythonEnvironment.StatusPending, target.Status);
        Assert.True(created.InstallInSystem);
    }

    [Fact]
    public async Task Moving_a_target_marks_the_old_and_the_new_environment_pending_and_no_other()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var b = await EnvAsync(db, "env-b");
        var c = await EnvAsync(db, "env-c");
        var modules = Modules(db);
        var created = Value(await modules.Post(Pip("netmiko", envs: [a, c]), default));
        await SetReadyAsync(db, a, b, c);

        var updated = Value(await modules.Update(created.Id,
            new WriteAllowedModule { EnvironmentIds = [b, c] }, default));

        Assert.Equal(PythonEnvironment.StatusPending, await StatusOf(db, a)); // removed: rebuild without it
        Assert.Equal(PythonEnvironment.StatusPending, await StatusOf(db, b)); // added
        Assert.Equal(PythonEnvironment.StatusReady, await StatusOf(db, c));   // unchanged target
        Assert.Equal(["env-b", "env-c"], updated.Environments.Select(e => e.Name));
    }

    [Fact]
    public async Task An_update_that_does_not_mention_targets_leaves_them_and_the_environments_alone()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var modules = Modules(db);
        var created = Value(await modules.Post(Pip("netmiko", envs: a), default));
        await SetReadyAsync(db, a);

        var updated = Value(await modules.Update(created.Id,
            new WriteAllowedModule { Description = "for the EXOS playbooks" }, default));

        Assert.Equal(PythonEnvironment.StatusReady, await StatusOf(db, a));
        Assert.Single(updated.Environments);
    }

    [Fact]
    public async Task An_empty_list_removes_every_environment_target()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var modules = Modules(db);
        var created = Value(await modules.Post(Pip("netmiko", envs: a), default));
        await SetReadyAsync(db, a);

        var updated = Value(await modules.Update(created.Id, new WriteAllowedModule { EnvironmentIds = [] }, default));

        Assert.Empty(updated.Environments);
        Assert.Empty(await db.PythonModuleEnvironments.ToListAsync());
        Assert.Equal(PythonEnvironment.StatusPending, await StatusOf(db, a));
    }

    [Fact]
    public async Task A_new_pip_spec_marks_every_targeted_environment_pending()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var b = await EnvAsync(db, "env-b");
        var modules = Modules(db);
        var created = Value(await modules.Post(Pip("netmiko", "netmiko==4.2.0", envs: a), default));
        await SetReadyAsync(db, a);

        await modules.Update(created.Id, new WriteAllowedModule { PipSpec = "netmiko==4.3.0" }, default);

        Assert.Equal(PythonEnvironment.StatusPending, await StatusOf(db, a));
        Assert.Equal(PythonEnvironment.StatusReady, await StatusOf(db, b));
        Assert.Equal(["netmiko==4.3.0"], await PythonEnvironmentService.IncludedSpecsAsync(db, a, default));
    }

    [Fact]
    public async Task Revoking_a_row_marks_its_environments_pending_and_drops_its_links()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var modules = Modules(db);
        var created = Value(await modules.Post(Pip("netmiko", envs: a), default));
        await SetReadyAsync(db, a);

        await modules.Delete(created.Id, default);

        Assert.Equal(PythonEnvironment.StatusPending, await StatusOf(db, a));
        Assert.Empty(await db.PythonModuleEnvironments.ToListAsync());
    }

    [Fact]
    public async Task A_target_change_releases_a_running_builds_claim()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a", PythonEnvironment.StatusBuilding);
        var env = await db.PythonEnvironments.SingleAsync();
        env.ClaimedBy = "builder";
        env.ClaimedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await Modules(db).Post(Pip("netmiko", envs: a), default);

        var row = await db.PythonEnvironments.AsNoTracking().SingleAsync();
        Assert.Equal(PythonEnvironment.StatusPending, row.Status);
        Assert.Null(row.ClaimedBy);
    }

    // ── validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_environment_id_is_refused()
    {
        using var db = NewDb();
        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            Modules(db).Post(Pip("netmiko", envs: Guid.NewGuid()), default));
        Assert.Equal("python_env_not_found", ex.Code);
        Assert.Contains("environment_ids", ex.Message);
        Assert.Empty(await db.AllowedPythonModules.ToListAsync());
    }

    [Fact]
    public async Task A_deleted_environment_is_refused_on_create_and_on_update()
    {
        using var db = NewDb();
        var deleted = await EnvAsync(db, "gone", active: false);
        var modules = Modules(db);

        var create = await Assert.ThrowsAsync<ValidationException>(() =>
            modules.Post(Pip("netmiko", envs: deleted), default));
        Assert.Equal("python_env_not_found", create.Code);

        var row = Value(await modules.Post(Pip("netmiko"), default));
        var update = await Assert.ThrowsAsync<ValidationException>(() =>
            modules.Update(row.Id, new WriteAllowedModule { EnvironmentIds = [deleted] }, default));
        Assert.Equal("python_env_not_found", update.Code);
        Assert.Empty(await db.PythonModuleEnvironments.ToListAsync());
    }

    [Fact]
    public async Task A_stdlib_row_has_no_install_targets()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var modules = Modules(db);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => modules.Post(new WriteAllowedModule
        {
            Module = "json", Source = "stdlib", EnvironmentIds = [a],
        }, default));
        Assert.Equal("install_targets_not_applicable", ex.Code);

        var json = Value(await modules.Post(new WriteAllowedModule { Module = "json" }, default));
        Assert.Empty(json.Environments);
    }

    [Fact]
    public async Task Environments_only_needs_the_import_name_because_nothing_discovers_it()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var modules = Modules(db);

        var ex = await Assert.ThrowsAsync<ValidationException>(() =>
            modules.Post(Pip("python-dateutil", inSystem: false, envs: a), default));
        Assert.Equal("module_import_name_required", ex.Code);

        var ok = Value(await modules.Post(Pip("dateutil", "python-dateutil", inSystem: false, envs: a), default));
        Assert.False(ok.InstallInSystem);
    }

    // ── the system install ──────────────────────────────────────────────────

    [Fact]
    public void A_row_that_does_not_target_the_system_python_skips_the_system_install()
    {
        var plan = PythonPackageProvisionerHostedService.PlanSystemInstall(new AllowedPythonModule
        {
            Module = "netmiko", Source = AllowedPythonModule.SourcePip, PipSpec = "netmiko", InstallInSystem = false,
        });
        Assert.Equal(PythonPackageProvisionerHostedService.SystemInstallKind.SkipReady, plan.Kind);

        var installs = PythonPackageProvisionerHostedService.PlanSystemInstall(new AllowedPythonModule
        {
            Module = "netmiko", Source = AllowedPythonModule.SourcePip, PipSpec = "netmiko",
        });
        Assert.Equal(PythonPackageProvisionerHostedService.SystemInstallKind.Install, installs.Kind);
    }

    [Fact]
    public void A_skipped_row_without_an_import_name_fails_instead_of_reaching_the_allowlist()
    {
        var plan = PythonPackageProvisionerHostedService.PlanSystemInstall(new AllowedPythonModule
        {
            Module = "python-dateutil", Source = AllowedPythonModule.SourcePip, InstallInSystem = false,
        });
        Assert.Equal(PythonPackageProvisionerHostedService.SystemInstallKind.SkipFailed, plan.Kind);
        Assert.Contains("import", plan.Error);
    }

    [Fact]
    public async Task Existing_and_new_rows_install_in_the_system_python_by_default()
    {
        Assert.True(new AllowedPythonModule().InstallInSystem);

        using var db = NewDb();
        var row = Value(await Modules(db).Post(Pip("netmiko"), default));
        Assert.True(row.InstallInSystem);
        Assert.Empty(row.Environments);
    }

    [Fact]
    public async Task Turning_the_system_target_back_on_queues_an_install()
    {
        using var db = NewDb();
        var modules = Modules(db);
        var row = Value(await modules.Post(Pip("netmiko", inSystem: false), default));
        var stored = await db.AllowedPythonModules.SingleAsync();
        stored.Status = AllowedPythonModule.StatusReady;
        await db.SaveChangesAsync();

        // On: the system interpreter needs the package on disk before it is offered.
        var on = Value(await modules.Update(row.Id, new WriteAllowedModule { InstallInSystem = true }, default));
        Assert.Equal(AllowedPythonModule.StatusPending, on.Status);

        stored.Status = AllowedPythonModule.StatusReady;
        await db.SaveChangesAsync();
        // Off while ready: stays ready — and the files stay in the site dir, since the
        // provisioner has no uninstall.
        var off = Value(await modules.Update(row.Id, new WriteAllowedModule { InstallInSystem = false }, default));
        Assert.Equal(AllowedPythonModule.StatusReady, off.Status);
        Assert.False(off.InstallInSystem);
    }

    // ── the builder's stale-claim guard ─────────────────────────────────────

    private static async Task<(Guid Id, DateTime ClaimedAt, string Hash)> ClaimedBuildAsync(AppDbContext db)
    {
        var id = await EnvAsync(db, "env-a", PythonEnvironment.StatusBuilding);
        var claimedAt = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var row = await db.PythonEnvironments.SingleAsync(e => e.PythonEnvironmentId == id);
        row.ClaimedBy = "builder";
        row.ClaimedAt = claimedAt;
        await db.SaveChangesAsync();
        // What the builder computed when it started: the definition at claim time.
        return (id, claimedAt, await PythonEnvironmentService.EffectiveHashAsync(db, row, default));
    }

    [Fact]
    public async Task A_build_whose_effective_requirements_are_unchanged_may_be_marked_ready()
    {
        using var db = NewDb();
        var (id, claimedAt, hash) = await ClaimedBuildAsync(db);

        Assert.Equal(BuildClaimState.Current,
            await PythonEnvironmentService.CheckBuildClaimAsync(db, id, "builder", claimedAt, hash, default));
    }

    [Fact]
    public async Task A_target_added_during_the_build_is_never_marked_ready_with_stale_packages()
    {
        using var db = NewDb();
        var (id, claimedAt, hash) = await ClaimedBuildAsync(db);
        db.AllowedPythonModules.Add(new AllowedPythonModule
        {
            AllowedPythonModuleId = Guid.NewGuid(), Module = "netmiko", Source = AllowedPythonModule.SourcePip,
            PipSpec = "netmiko==4.3.0", Status = AllowedPythonModule.StatusReady,
        });
        await db.SaveChangesAsync();
        // Linked without going through the API — so the claim is still held, and only the
        // effective-requirements comparison can tell the build is stale.
        db.PythonModuleEnvironments.Add(new PythonModuleEnvironment
        {
            AllowedPythonModuleId = (await db.AllowedPythonModules.SingleAsync()).AllowedPythonModuleId,
            PythonEnvironmentId = id,
        });
        await db.SaveChangesAsync();

        Assert.Equal(BuildClaimState.DefinitionChanged,
            await PythonEnvironmentService.CheckBuildClaimAsync(db, id, "builder", claimedAt, hash, default));
    }

    [Fact]
    public async Task A_target_change_through_the_api_loses_the_claim()
    {
        using var db = NewDb();
        var (id, claimedAt, hash) = await ClaimedBuildAsync(db);

        await Modules(db).Post(Pip("netmiko", envs: id), default);

        Assert.Equal(BuildClaimState.Lost,
            await PythonEnvironmentService.CheckBuildClaimAsync(db, id, "builder", claimedAt, hash, default));
    }

    // ── deleting an environment ─────────────────────────────────────────────

    [Fact]
    public async Task Deleting_an_environment_drops_its_links_and_keeps_the_allowlist_row()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var b = await EnvAsync(db, "env-b");
        var modules = Modules(db);
        var created = Value(await modules.Post(Pip("netmiko", envs: [a, b]), default));

        await new PythonEnvironmentService(db, new HardeningTestUser("admin")).DeleteAsync(a, default);

        var link = Assert.Single(await db.PythonModuleEnvironments.ToListAsync());
        Assert.Equal(b, link.PythonEnvironmentId);
        var row = Value(await modules.Get(ct: default)).Items.Single();
        Assert.Equal(created.Id, row.Id);
        Assert.Equal(["env-b"], row.Environments.Select(e => e.Name));
    }

    [Fact]
    public async Task An_environment_targeted_by_the_allowlist_can_still_be_deleted()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        await Modules(db).Post(Pip("netmiko", envs: a), default);

        var deleted = await new PythonEnvironmentService(db, new HardeningTestUser("admin")).DeleteAsync(a, default);

        Assert.False(deleted.IsActive);
    }

    // ── environment responses ───────────────────────────────────────────────

    [Fact]
    public async Task An_environment_response_lists_what_the_allowlist_includes()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        await Modules(db).Post(Pip("netmiko", "netmiko==4.3.0", envs: a), default);
        var controller = new PythonEnvironmentController(
            new PythonEnvironmentService(db, new HardeningTestUser("admin")), new CapturingAudit());

        var one = Value(await controller.GetById(a, default));
        var list = Value(await controller.Get(default)).Items.Single();

        Assert.Equal(["netmiko==4.3.0"], one.IncludedPackages);
        Assert.Equal(["netmiko==4.3.0"], list.IncludedPackages);
    }

    // ── masking ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pip_specs_and_errors_are_masked_in_responses_audit_and_included_packages()
    {
        using var db = NewDb();
        var a = await EnvAsync(db, "env-a");
        var id = Guid.NewGuid();
        // The API's charset cannot carry a URL today; a hand-written row can, and the
        // masking must not depend on the validation in front of it.
        const string spec = "netmiko@https://u:pw@idx.example/netmiko-4.3.0.whl";
        db.AllowedPythonModules.Add(new AllowedPythonModule
        {
            AllowedPythonModuleId = id, Module = "netmiko", Source = AllowedPythonModule.SourcePip, PipSpec = spec,
            Status = AllowedPythonModule.StatusFailed,
            Error = "pip install failed: could not reach https://u:pw@idx.example/netmiko-4.3.0.whl",
        });
        db.PythonModuleEnvironments.Add(new PythonModuleEnvironment { AllowedPythonModuleId = id, PythonEnvironmentId = a });
        await db.SaveChangesAsync();
        var audit = new CapturingAudit();
        var modules = Modules(db, audit);

        var listed = Value(await modules.Get(ct: default)).Items.Single();
        Assert.DoesNotContain("pw@", listed.PipSpec);
        Assert.Contains("***@idx.example", listed.PipSpec);
        Assert.DoesNotContain("pw@", listed.Error);

        var envResponse = PythonEnvironmentResponse.From(
            await db.PythonEnvironments.SingleAsync(),
            await PythonEnvironmentService.IncludedSpecsAsync(db, a, default));
        Assert.DoesNotContain("pw@", Assert.Single(envResponse.IncludedPackages));

        await modules.Retry(id, default);
        await modules.Delete(id, default);
        Assert.NotEmpty(audit.Entries);
        foreach (var (_, before, after) in audit.Entries)
        {
            Assert.DoesNotContain("pw@", System.Text.Json.JsonSerializer.Serialize(before));
            Assert.DoesNotContain("pw@", System.Text.Json.JsonSerializer.Serialize(after));
        }

        // The stored row keeps them: an install needs them.
        Assert.Equal(spec, (await db.AllowedPythonModules.SingleAsync()).PipSpec);
    }
}
