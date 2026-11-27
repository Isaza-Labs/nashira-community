using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Worker.Python;

// A ready environment, pinned to one concrete build on disk.
public sealed record ResolvedPythonEnvironment(
    Guid Id,
    string Name,
    // The concrete build directory (<envs>/<name>.<id>), not the swappable symlink:
    // a job started against one build keeps using it even if a rebuild swaps the link
    // underneath it mid-run — it never imports half from one build and half from the next.
    string Directory,
    string Interpreter,
    string BinDir);

// Why an environment cannot be used. The message is written for whoever reads the
// failed step or chat answer: it names the environment and what to do about it.
public sealed class PythonEnvironmentUnavailableException(string message) : Exception(message)
{
    public const string Code = "python_env_unavailable";
}

// Maps an environment id to the interpreter an execution should start.
public interface IPythonEnvironmentResolver
{
    /// <summary>
    /// The ready build of this environment.
    /// </summary>
    /// <exception cref="PythonEnvironmentUnavailableException">
    /// The environment does not exist, was deleted, is not <c>ready</c>, or its build is
    /// missing from disk.
    /// </exception>
    Task<ResolvedPythonEnvironment> ResolveAsync(Guid environmentId, CancellationToken ct);

    /// <summary>
    /// The active, ready environment flagged <c>IsSshFallback</c>, or null when there is
    /// none usable. Never throws for an unusable fallback: a missing fallback just means
    /// the original SSH failure stands.
    /// </summary>
    Task<ResolvedPythonEnvironment?> FindSshFallbackAsync(CancellationToken ct);
}

// Singleton: the SSH runner that consumes it is one, so it opens its own scope per
// lookup rather than holding a DbContext.
public sealed class PythonEnvironmentResolver : IPythonEnvironmentResolver
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<PythonEnvironmentResolver> _logger;

    public PythonEnvironmentResolver(
        IServiceScopeFactory scopes, IConfiguration config, ILogger<PythonEnvironmentResolver> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    public async Task<ResolvedPythonEnvironment> ResolveAsync(Guid environmentId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PythonEnvironments.AsNoTracking()
            .FirstOrDefaultAsync(e => e.PythonEnvironmentId == environmentId, ct);

        if (row is null)
            throw new PythonEnvironmentUnavailableException(
                $"Python environment {environmentId} does not exist — pick another one, or clear the "
                + "selection to use the system Python");
        if (!row.IsActive)
            throw new PythonEnvironmentUnavailableException(
                $"Python environment '{row.Name}' was deleted — pick another one, or clear the selection "
                + "to use the system Python");

        return Locate(row);
    }

    public async Task<ResolvedPythonEnvironment?> FindSshFallbackAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.PythonEnvironments.AsNoTracking()
            .Where(e => e.IsActive && e.IsSshFallback)
            .OrderBy(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        try
        {
            return Locate(row);
        }
        catch (PythonEnvironmentUnavailableException ex)
        {
            _logger.LogWarning("python.env.ssh_fallback_unusable name={Name} reason={Reason}", row.Name, ex.Message);
            return null;
        }
    }

    // Status first, then the disk: a `ready` row whose build vanished (a wiped volume, a
    // restored database) is as unusable as a failed one, and saying which one it is
    // decides what the admin does next.
    private ResolvedPythonEnvironment Locate(PythonEnvironment row)
    {
        switch (row.Status)
        {
            case PythonEnvironment.StatusReady:
                break;
            case PythonEnvironment.StatusFailed:
                throw new PythonEnvironmentUnavailableException(
                    $"Python environment '{row.Name}' failed to build — fix it under Admin → Python "
                    + "environments and rebuild it");
            default:
                throw new PythonEnvironmentUnavailableException(
                    $"Python environment '{row.Name}' is not ready yet (status: {row.Status}) — retry once "
                    + "its build finishes");
        }

        // The name is a validated slug; refuse anything else before it reaches a path.
        if (!PythonEnvironmentRules.IsValidName(row.Name))
            throw new PythonEnvironmentUnavailableException(
                $"Python environment '{row.Name}' has an invalid name");

        var linkDir = PythonPackagesLayout.ResolveEnvDir(_config, row.Name);
        var buildDir = ResolveBuildDir(linkDir);
        var interpreter = PythonPackagesLayout.Interpreter(buildDir);
        if (!System.IO.Directory.Exists(buildDir) || !File.Exists(interpreter))
            throw new PythonEnvironmentUnavailableException(
                $"Python environment '{row.Name}' is marked ready but its build is missing from disk "
                + $"({linkDir}) — rebuild it under Admin → Python environments");

        return new ResolvedPythonEnvironment(
            row.PythonEnvironmentId, row.Name, buildDir, interpreter, PythonPackagesLayout.BinDir(buildDir));
    }

    // The link's final target when <envs>/<name> is the symlink the builder swaps, else
    // the directory itself (a developer machine where the build is a plain directory).
    internal static string ResolveBuildDir(string linkDir)
    {
        try
        {
            var info = new DirectoryInfo(linkDir);
            if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return target.FullName;
        }
        catch (IOException)
        {
            // A dangling or unreadable link: fall through, and the existence check fails.
        }
        return linkDir;
    }
}
