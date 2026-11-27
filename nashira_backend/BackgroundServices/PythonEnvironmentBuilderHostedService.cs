using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Settings;
using nashira_backend.Services.Trace;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.BackgroundServices;

// Builds the Python environments admins define, with uv, at runtime.
//
// Modelled on PythonPackageProvisionerHostedService and deliberately the same shape:
// the same claim (a conditional UPDATE, so several replicas never build one environment
// twice), the same on/off setting (Python:PackageProvisioningEnabled — it is the same
// decision, "may an admin screen make this server install packages"), the same
// wheels-only default (Python:PipOnlyBinary), the same trace and timeout handling.
//
//     uv venv <envs>/<name>.<id>.tmp --python <version|python3> --relocatable
//     uv pip install --python <tmp>/bin/python -r <effective requirements> [--only-binary :all:]
//     uv pip freeze --python <tmp>/bin/python
//
// then the finished build is renamed to <envs>/<name>.<id> and the symlink
// <envs>/<name> is swapped onto it with one rename(2). An execution resolves the link
// to a concrete build before it starts (PythonEnvironmentResolver), so it never sees a
// half-built environment, and a rebuild never pulls files out from under it: the
// previous build is kept until the one after.
//
// The effective requirements are the row's own Requirements plus the pip specs of
// the active allowlist rows that target the environment (python_module_environments),
// deduplicated — see PythonEnvironmentRules.EffectiveRequirements. They are what is
// installed and what RequirementsHash is computed over.
//
// A failed build marks the row `failed` and leaves the previous good build where it
// was. The row itself is not usable while failed (only `ready` is), but rebuilding
// after fixing the requirements does not start from nothing.
//
// Runs OUTSIDE the snippet sandbox: this process has network, and uv needs it.
//
// Owned by core (HostedServiceCatalog) because its consumers span two capabilities —
// the SSH runner (fleet) and python_snippet (automation) — and the catalog cannot say
// "either". It checks that one of them is enabled, the way RetentionHostedService
// checks the capabilities whose rows it prunes, and otherwise builds nothing.
public sealed class PythonEnvironmentBuilderHostedService : BackgroundService
{
    // A claim older than this belonged to a process that died mid-build. Longer than
    // the provisioner's: an environment is several packages, not one.
    private static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ITraceLogger _trace;
    private readonly AppSettingsProvider _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<PythonEnvironmentBuilderHostedService> _logger;
    private readonly ModuleSelection? _modules;
    private readonly string _claimant = $"{Environment.MachineName}:{Environment.ProcessId}";

    public PythonEnvironmentBuilderHostedService(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<PythonEnvironmentBuilderHostedService> logger,
        ITraceLogger trace,
        AppSettingsProvider settings,
        ModuleSelection? modules = null)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _trace = trace;
        _settings = settings;
        _modules = modules;
    }

    // Nothing in a deployment without devices or the workflow engine can run Python
    // through an environment, so nothing is installed for it.
    private bool HasConsumer =>
        _modules is null
        || _modules.IsEnabled(ModuleId.Fleet)
        || _modules.IsEnabled(ModuleId.Automation);

    private bool OnlyBinary => _config.GetValue("Python:PipOnlyBinary", true);
    private string UvExecutable => _config["Python:UvExecutable"] ?? "uv";
    private string PythonExecutable => _config["Python:Executable"] ?? "python3";
    private int IntervalSeconds => Math.Max(5, _config.GetValue("Python:ProvisionIntervalSeconds", 15));
    // Per uv step. Not a settings-screen row: that catalog only lists settings of
    // modules a deployment may switch off, and this belongs to core.
    private int BuildTimeoutSeconds => Math.Max(60, _config.GetValue("Python:EnvBuildTimeoutSeconds", 900));
    private bool ProvisioningEnabled => _settings.GetBool("Python:PackageProvisioningEnabled", true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!HasConsumer)
        {
            _logger.LogInformation("python.env.builder.disabled reason=no_fleet_or_automation");
            return;
        }
        if (!ProvisioningEnabled)
            _logger.LogInformation("python.env.builder.disabled reason=setting");

        var envsDir = PythonPackagesLayout.ResolveEnvsDir(_config);
        _logger.LogInformation(
            "python.env.builder.started dir={Dir} only_binary={OnlyBinary} interval={Interval}s",
            envsDir, OnlyBinary, IntervalSeconds);

        var interval = TimeSpan.FromSeconds(IntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Re-checked every cycle, like the provisioner: switching it off stops the
                // next build, and pending rows build when it is switched back on.
                if (ProvisioningEnabled)
                {
                    while (!stoppingToken.IsCancellationRequested && await BuildOneAsync(envsDir, stoppingToken))
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "python.env.builder.cycle_failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("python.env.builder.stopped");
    }

    // Returns true when a row was processed, so the caller keeps draining.
    private async Task<bool> BuildOneAsync(string envsDir, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var claim = await ClaimNextAsync(db, ct);
        if (claim is null) return false;
        var (env, claimedAt) = claim.Value;

        if (!PythonEnvironmentRules.IsValidName(env.Name))
        {
            // The API never writes such a name; a hand-edited row must not reach a path.
            await MarkFailedAsync(db, env.PythonEnvironmentId, claimedAt, $"invalid environment name '{env.Name}'", ct);
            return true;
        }

        // The EFFECTIVE requirements: the environment's own list plus the pip specs of
        // the allowlist rows that target it. One file, one install, one hash — so the
        // build is reproducible from the definition and ResolvedPackages lists what the
        // allowlist added, instead of packages pip-installed into the venv on the side.
        var included = await PythonEnvironmentService.IncludedSpecsAsync(db, env.PythonEnvironmentId, ct);
        var requirements = PythonEnvironmentRules.EffectiveRequirements(env.Requirements, included);
        var hash = PythonEnvironmentRules.Hash(env.PythonVersion, requirements);
        var buildId = Guid.NewGuid().ToString("N")[..8];
        var finalDir = Path.Combine(envsDir, $"{env.Name}.{buildId}");
        var tmpDir = finalDir + ".tmp";
        var requirementsFile = Path.Combine(envsDir, $".{env.Name}.{buildId}.requirements.txt");

        _logger.LogInformation(
            "python.env.build.begin id={Id} name={Name} python={Python} hash={Hash} included={Included}",
            env.PythonEnvironmentId, env.Name, env.PythonVersion ?? "system", hash[..12], included.Count);

        using var trace = _trace.Begin(TraceEvent.CategorySystem, "system.python.env_build",
            new { name = env.Name, python = env.PythonVersion ?? "system", hash });

        try
        {
            Directory.CreateDirectory(envsDir);
            await File.WriteAllTextAsync(requirementsFile, requirements + "\n", ct);

            // --relocatable: entry-point scripts (ansible-playbook, netmiko's CLI tools)
            // get a shebang relative to themselves instead of the absolute .tmp path,
            // which would no longer exist once the build is renamed into place.
            var venv = await RunAsync(UvExecutable,
                ["venv", tmpDir, "--python", env.PythonVersion ?? PythonExecutable, "--relocatable", "--no-config"],
                ct);
            if (venv.Exit != 0)
                return await FailBuildAsync(db, env, claimedAt, "uv venv", venv, tmpDir, trace, ct);

            var tmpPython = PythonPackagesLayout.Interpreter(tmpDir);
            var installArgs = new List<string>
            {
                "pip", "install", "--python", tmpPython, "-r", requirementsFile, "--no-config",
            };
            // Wheels only: never builds, and so never executes, an sdist's packaging code.
            if (OnlyBinary) { installArgs.Add("--only-binary"); installArgs.Add(":all:"); }
            var install = await RunAsync(UvExecutable, installArgs, ct);
            if (install.Exit != 0)
                return await FailBuildAsync(db, env, claimedAt, "uv pip install", install, tmpDir, trace, ct);

            var freeze = await RunAsync(UvExecutable, ["pip", "freeze", "--python", tmpPython, "--no-config"], ct);
            if (freeze.Exit != 0)
                return await FailBuildAsync(db, env, claimedAt, "uv pip freeze", freeze, tmpDir, trace, ct);

            // Still ours, and still what the row describes? An edit or a rebuild while uv
            // ran released the claim; an allowlist change altered the effective
            // requirements. Either way that definition is built on the next sweep, and this
            // build is thrown away rather than swapped in under a row it no longer matches.
            var state = await PythonEnvironmentService.CheckBuildClaimAsync(
                db, env.PythonEnvironmentId, _claimant, claimedAt, hash, ct);
            if (state != BuildClaimState.Current)
            {
                TryDeleteDirectory(tmpDir);
                if (state == BuildClaimState.DefinitionChanged)
                    await RequeueAsync(db, env.PythonEnvironmentId, claimedAt, ct);
                trace.Fail("superseded", new { name = env.Name });
                _logger.LogInformation("python.env.build.superseded id={Id} name={Name} reason={Reason}",
                    env.PythonEnvironmentId, env.Name, state);
                return true;
            }

            Directory.Move(tmpDir, finalDir);
            var previous = SwapInto(envsDir, env.Name, finalDir);
            PruneOldBuilds(envsDir, env.Name, keep: [finalDir, previous]);

            var resolved = freeze.Stdout.Trim();
            var marked = await db.PythonEnvironments
                .Where(e => e.PythonEnvironmentId == env.PythonEnvironmentId
                    && e.Status == PythonEnvironment.StatusBuilding
                    && e.ClaimedBy == _claimant && e.ClaimedAt == claimedAt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, PythonEnvironment.StatusReady)
                    .SetProperty(e => e.ResolvedPackages, resolved)
                    .SetProperty(e => e.RequirementsHash, hash)
                    .SetProperty(e => e.BuiltAt, DateTime.UtcNow)
                    .SetProperty(e => e.LastError, (string?)null)
                    .SetProperty(e => e.ClaimedBy, (string?)null)
                    .SetProperty(e => e.ClaimedAt, (DateTime?)null)
                    .SetProperty(e => e.UpdatedAt, DateTime.UtcNow), ct);

            _logger.LogInformation(
                "python.env.build.ok id={Id} name={Name} packages={Count} marked={Marked}",
                env.PythonEnvironmentId, env.Name, resolved.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                marked);
            trace.Complete(new { name = env.Name, hash });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down mid-build. Leave the row `building`; the stale-claim window
            // hands it to the next process instead of recording a failure that was not one.
            TryDeleteDirectory(tmpDir);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(tmpDir);
            await MarkFailedAsync(db, env.PythonEnvironmentId, claimedAt,
                PythonEnvironmentRules.Tail(ex.Message, 1000), ct);
            _logger.LogError(ex, "python.env.build.error id={Id} name={Name}", env.PythonEnvironmentId, env.Name);
            trace.Fail(ex.Message, new { name = env.Name });
            return true;
        }
        finally
        {
            try { File.Delete(requirementsFile); } catch { /* best effort */ }
        }
    }

    private async Task<bool> FailBuildAsync(
        AppDbContext db, PythonEnvironment env, DateTime claimedAt, string step, ProcessResult result,
        string tmpDir, ITraceScope trace, CancellationToken ct)
    {
        TryDeleteDirectory(tmpDir);
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        // Stored as-is (the row keeps whatever uv said) and masked on every way out:
        // uv echoes the index URL it could not reach, credentials included.
        var error = $"{step} failed (exit {result.Exit}): {PythonEnvironmentRules.Tail(detail, 2000)}";
        await MarkFailedAsync(db, env.PythonEnvironmentId, claimedAt, error, ct);
        _logger.LogWarning("python.env.build.failed id={Id} name={Name} step={Step} exit={Exit}",
            env.PythonEnvironmentId, env.Name, step, result.Exit);
        trace.Fail($"{step} exited {result.Exit}", new { name = env.Name });
        return true;
    }

    // Conditional UPDATE: only the process that flips a claimable row to `building`
    // wins, so replicas sharing the volume never build the same environment at once.
    private async Task<(PythonEnvironment Env, DateTime ClaimedAt)?> ClaimNextAsync(AppDbContext db, CancellationToken ct)
    {
        var staleBefore = DateTime.UtcNow - StaleClaim;

        var candidates = await db.PythonEnvironments.AsNoTracking()
            .Where(e => e.IsActive
                && (e.Status == PythonEnvironment.StatusPending
                    || (e.Status == PythonEnvironment.StatusBuilding
                        && (e.ClaimedAt == null || e.ClaimedAt < staleBefore))))
            .OrderBy(e => e.UpdatedAt)
            .Select(e => e.PythonEnvironmentId)
            .Take(20)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            // Truncated to whole microseconds, which is what Postgres stores, so the
            // equality check on the way out compares like with like.
            var now = DateTime.UtcNow;
            now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
            var claimed = await db.PythonEnvironments
                .Where(e => e.PythonEnvironmentId == id && e.IsActive
                    && (e.Status == PythonEnvironment.StatusPending
                        || (e.Status == PythonEnvironment.StatusBuilding
                            && (e.ClaimedAt == null || e.ClaimedAt < staleBefore))))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, PythonEnvironment.StatusBuilding)
                    .SetProperty(e => e.ClaimedBy, _claimant)
                    .SetProperty(e => e.ClaimedAt, now)
                    .SetProperty(e => e.UpdatedAt, now), ct);

            if (claimed == 1)
            {
                var row = await db.PythonEnvironments.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.PythonEnvironmentId == id, ct);
                if (row is not null) return (row, now);
            }
        }

        return null;
    }

    // The claim is still ours but the definition moved on underneath the build: hand
    // the row back to the queue so the current definition is built next.
    private Task RequeueAsync(AppDbContext db, Guid id, DateTime claimedAt, CancellationToken ct) =>
        db.PythonEnvironments
            .Where(e => e.PythonEnvironmentId == id
                && e.Status == PythonEnvironment.StatusBuilding
                && e.ClaimedBy == _claimant && e.ClaimedAt == claimedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, PythonEnvironment.StatusPending)
                .SetProperty(e => e.ClaimedBy, (string?)null)
                .SetProperty(e => e.ClaimedAt, (DateTime?)null)
                .SetProperty(e => e.UpdatedAt, DateTime.UtcNow), ct);

    // Only while the claim is still ours: an edit that re-queued the row must not be
    // overwritten with the failure of the definition it replaced.
    private Task MarkFailedAsync(AppDbContext db, Guid id, DateTime claimedAt, string error, CancellationToken ct) =>
        db.PythonEnvironments
            .Where(e => e.PythonEnvironmentId == id
                && e.Status == PythonEnvironment.StatusBuilding
                && e.ClaimedBy == _claimant && e.ClaimedAt == claimedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, PythonEnvironment.StatusFailed)
                .SetProperty(e => e.LastError, error)
                .SetProperty(e => e.ClaimedBy, (string?)null)
                .SetProperty(e => e.ClaimedAt, (DateTime?)null)
                .SetProperty(e => e.UpdatedAt, DateTime.UtcNow), ct);

    // Points <envs>/<name> at the new build and returns the build it pointed at before
    // (null when there was none).
    //
    // On Linux the link is replaced by rename(2) of a freshly made link onto it, which is
    // atomic: a reader resolving the path gets the old build or the new one, never
    // nothing. Elsewhere (a developer's Windows machine, where symlinks need privileges)
    // the build directory is moved into place, which is not atomic and does not need to be.
    private static string? SwapInto(string envsDir, string name, string buildDir)
    {
        var link = Path.Combine(envsDir, name);
        var previous = PythonEnvironmentResolver.ResolveBuildDir(link);
        previous = string.Equals(previous, link, StringComparison.Ordinal) ? null : previous;

        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(link)) Directory.Delete(link, recursive: true);
            Directory.Move(buildDir, link);
            return null;
        }

        // A plain directory at the link's path (left by a hand-made environment) cannot
        // be renamed over; move it aside so the swap below can happen.
        var existing = new DirectoryInfo(link);
        if (existing.Exists && existing.LinkTarget is null)
        {
            var aside = $"{link}.legacy-{Guid.NewGuid().ToString("N")[..8]}";
            Directory.Move(link, aside);
            previous = aside;
        }

        var tmpLink = Path.Combine(envsDir, $".{name}.{Guid.NewGuid().ToString("N")[..8]}.link");
        // Relative target, so the volume can be mounted elsewhere and still resolve.
        Directory.CreateSymbolicLink(tmpLink, Path.GetFileName(buildDir));
        if (Rename(tmpLink, link) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            try { File.Delete(tmpLink); } catch { /* best effort */ }
            throw new IOException($"could not swap {link} onto {buildDir} (errno {errno})");
        }
        return previous;
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename(string oldPath, string newPath);

    // Keeps the current build and the one before it — a job that resolved the previous
    // build just before the swap is still running on it — and removes older ones and
    // any abandoned .tmp of this environment.
    private void PruneOldBuilds(string envsDir, string name, IEnumerable<string?> keep)
    {
        var kept = new HashSet<string>(keep.Where(k => k is not null).Select(k => Path.GetFullPath(k!)),
            StringComparer.Ordinal);
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(envsDir, name + ".*"))
            {
                var info = new DirectoryInfo(dir);
                if (info.LinkTarget is not null) continue;
                if (kept.Contains(Path.GetFullPath(dir))) continue;
                // Names have no dots, so `<name>.` cannot be the start of another
                // environment's directory; still, only what this service creates —
                // `<name>.<8 hex>[.tmp]` and `<name>.legacy-…` — is ever removed.
                var suffix = info.Name[(name.Length + 1)..];
                var isBuild = suffix.Length >= 8 && suffix[..8].All(Uri.IsHexDigit);
                if (!isBuild && !suffix.StartsWith("legacy-", StringComparison.Ordinal)) continue;
                TryDeleteDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "python.env.prune_failed name={Name}", name);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort; the next prune retries */ }
    }

    private sealed record ProcessResult(int Exit, string Stdout, string Stderr);

    private async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // The image sets both under the persistent volume; a deployment that does not
        // still gets them there instead of in a home directory the app user may not have.
        var root = PythonPackagesLayout.ResolveRoot(_config);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UV_CACHE_DIR")))
            psi.Environment["UV_CACHE_DIR"] = Path.Combine(root, PythonPackagesLayout.UvCacheDirName);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UV_PYTHON_INSTALL_DIR")))
            psi.Environment["UV_PYTHON_INSTALL_DIR"] = Path.Combine(root, PythonPackagesLayout.UvPythonDirName);
        // The image's PYTHONPATH points at the SSH runner's modules; it has no business
        // inside a build, where it would leak onto sys.path of every probe uv runs.
        psi.Environment.Remove("PYTHONPATH");
        psi.Environment["NO_COLOR"] = "1";

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, $"could not start {file}: {ex.Message}");
        }
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(BuildTimeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcessResult(-1, string.Empty, $"timed out after {BuildTimeoutSeconds}s");
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }

        return new ProcessResult(proc.ExitCode, await stdout, await stderr);
    }
}
