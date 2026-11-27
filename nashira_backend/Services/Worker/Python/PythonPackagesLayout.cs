namespace nashira_backend.Services.Worker.Python;

// Where admin-approved pip packages land.
//
// Two components have to agree on this path and they run at different times: the
// provisioner writes into it during a background sweep, and the sandbox adds it to
// `sys.path` when a snippet runs. A constant is the cheapest way to keep an install
// and an import from disagreeing about where a package is.
//
// The `site` sub-directory exists so the configured root can hold other things later
// (a wheel cache, a lock file) without pip's --target output colliding with them.
public static class PythonPackagesLayout
{
    public const string SiteDirName = "site";

    // Matches the volume mount in deploy/docker-compose.yml. Absolute because the
    // provisioner and the sandbox may run with different working directories.
    public const string DefaultPackagesDir = "/app/pyenv";

    public const string ConfigKey = "Python:PackagesDir";

    public static string ResolveRoot(IConfiguration config)
    {
        var root = (config[ConfigKey] ?? string.Empty).Trim();
        return root.Length == 0 ? DefaultPackagesDir : root;
    }

    public static string ResolveSiteDir(IConfiguration config) =>
        Path.Combine(ResolveRoot(config), SiteDirName);

    // ── Python environments (uv) ─────────────────────────────────────────────
    //
    // Under the same volume as the pip site dir, so a build survives a container
    // restart and the uv cache keeps the next rebuild fast.
    //
    //   <root>/envs/<name>            what an execution runs: a symlink, swapped
    //                                 atomically, to the current build
    //   <root>/envs/<name>.<id>       one completed build
    //   <root>/envs/<name>.<id>.tmp   a build in progress — never referenced
    //   <root>/uv-cache               UV_CACHE_DIR when the image does not set it
    //   <root>/python                 UV_PYTHON_INSTALL_DIR for a pinned PythonVersion
    public const string EnvsDirName = "envs";
    public const string UvCacheDirName = "uv-cache";
    public const string UvPythonDirName = "python";

    public static string ResolveEnvsDir(IConfiguration config) =>
        Path.Combine(ResolveRoot(config), EnvsDirName);

    // `name` is a validated slug (PythonEnvironmentRules.NameRegex), never input that
    // could climb out of the envs dir.
    public static string ResolveEnvDir(IConfiguration config, string name) =>
        Path.Combine(ResolveEnvsDir(config), name);

    // Where a venv keeps its executables. Windows only matters for a developer running
    // the backend outside the container.
    public static string BinDir(string envDir) =>
        Path.Combine(envDir, OperatingSystem.IsWindows() ? "Scripts" : "bin");

    public static string Interpreter(string envDir) =>
        Path.Combine(BinDir(envDir), OperatingSystem.IsWindows() ? "python.exe" : "python");

    // The site dir only when something is actually installed there. Null keeps the
    // sandbox harness byte-identical to what it was before packages existed, so a
    // deployment that never approved a pip package cannot be affected by this at all.
    public static string? ResolveSiteDirIfPopulated(IConfiguration config)
    {
        try
        {
            var dir = ResolveSiteDir(config);
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any() ? dir : null;
        }
        catch (Exception)
        {
            // An unreadable packages dir is a deployment problem, but failing the
            // snippet over it would take down every snippet including the ones that
            // import nothing but the standard library.
            return null;
        }
    }
}
