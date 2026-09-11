using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace nashira_backend.Services.Worker.Python;

public sealed record PythonRunRequest(
    string Code,
    JsonElement Input,
    int TimeoutSeconds,
    bool NetworkEnabled,
    IReadOnlyCollection<string> AllowedModules,
    // Where the admin-approved pip packages are installed, or null when none are.
    // Made importable inside the harness rather than through PYTHONPATH, which `-I`
    // ignores by design.
    string? PackagesDir = null);

public sealed record PythonRunResult(
    bool Ok, JsonElement Output, string Stdout, string Stderr, string? ErrorKind, int ExitCode);

public interface IPythonSandbox
{
    Task<PythonRunResult> RunAsync(PythonRunRequest request, CancellationToken ct);
}

// Runs a snippet's Python in a child process.
//
// ── What this actually protects against, and what it does not ───────────
//
// IN PLACE:
//   - A wall-clock deadline; the process tree is killed on expiry.
//   - A SCRUBBED environment. Only PATH and a couple of locale/system variables
//     are passed through, so the connection string, the JWT signing key and any
//     provider tokens the backend holds in its environment are simply not there
//     to be read. This is the single most valuable control here, because it is
//     the one that survives a full interpreter escape.
//   - `python3 -I`: isolated mode, ignoring PYTHONPATH and the user site
//     directory, so a snippet cannot pick up whatever is installed in the
//     invoking user's home.
//   - A per-run temporary working directory, removed afterwards.
//   - Output caps that KILL the child rather than draining it, so a runaway print
//     cannot hold a worker open for the whole deadline.
//   - An import allowlist enforced INSIDE the interpreter by replacing
//     __import__, which catches importlib and dynamic imports the static scan
//     cannot see.
//   - The script is passed on stdin and never written to disk.
//   - An optional `Python:SandboxCommand` wrapper, so an operator can put real OS
//     isolation in front of the interpreter (bwrap, firejail, a container shim).
//
// NOT IN PLACE — an operator must know these:
//   - NO OS-level isolation BY DEFAULT. Unless `Python:SandboxCommand` is set, the
//     child runs as the same user as the backend with the same filesystem and
//     network access. `NetworkEnabled` gates which modules are offered, not what
//     the kernel allows.
//   - NO memory or CPU limit. The deadline bounds wall time only; a busy loop
//     consumes a core until it expires.
//   - The import hook is defence in depth, not a jail. CPython's object graph
//     reaches the real builtins from almost any object, and its own documentation
//     says a reliable in-process sandbox is not achievable.
//
// So the boundary that actually holds is operational. Writing a `python_snippet`
// is close to shell access on the backend host; the admin-only module allowlist
// and the Operator role on snippet authoring are what keep it with people who
// already have that. Set `Python:SandboxCommand` anywhere that is not true.
public sealed class PythonSandbox : IPythonSandbox
{
    private const string DefaultExecutable = "python3";
    private const int MaxOutputChars = 200_000;
    private const int GraceSeconds = 5;

    // Everything else is withheld. PATH is needed to find the interpreter;
    // the rest keep encoding and temp-file handling sane on each platform.
    private static readonly string[] PassThroughEnv =
    [
        "PATH", "SystemRoot", "COMSPEC", "TEMP", "TMP", "TMPDIR",
        "LANG", "LC_ALL", "LC_CTYPE",
    ];

    private readonly string _executable;
    private readonly string? _sandboxCommand;
    private readonly ILogger<PythonSandbox> _logger;
    private bool _warnedNoIsolation;

    public PythonSandbox(IConfiguration config, ILogger<PythonSandbox> logger)
    {
        // Same setting the SSH runner uses; one Python for the image.
        _executable = config["Python:Executable"] ?? DefaultExecutable;
        // `;`-delimited, e.g. "bwrap;--unshare-all;--ro-bind;/usr;/usr;--tmpfs;/tmp;--"
        _sandboxCommand = config["Python:SandboxCommand"];
        _logger = logger;
    }

    private static List<string> SplitCommand(string? command) =>
        string.IsNullOrWhiteSpace(command)
            ? []
            : command.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* best effort; it is a temp dir */ }
    }

    public async Task<PythonRunResult> RunAsync(PythonRunRequest request, CancellationToken ct)
    {
        var harness = BuildHarness(request);

        // Per-run working directory. Without it the child inherits the backend's
        // cwd, so a relative path in a snippet reaches the application directory.
        var workDir = Directory.CreateTempSubdirectory("nashira-py-").FullName;

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };

        // An operator can put real OS isolation in front of the interpreter. The
        // in-interpreter controls cannot be a boundary (see the class comment), so
        // this hook is how a deployment gets one.
        var wrapper = SplitCommand(_sandboxCommand);
        if (wrapper.Count > 0)
        {
            psi.FileName = wrapper[0];
            foreach (var arg in wrapper.Skip(1)) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add(_executable);
        }
        else
        {
            psi.FileName = _executable;
            if (!_warnedNoIsolation)
            {
                _warnedNoIsolation = true;
                _logger.LogWarning(
                    "python.sandbox.no_os_isolation — Python:SandboxCommand is unset, so python_snippet " +
                    "runs with the worker's own filesystem and network access");
            }
        }

        // -I isolated: ignores PYTHONPATH and the user site directory, so a snippet
        // cannot pick up whatever is installed in the invoking user's home.
        // -B: no bytecode written next to anything.
        // -: read the program from stdin, so it never touches disk.
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add("-B");
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add("-");

        // The single most valuable control here, because it is the one that
        // survives a full interpreter escape: the child never sees the connection
        // string, the JWT signing key, or any provider token the backend holds in
        // its environment. Allowlisted rather than denylisted — a new secret added
        // to the deployment must not silently become visible.
        psi.Environment.Clear();
        foreach (var name in PassThroughEnv)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) psi.Environment[name] = value;
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            TryDelete(workDir);
            _logger.LogError(ex, "python.sandbox.unavailable exe={Exe}", psi.FileName);
            return Fail("runtime_unavailable", $"Python runtime not available: {ex.Message}");
        }

        var deadline = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300) + GraceSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        try
        {
            await proc.StandardInput.WriteAsync(harness.AsMemory(), cts.Token);
            proc.StandardInput.Close();

            var stdoutTask = ReadCappedAsync(proc.StandardOutput, proc, cts.Token);
            var stderrTask = ReadCappedAsync(proc.StandardError, proc, cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
                return new PythonRunResult(
                    false, Empty(), stdout, stderr, "script_error", proc.ExitCode);

            // The harness prints one JSON envelope on the last line; everything the
            // snippet printed itself stays in stdout for the operator to read.
            var (payload, logs) = SplitEnvelope(stdout);
            if (payload is null)
                return new PythonRunResult(false, Empty(), logs, stderr, "bad_output", proc.ExitCode);

            if (payload.Value.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return new PythonRunResult(
                    false, payload.Value, logs, err.GetString() ?? string.Empty, "script_error", proc.ExitCode);

            var result = payload.Value.TryGetProperty("result", out var r) ? r.Clone() : Empty();
            return new PythonRunResult(true, result, logs, stderr, null, proc.ExitCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(proc);
            _logger.LogWarning("python.sandbox.timeout deadline={Deadline}s", (int)deadline.TotalSeconds);
            return Fail("timeout", $"the script exceeded {request.TimeoutSeconds}s");
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }
        catch (Exception ex)
        {
            TryKill(proc);
            _logger.LogError(ex, "python.sandbox.failed");
            return Fail("sandbox_error", ex.Message);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // Wraps the snippet so the allowlist is enforced by the interpreter rather than
    // by our regex. Replacing __import__ catches importlib and dynamic imports the
    // static scan cannot see.
    //
    // The snippet's own source is embedded as a string and exec'd, so a syntax
    // error in it surfaces as a script error rather than breaking the harness.
    private static string BuildHarness(PythonRunRequest request)
    {
        var allowed = JsonSerializer.Serialize(request.AllowedModules.Select(m => m.ToLowerInvariant()));
        var code = JsonSerializer.Serialize(request.Code);
        var input = request.Input.ValueKind == JsonValueKind.Undefined ? "{}" : request.Input.GetRawText();
        // `-I` keeps PYTHONPATH and the script directory off sys.path entirely, which
        // is the whole reason the packages directory has to be added here. APPENDED, not
        // prepended: a pip package that happens to be named after a standard-library
        // module is then never reached instead of silently replacing it everywhere.
        var hasPackages = !string.IsNullOrWhiteSpace(request.PackagesDir);
        var packagesPath = JsonSerializer.Serialize(request.PackagesDir ?? string.Empty);

        // Importing a package FROM DISK can make CPython's own import machinery reach
        // for `_io` through builtins.__import__ — on some interpreter versions with no
        // caller globals, which the guard holds to the snippet's rule. It is not a
        // capability grant: `_io` is the C implementation behind `io`, whose `open` is
        // already a builtin every snippet has (the allowlist governs imports, never
        // the filesystem — see the class comment). Added only when a packages
        // directory is actually in play, so a snippet using none behaves as it did.
        var machinery = JsonSerializer.Serialize(hasPackages ? new[] { "_io" } : []);

        return $$"""
            import builtins, json, sys

            _packages = {{packagesPath}}
            if _packages:
                sys.path.append(_packages)

            _allowed = set({{allowed}})
            _machinery = set({{machinery}})
            _real_import = builtins.__import__

            _input = json.loads({{JsonSerializer.Serialize(input)}})
            _source = {{code}}
            _env = {"input": _input, "result": None, "__builtins__": builtins}

            # Enforced only for imports the snippet itself wrote. An allowed module
            # importing its own dependencies is different: `collections` reaching for
            # `heapq`, `datetime` for `time`, a pip package for its requirements —
            # blocking those would make the allowlist secretly require the whole
            # transitive closure of every entry, varying by interpreter version with
            # whatever the harness happened to cache in sys.modules first. The admin
            # vetted the module; how it is implemented is not a new capability the
            # snippet asked for.
            #
            # WHO is importing is read from the calling frame, not from the `globals`
            # argument. That argument is what the caller chose to pass, and library
            # code routinely passes nothing: `from dateutil import parser` reached
            # __import__ with globals=None from inside the package, was therefore
            # judged as snippet code, and failed with
            #   ImportError: module 'builtins' is not on this deployment's allowlist
            # — naming a module the author never wrote, cannot allowlist meaningfully,
            # and which was already present. Every pip package with a lazy or
            # indirect import hit this, which is why the allowlist looked broken for
            # exactly the packages that needed installing.
            #
            # The frame cannot be spoofed by omitting an argument: snippet code runs
            # with _env as its frame globals whether it writes `import x` or calls
            # `__import__("x")` directly, so both are still checked.
            def _guarded_import(name, globals=None, locals=None, fromlist=(), level=0):
                try:
                    caller = sys._getframe(1).f_globals
                except ValueError:
                    caller = globals          # no caller frame: fall back, still checked
                if caller is _env:
                    root = name.split('.')[0].lower()
                    if root not in _allowed and root not in _machinery:
                        raise ImportError(
                            "module '%s' is not on this deployment's allowlist" % root)
                return _real_import(name, globals, locals, fromlist, level)

            builtins.__import__ = _guarded_import

            _envelope = {}
            try:
                exec(compile(_source, "<snippet>", "exec"), _env)
                _envelope["result"] = _env.get("result")
            except BaseException as e:
                _envelope["error"] = "%s: %s" % (type(e).__name__, e)

            sys.stdout.flush()
            # Sentinel-prefixed so the snippet's own prints stay distinguishable
            # from the envelope, whatever it printed.
            sys.stdout.write("\n__NASHIRA_RESULT__" + json.dumps(_envelope, default=str))
            """;
    }

    private const string Sentinel = "__NASHIRA_RESULT__";

    private static (JsonElement? Payload, string Logs) SplitEnvelope(string stdout)
    {
        var idx = stdout.LastIndexOf(Sentinel, StringComparison.Ordinal);
        if (idx < 0) return (null, stdout);

        var logs = stdout[..idx].TrimEnd('\n', '\r');
        var json = stdout[(idx + Sentinel.Length)..];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return (doc.RootElement.Clone(), logs);
        }
        catch (JsonException)
        {
            return (null, stdout);
        }
    }

    // Caps as it reads rather than after: a script printing in a loop would
    // otherwise materialise the whole thing in memory before the cap applied.
    private static async Task<string> ReadCappedAsync(
        StreamReader reader, Process proc, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            var remaining = MaxOutputChars - sb.Length;
            if (remaining > 0) sb.Append(buffer, 0, Math.Min(read, remaining));

            if (sb.Length >= MaxOutputChars)
            {
                // Stop the child rather than draining it. A script printing in a
                // loop would otherwise hold a worker for the whole deadline
                // producing output nobody will read.
                TryKill(proc);
                break;
            }
        }
        if (sb.Length >= MaxOutputChars) sb.Append("\n…(truncated)");
        return sb.ToString();
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception) { /* already gone */ }
    }

    private static PythonRunResult Fail(string kind, string message) =>
        new(false, Empty(), string.Empty, message, kind, -1);

    private static JsonElement Empty() => JsonDocument.Parse("{}").RootElement.Clone();
}
