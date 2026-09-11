using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Worker.Python;
using nashira_backend.Services.Trace;
using nashira_backend.Services.Settings;

namespace nashira_backend.BackgroundServices;

// Installs the pip packages an admin approved on the python_snippet allowlist.
//
// Without this, a `pip` row was a promise nobody kept: the allowlist said `netmiko`
// was permitted, the interpreter had never heard of it, and the snippet failed with an
// ImportError that pointed at the script rather than at the missing install. The row
// carried Status/PipSpec columns for exactly this and nothing ever moved them.
//
//     pip3 install --target <PackagesDir>/site [--only-binary :all:] <pip_spec>
//
// Runs OUTSIDE the snippet sandbox — this process has network, the sandbox is the
// thing that does not. Rows are claimed with a conditional UPDATE, so several replicas
// can run this and no package is installed twice.
//
// Security: pip executes packaging code from sdists, so wheels-only is the default
// (Python:PipOnlyBinary). An operator who needs a source distribution turns it off
// knowingly. Approving a pip package is already the highest-privilege action on this
// screen — it ends with third-party code running wherever snippets run.
public sealed partial class PythonPackageProvisionerHostedService : BackgroundService
{
    // A claim older than this belonged to a process that died mid-install. Long enough
    // that a genuinely slow install is never stolen from underneath itself.
    private static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly ITraceLogger _trace;
    private readonly AppSettingsProvider _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<PythonPackageProvisionerHostedService> _logger;

    public PythonPackageProvisionerHostedService(
        IServiceScopeFactory scopes,
        IConfiguration config,
        ILogger<PythonPackageProvisionerHostedService> logger,
        ITraceLogger trace,
        AppSettingsProvider settings)
    {
        _settings = settings;
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _trace = trace;
    }

    private bool OnlyBinary => _config.GetValue("Python:PipOnlyBinary", true);
    private string PipExecutable => _config["Python:PipExecutable"] ?? "pip3";
    private string PythonExecutable => _config["Python:Executable"] ?? "python3";
    private int IntervalSeconds => Math.Max(5, _config.GetValue("Python:ProvisionIntervalSeconds", 15));
    private int InstallTimeoutSeconds =>
        Math.Max(30, _settings.GetInt("Python:PipInstallTimeoutSeconds", 300));

    // Checked every cycle rather than once at startup. Read once, turning provisioning
    // off would have taken effect only on restart, and turning it back on would have
    // needed another one — while the queued packages sat there looking broken.
    private bool ProvisioningEnabled =>
        _settings.GetBool("Python:PackageProvisioningEnabled", true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Opt-out for a deployment that does not want pip reachable from an admin
        // screen at all. Default on: the column exists, the UI offers it, and a
        // silently-never-installed package is worse than no feature.
        if (!ProvisioningEnabled)
            _logger.LogInformation("python.pkg.provisioner.disabled reason=setting");

        var siteDir = PythonPackagesLayout.ResolveSiteDir(_config);
        _logger.LogInformation(
            "python.pkg.provisioner.started dir={Dir} only_binary={OnlyBinary} interval={Interval}s",
            siteDir, OnlyBinary, IntervalSeconds);

        var interval = TimeSpan.FromSeconds(IntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Re-checked each cycle, so switching it off stops the next install
                // rather than the next restart. The loop keeps turning either way:
                // pending rows stay pending and install when it is switched back on.
                if (!ProvisioningEnabled)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), stoppingToken); }
                    catch (OperationCanceledException) { return; }
                    continue;
                }

                // Drain everything claimable before sleeping: approving five packages
                // at once should not take five sweeps.
                while (!stoppingToken.IsCancellationRequested && await ProvisionOneAsync(siteDir, stoppingToken))
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "python.pkg.provisioner.cycle_failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("python.pkg.provisioner.stopped");
    }

    // Returns true when a row was processed, so the caller keeps draining.
    private async Task<bool> ProvisionOneAsync(string siteDir, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var module = await ClaimNextAsync(db, ct);
        if (module is null) return false;

        var spec = string.IsNullOrWhiteSpace(module.PipSpec) ? module.Module : module.PipSpec.Trim();

        _logger.LogInformation(
            "python.pkg.install.begin id={Id} module={Module} spec={Spec} target={Target}",
            module.AllowedPythonModuleId, module.Module, spec, siteDir);

        // A pip install reaches the network and can hang for its whole timeout. Until
        // now the only sign was the row sitting at `installing` with no way to tell a
        // slow download from a dead process.
        using var trace = _trace.Begin(TraceEvent.CategorySystem, "system.python.install",
            new { module = module.Module, spec, target = siteDir });

        try
        {
            Directory.CreateDirectory(siteDir);

            var install = await InstallAsync(siteDir, spec, ct);
            if (install.Exit != 0)
            {
                var detail = string.IsNullOrWhiteSpace(install.Stderr) ? install.Stdout : install.Stderr;
                await MarkFailedAsync(db, module.AllowedPythonModuleId,
                    $"pip install failed (exit {install.Exit}): {Tail(detail, 1000)}", ct);
                _logger.LogWarning(
                    "python.pkg.install.failed id={Id} module={Module} exit={Exit}",
                    module.AllowedPythonModuleId, module.Module, install.Exit);
                trace.Fail($"pip exited {install.Exit}", new { module = module.Module, spec });
                return true;
            }

            // pip succeeding does not mean the module imports: `pip install pyyaml`
            // gives you `yaml`, and a `ready` row whose name does not import would
            // fail at snippet time — far from where the mistake was made.
            //
            // So ask the installed distribution what it actually provides, rather
            // than making an admin know. python-dateutil imports as dateutil,
            // beautifulsoup4 as bs4, Pillow as PIL: none of those are derivable from
            // the package name, and a lookup table would be wrong for the next one.
            // The distribution knows, and now that it is installed we can ask it.
            var discovered = await DiscoverImportNameAsync(siteDir, spec, ct);
            if (discovered is { Length: > 0 }
                && !string.Equals(discovered, module.Module, StringComparison.OrdinalIgnoreCase))
            {
                var renamed = await RenameModuleAsync(db, module, discovered, ct);
                if (!renamed)
                {
                    await MarkFailedAsync(db, module.AllowedPythonModuleId,
                        $"'{spec}' imports as '{discovered}', which is already on the allowlist — "
                        + "remove one of the two rows", ct);
                    trace.Fail($"'{discovered}' already allowed", new { module = module.Module, spec });
                    return true;
                }
                _logger.LogInformation(
                    "python.pkg.import_name id={Id} spec={Spec} module={Module}",
                    module.AllowedPythonModuleId, spec, discovered);
            }

            // Only a bare identifier is ever interpolated into generated Python. A pip
            // row may carry a distribution name, which is not one — `python-dateutil`
            // compiles to a syntax error, `ruamel.yaml` imports something else — so a
            // name discovery could not resolve fails here rather than reaching the
            // interpolation. (Flow Weaver's suite caught this; the charsets make it
            // harmless today, and relying on that reading is how it stops being.)
            if (!ImportNameRegex().IsMatch(module.Module))
            {
                await MarkFailedAsync(db, module.AllowedPythonModuleId,
                    $"'{spec}' installed, but the module it provides could not be determined — "
                    + "set the module name by hand (the name a script writes after `import`)", ct);
                _logger.LogWarning(
                    "python.pkg.import_name.unresolved id={Id} spec={Spec}",
                    module.AllowedPythonModuleId, spec);
                trace.Fail("import name unresolved", new { module = module.Module, spec });
                return true;
            }

            var verify = await VerifyImportAsync(siteDir, module.Module, ct);
            if (verify.Exit != 0)
            {
                await MarkFailedAsync(db, module.AllowedPythonModuleId,
                    $"installed, but '{module.Module}' did not import — check the module name against the "
                    + $"pip package (pip install pyyaml imports as yaml). {Tail(verify.Stderr, 500)}", ct);
                _logger.LogWarning(
                    "python.pkg.verify.failed id={Id} module={Module}",
                    module.AllowedPythonModuleId, module.Module);
                trace.Fail($"installed, but '{module.Module}' did not import",
                    new { module = module.Module, spec });
                return true;
            }

            var version = verify.Stdout.Trim();
            await MarkReadyAsync(db, module.AllowedPythonModuleId,
                string.IsNullOrWhiteSpace(version) ? null : version, ct);
            _logger.LogInformation(
                "python.pkg.install.ok id={Id} module={Module} version={Version}",
                module.AllowedPythonModuleId, module.Module, version);
            trace.Complete(new { module = module.Module, spec, version });
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down mid-install. Leave the row `installing` — the stale-claim
            // window hands it back to the next process rather than recording a
            // failure that never happened.
            throw;
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(db, module.AllowedPythonModuleId, Tail(ex.Message, 500), ct);
            _logger.LogError(ex, "python.pkg.install.error id={Id} module={Module}",
                module.AllowedPythonModuleId, module.Module);
            trace.Fail(ex.Message, new { module = module.Module, spec });
            return true;
        }
    }

    // Conditional UPDATE: only the process that flips a claimable row to `installing`
    // wins, so replicas never install the same package concurrently into one volume.
    private static async Task<AllowedPythonModule?> ClaimNextAsync(AppDbContext db, CancellationToken ct)
    {
        var staleBefore = DateTime.UtcNow - StaleClaim;

        var candidates = await db.AllowedPythonModules.AsNoTracking()
            .Where(m => m.IsActive
                && m.Source == AllowedPythonModule.SourcePip
                && (m.Status == AllowedPythonModule.StatusPending
                    || (m.Status == AllowedPythonModule.StatusInstalling && m.UpdatedAt < staleBefore)))
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.AllowedPythonModuleId)
            .Take(20)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            var claimed = await db.AllowedPythonModules
                .Where(m => m.AllowedPythonModuleId == id
                    && (m.Status == AllowedPythonModule.StatusPending
                        || (m.Status == AllowedPythonModule.StatusInstalling && m.UpdatedAt < staleBefore)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, AllowedPythonModule.StatusInstalling)
                    .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

            if (claimed == 1)
                return await db.AllowedPythonModules.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.AllowedPythonModuleId == id, ct);
        }

        return null;
    }

    private static Task MarkReadyAsync(AppDbContext db, Guid id, string? version, CancellationToken ct) =>
        db.AllowedPythonModules
            .Where(m => m.AllowedPythonModuleId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, AllowedPythonModule.StatusReady)
                .SetProperty(m => m.InstalledVersion, version)
                .SetProperty(m => m.Error, (string?)null)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

    private static Task MarkFailedAsync(AppDbContext db, Guid id, string error, CancellationToken ct) =>
        db.AllowedPythonModules
            .Where(m => m.AllowedPythonModuleId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, AllowedPythonModule.StatusFailed)
                .SetProperty(m => m.Error, error)
                .SetProperty(m => m.UpdatedAt, DateTime.UtcNow), ct);

    private Task<ProcessResult> InstallAsync(string targetDir, string spec, CancellationToken ct)
    {
        var args = new List<string>
        {
            "install", "--target", targetDir,
            // --upgrade so re-approving a pinned version actually replaces what is
            // already in the target dir instead of pip deciding it is satisfied.
            "--upgrade",
            "--break-system-packages", "--no-cache-dir",
            "--no-warn-script-location", "--disable-pip-version-check",
        };
        // Wheels only: never builds or executes an sdist's packaging code.
        if (OnlyBinary) { args.Add("--only-binary"); args.Add(":all:"); }
        // One token, charset-validated at the API layer, and passed via ArgumentList
        // so there is no shell to inject into either way.
        args.Add(spec);

        return RunAsync(PipExecutable, args, env: null, InstallTimeoutSeconds, ct);
    }

    /// <summary>
    /// The top-level module an installed distribution provides, or null when it
    /// cannot be determined.
    /// </summary>
    /// <remarks>
    /// Asked of the installed distribution rather than guessed from the package
    /// name, because the two are unrelated often enough to matter and no rule maps
    /// one to the other: python-dateutil imports as dateutil, beautifulsoup4 as bs4,
    /// Pillow as PIL.
    ///
    /// `top_level.txt` first — the file that exists to answer this — then the record
    /// of installed files, taking the first entry that is a package directory or a
    /// top-level module. A distribution shipping several top-level names returns the
    /// one matching the distribution, else the first; approving one name and getting
    /// another is better than approving nothing, and the verify step below still has
    /// to pass.
    ///
    /// Returning null is not a failure: the caller keeps the name the admin gave and
    /// verifies that instead, which is the behaviour that existed before.
    /// </remarks>
    private async Task<string?> DiscoverImportNameAsync(
        string targetDir, string spec, CancellationToken ct)
    {
        // The distribution name is the spec without extras or a version pin.
        var dist = new string(spec.TakeWhile(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        if (dist.Length == 0) return null;

        const string Probe = """
            import sys
            from importlib import metadata
            dist = sys.argv[1]
            try:
                d = metadata.distribution(dist)
            except Exception:
                print(""); raise SystemExit(0)
            names = []
            top = d.read_text("top_level.txt")
            if top:
                names = [ln.strip() for ln in top.splitlines() if ln.strip()]
            if not names:
                seen = []
                for f in (d.files or []):
                    parts = str(f).split("/")
                    if parts[0].endswith(".dist-info") or parts[0].endswith(".data"):
                        continue
                    if len(parts) > 1 and parts[1] == "__init__.py":
                        seen.append(parts[0])
                    elif len(parts) == 1 and parts[0].endswith(".py"):
                        seen.append(parts[0][:-3])
                names = list(dict.fromkeys(seen))
            if not names:
                print(""); raise SystemExit(0)
            normalised = dist.replace("-", "_").lower()
            for n in names:
                if n.lower() == normalised:
                    print(n); raise SystemExit(0)
            print(names[0])
            """;

        var env = new Dictionary<string, string> { ["PYTHONPATH"] = targetDir };
        var result = await RunAsync(
            PythonExecutable, ["-c", Probe, dist], env, timeoutSeconds: 30, ct);
        if (result.Exit != 0) return null;

        var name = result.Stdout.Trim();
        return ImportNameRegex().IsMatch(name) ? name : null;
    }

    /// <summary>
    /// Points the row at the name that actually imports. Returns false when another
    /// active row already owns it.
    /// </summary>
    /// <remarks>
    /// The row's identity is its id, not its name, so this is an ordinary update —
    /// and it happens while the row is still `pending`, so no snippet can have seen
    /// the interim name: the allowlist only offers `ready` rows.
    /// </remarks>
    private static async Task<bool> RenameModuleAsync(
        AppDbContext db, AllowedPythonModule module, string importName, CancellationToken ct)
    {
        var taken = await db.AllowedPythonModules.AnyAsync(
            m => m.Module == importName && m.AllowedPythonModuleId != module.AllowedPythonModuleId, ct);
        if (taken) return false;

        var row = await db.AllowedPythonModules.FirstOrDefaultAsync(
            m => m.AllowedPythonModuleId == module.AllowedPythonModuleId, ct);
        if (row is null) return false;

        row.Module = importName;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        module.Module = importName;
        return true;
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ImportNameRegex();

    private Task<ProcessResult> VerifyImportAsync(string targetDir, string module, CancellationToken ct)
    {
        // PYTHONPATH rather than -I here: this probe wants to see the target dir, and
        // it runs our own one-line script, not the user's. The snippet sandbox is the
        // one that must stay isolated, and it adds the same dir explicitly instead.
        var env = new Dictionary<string, string> { ["PYTHONPATH"] = targetDir };
        var script = $"import {module} as _m; print(getattr(_m, '__version__', ''))";
        return RunAsync(PythonExecutable, ["-c", script], env, timeoutSeconds: 30, ct);
    }

    private sealed record ProcessResult(int Exit, string Stdout, string Stderr);

    private static async Task<ProcessResult> RunAsync(
        string file, IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? env,
        int timeoutSeconds, CancellationToken ct)
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
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new ProcessResult(-1, string.Empty, $"timed out after {timeoutSeconds}s");
        }

        return new ProcessResult(proc.ExitCode, await stdout, await stderr);
    }

    // pip's failure output is long and the useful part is at the end.
    private static string Tail(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }
}
