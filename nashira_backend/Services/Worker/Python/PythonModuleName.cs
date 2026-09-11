using System.Text.RegularExpressions;

namespace nashira_backend.Services.Worker.Python;

// The one definition of "a Python module name", shared by the allowlist, the uploaded
// libraries and the sandbox that turns one into a filename.
//
// It exists because the last of those three is a path join: a module called
// `../../appsettings` would be a directory traversal, and a module called `os` would
// shadow the standard library for every snippet in the installation. Validating in one
// place means the API and the thing that writes the file cannot disagree about what is
// acceptable.
public static partial class PythonModuleName
{
    [GeneratedRegex(@"^[a-z_][a-z0-9_]*$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? module) =>
        !string.IsNullOrWhiteSpace(module) && Pattern().IsMatch(module);

    // Names that must never be taken by an upload. Not the whole standard library —
    // sys.path ordering already makes shadowing impossible — but the ones where a
    // silently-ignored file would waste an afternoon, plus the import machinery itself.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "builtins", "sys", "os", "importlib", "imp", "types", "typing", "json", "io",
        "abc", "codecs", "collections", "copy", "enum", "functools", "itertools",
        "operator", "re", "site", "sitecustomize", "usercustomize", "warnings",
        "socket", "subprocess", "threading", "asyncio", "ctypes", "pickle",
    };

    public static bool IsReserved(string module) => Reserved.Contains(module);
}
