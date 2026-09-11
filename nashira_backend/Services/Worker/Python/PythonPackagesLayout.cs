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
