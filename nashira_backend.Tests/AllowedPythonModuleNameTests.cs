using nashira_backend.Controllers;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;

namespace nashira_backend.Tests;

// `module` is what a snippet writes after `import`; the pip package is often named
// something else — python-dateutil imports as dateutil, beautifulsoup4 as bs4,
// Pillow as PIL, and no rule maps one to the other.
//
// Asking an admin to know that was the bug: they chose source `pip`, typed the
// package name they had, and were refused with a rule restated at them. A pip row
// now takes the package name and the provisioner asks the installed distribution
// what it provides.
public class AllowedPythonModuleNameTests
{
    private static string? Reject(string module, string source)
    {
        try
        {
            AllowedPythonModuleController.ValidateModuleNameForTests(module, source);
            return null;
        }
        catch (ValidationException ex)
        {
            return ex.Message;
        }
    }

    [Theory]
    [InlineData("python-dateutil")]
    [InlineData("beautifulsoup4")]
    [InlineData("pillow")]
    [InlineData("ruamel.yaml")]
    [InlineData("zope.interface")]
    public void A_pip_row_accepts_the_package_name(string pkg)
    {
        Assert.Null(Reject(pkg, AllowedPythonModule.SourcePip));
    }

    [Theory]
    [InlineData("dateutil")]
    [InlineData("bs4")]
    [InlineData("numpy")]
    public void A_pip_row_still_accepts_an_import_name(string module)
    {
        Assert.Null(Reject(module, AllowedPythonModule.SourcePip));
    }

    [Fact]
    public void A_stdlib_row_still_requires_the_import_name()
    {
        // Nothing installs a stdlib entry, so there is no distribution to ask and
        // the name has to be the one an import statement resolves to.
        var message = Reject("python-dateutil", AllowedPythonModule.SourceStdlib);

        Assert.NotNull(message);
        Assert.Contains("pip", message);
    }

    [Fact]
    public void A_submodule_is_refused_and_told_to_approve_its_root()
    {
        // Approval is granted at the root: `urllib.request` would approve something
        // no import statement resolves to.
        var message = Reject("urllib.request", AllowedPythonModule.SourceStdlib);

        Assert.NotNull(message);
        Assert.Contains("urllib", message);
    }

    [Fact]
    public void The_name_is_lowercased_so_Pillow_and_pillow_are_one_row()
    {
        Assert.Equal("pillow",
            AllowedPythonModuleController.ValidateModuleNameForTests("Pillow", AllowedPythonModule.SourcePip));
    }

    [Fact]
    public void An_empty_name_is_still_required()
    {
        Assert.NotNull(Reject("  ", AllowedPythonModule.SourcePip));
    }
}
