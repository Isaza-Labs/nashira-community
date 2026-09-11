using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

// The static scan is an authoring hint, not the security boundary — the import
// hook inside the interpreter is. These tests pin the hint's behaviour, and in
// particular that it refuses code it cannot reason about instead of guessing.
public class PythonImportScannerTests
{
    [Fact]
    public void No_code_yields_nothing()
    {
        Assert.Empty(PythonImportScanner.Scan(null).Modules);
        Assert.Empty(PythonImportScanner.Scan("   ").Modules);
    }

    [Theory]
    [InlineData("import json", "json")]
    [InlineData("import  re  ", "re")]
    [InlineData("import json as j", "json")]
    [InlineData("from ipaddress import ip_address", "ipaddress")]
    public void Simple_imports_are_found(string code, string expected)
    {
        Assert.Contains(expected, PythonImportScanner.Scan(code).Modules);
    }

    // An author reasons about "may I use xml?", not about submodule trees.
    [Theory]
    [InlineData("import xml.etree.ElementTree")]
    [InlineData("from xml.etree import ElementTree")]
    public void Submodules_report_their_root(string code)
    {
        var scan = PythonImportScanner.Scan(code);
        Assert.Equal(["xml"], scan.Modules);
    }

    [Fact]
    public void A_comma_separated_import_reports_every_root()
    {
        var scan = PythonImportScanner.Scan("import json, re, xml.dom as d");
        Assert.Contains("json", scan.Modules);
        Assert.Contains("re", scan.Modules);
        Assert.Contains("xml", scan.Modules);
    }

    [Fact]
    public void Module_names_are_normalised_to_lowercase()
    {
        Assert.Contains("json", PythonImportScanner.Scan("import JSON").Modules);
    }

    [Fact]
    public void A_relative_import_has_no_root_to_report()
    {
        // A snippet is a single file with no package around it, so this cannot
        // resolve to anything anyway.
        Assert.Empty(PythonImportScanner.Scan("from . import helpers").Modules);
    }

    // The important behaviour: code whose imports cannot be determined statically
    // is rejected outright rather than scanned and waved through.
    [Theory]
    [InlineData("__import__('os')")]
    [InlineData("import importlib\nimportlib.import_module('os')")]
    [InlineData("eval('__import__(\"os\")')")]
    [InlineData("exec(payload)")]
    [InlineData("getattr(builtins, 'open')")]
    [InlineData("compile(src, '<s>', 'exec')")]
    public void Dynamic_import_machinery_is_rejected(string code)
    {
        Assert.NotEmpty(PythonImportScanner.Scan(code).Rejected);
    }

    [Fact]
    public void Ordinary_code_is_not_rejected()
    {
        var scan = PythonImportScanner.Scan("""
            import json
            result = {"total": sum(x["n"] for x in input["rows"])}
            print(json.dumps(result))
            """);

        Assert.Empty(scan.Rejected);
        Assert.Equal(["json"], scan.Modules);
    }

    // The regex anchors at line start, so a commented-out import is not reported.
    [Fact]
    public void A_commented_import_is_not_reported()
    {
        Assert.DoesNotContain("os", PythonImportScanner.Scan("# import os").Modules);
    }

    // The flip side of anchoring: an import written at the start of a line inside a
    // docstring IS reported. A false positive the author can see and rewrite, which
    // is the safe direction for a hint to err in — the interpreter's import hook is
    // what actually decides.
    [Fact]
    public void An_import_inside_a_docstring_is_a_known_false_positive()
    {
        var scan = PythonImportScanner.Scan("\"\"\"\nimport os\n\"\"\"\nresult = 1");
        Assert.Contains("os", scan.Modules);
    }

    // The call site can be many lines from the import, so the bare name is what
    // matters — not a call shape.
    [Fact]
    public void Importing_importlib_at_all_is_rejected()
    {
        Assert.Contains("importlib", PythonImportScanner.Scan("import importlib").Rejected);
    }

    [Fact]
    public void Duplicates_collapse()
    {
        var scan = PythonImportScanner.Scan("import json\nimport json\nfrom json import loads");
        Assert.Equal(["json"], scan.Modules);
    }
}
