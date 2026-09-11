using System.Text.RegularExpressions;

namespace nashira_backend.Services.Worker.Python;

public sealed record ImportScan(IReadOnlyList<string> Modules, IReadOnlyList<string> Rejected);

// Static scan of a snippet's source for the modules it imports.
//
// THIS IS NOT THE SECURITY BOUNDARY, and it is important to be clear about that.
// A static scan cannot see `__import__(chr(111)+"s")` or `getattr(builtins, ...)`,
// and anything claiming otherwise is selling something. The scan exists to give a
// snippet author a fast, readable error at save time — "you imported `os`, which
// is not on the allowlist" — instead of an opaque failure at 3am.
//
// The real boundary is the runner process itself: what the interpreter is allowed
// to reach, which user it runs as, and what the OS permits it. See PythonSandbox
// for exactly how much of that is in place, and what is not.
public static partial class PythonImportScanner
{
    // `import a, b.c as d` / `from a.b import c`, anchored at line start.
    //
    // Anchoring means a commented-out import is NOT reported, which is the common
    // case and worth getting right. It also means an import written at the start of
    // a line inside a triple-quoted string IS reported — a false positive, and the
    // safe direction for a hint to err in.
    [GeneratedRegex(@"^\s*import\s+([^\r\n#]+)", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex ImportRegex();

    [GeneratedRegex(@"^\s*from\s+([\w\.]+)\s+import\s", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex FromRegex();

    // Dynamic import machinery. Its presence means the static scan cannot be
    // trusted for this snippet at all, so it is refused outright rather than
    // scanned and waved through.
    //
    // `importlib` matches on the bare name, not on a call: `import importlib`
    // followed by `importlib.import_module(...)` defeats the scan just as
    // thoroughly as calling `__import__` directly, and the call site may be many
    // lines from the import.
    [GeneratedRegex(@"\b(importlib)\b", RegexOptions.Compiled)]
    private static partial Regex DynamicModuleRegex();

    [GeneratedRegex(@"\b(__import__|eval|exec|compile|globals|locals|getattr|setattr|vars|breakpoint)\s*\(",
        RegexOptions.Compiled)]
    private static partial Regex DynamicRegex();

    public static ImportScan Scan(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return new ImportScan([], []);

        var modules = new SortedSet<string>(StringComparer.Ordinal);
        var rejected = new List<string>();

        foreach (Match m in DynamicRegex().Matches(code))
        {
            var name = m.Groups[1].Value;
            if (!rejected.Contains(name)) rejected.Add(name);
        }

        foreach (Match m in DynamicModuleRegex().Matches(code))
        {
            var name = m.Groups[1].Value;
            if (!rejected.Contains(name)) rejected.Add(name);
        }

        foreach (Match m in ImportRegex().Matches(code))
        {
            // `import a.b as c, d` -> roots a and d.
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                var name = part.Trim().Split(new[] { " as " }, StringSplitOptions.None)[0].Trim();
                var root = Root(name);
                if (root.Length > 0) modules.Add(root);
            }
        }

        foreach (Match m in FromRegex().Matches(code))
        {
            var root = Root(m.Groups[1].Value.Trim());
            if (root.Length > 0) modules.Add(root);
        }

        return new ImportScan(modules.ToList(), rejected);
    }

    // A relative import (`from . import x`) has no root module to check; it also
    // cannot resolve to anything, because a snippet is a single file with no
    // package around it.
    private static string Root(string dotted)
    {
        var trimmed = dotted.TrimStart('.');
        if (trimmed.Length == 0) return string.Empty;
        var dot = trimmed.IndexOf('.');
        return (dot < 0 ? trimmed : trimmed[..dot]).Trim().ToLowerInvariant();
    }
}
