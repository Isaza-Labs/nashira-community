using nashira_backend.Services.Git;

namespace nashira_backend.Tests;

// Security-relevant pure helpers on GitService: URL scheme allow-list (https only)
// and the repo-relative path guard (rejects traversal / absolute fragments).
public class GitServiceHelpersTests
{
    [Theory]
    [InlineData("https://github.com/org/repo.git", true)]
    [InlineData("https://gitlab.internal/team/repo.git", true)]
    [InlineData("http://github.com/org/repo.git", false)]
    [InlineData("git@github.com:org/repo.git", false)]
    [InlineData("ssh://git@host/repo.git", false)]
    [InlineData("git://host/repo.git", false)]
    [InlineData("", false)]
    public void IsAllowedUrl_accepts_https_only(string url, bool expected) =>
        Assert.Equal(expected, GitService.IsAllowedUrl(url));

    [Theory]
    [InlineData("configs/router1.cfg", "configs/router1.cfg")]
    [InlineData("/configs/router1.cfg", "configs/router1.cfg")]
    [InlineData("configs\\router1.cfg", "configs/router1.cfg")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizePath_normalizes_separators(string? input, string expected) =>
        Assert.Equal(expected, GitService.NormalizePath(input));

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("configs/../../secret")]
    [InlineData("./hidden")]
    [InlineData("a/./b")]
    public void NormalizePath_rejects_traversal(string input) =>
        Assert.Equal(string.Empty, GitService.NormalizePath(input));
}
