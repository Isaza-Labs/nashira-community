using nashira_backend.Services.Ai.Seed;
using nashira_backend.Services.Worker.Python;

namespace nashira_backend.Tests;

// The seeded `SSH primitive (paramiko)` body is the only baseline that is CODE rather
// than a declaration, so it is the only one an edit can break while still compiling.
// The scanner matches on TEXT, which makes two failures invisible to a reader:
//
//   - naming the exec-mode helper `_run_exec` — `\bexec\s*\(` matches inside it;
//   - writing `re.compile(...)`, or even a COMMENT that spells that call out, because
//     a comment is text like any other and the scan does not parse.
//
// Both have already happened once. They fail here now instead of on a live device.
public class ParamikoBaselineSnippetTests
{
    [Fact]
    public void The_seeded_body_is_not_refused_by_the_import_scanner()
    {
        var scan = PythonImportScanner.Scan(DefaultSnippetSeeder.ParamikoCode);

        Assert.Empty(scan.Rejected);
    }

    [Fact]
    public void The_seeded_body_declares_exactly_the_modules_an_admin_has_to_approve()
    {
        var scan = PythonImportScanner.Scan(DefaultSnippetSeeder.ParamikoCode);

        // The snippet's description tells the admin to approve `paramiko` (pip, network)
        // and `io` (stdlib); `re` and `time` are already in the seeded baseline. If this
        // list grows, that description is now wrong and the admin will follow it into a
        // module_denied they were told nothing about.
        Assert.Equal(
            new[] { "io", "paramiko", "re", "time" },
            scan.Modules.OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }
}
