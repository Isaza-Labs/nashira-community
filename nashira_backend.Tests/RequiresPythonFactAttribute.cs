namespace nashira_backend.Tests;

// A test that needs a real Python interpreter. Without one it is reported as skipped, with the
// reason, instead of returning early and counting as a pass for code it never ran.
// `RequiresUnix` is for tests that also rely on a Unix executable such as /usr/bin/env.
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresPythonFactAttribute : FactAttribute
{
    public RequiresPythonFactAttribute(bool requiresUnix = false)
    {
        if (PythonSandboxTests.Exe is null)
            Skip = "No Python interpreter (python3 or python) was found on this host.";
        else if (requiresUnix && OperatingSystem.IsWindows())
            Skip = "This test needs a Unix executable (/usr/bin/env) and is not run on Windows.";
    }
}
