using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Security;
using CredentialEntity = nashira_backend.Data.Models.Credential;

namespace nashira_backend.Tests;

// The lookup behind list_credentials and the github_* tools.
//
// These exist because of a specific failure: the user said "use the Github credential",
// list_credentials returned its name but not its id, github_create_repo required the
// uuid, and the agent stopped mid-task asking the user to paste one out of the UI. A
// credential the agent can see must be a credential it can name.
//
// The other half is the opposite risk: resolving a name to the wrong token pushes code
// to somebody else's account, so an ambiguous name has to fail loudly.
public class CredentialQueryTests
{
    private static AppDbContext DbWith(params (string Name, string Type, string AuthMethod, bool Active)[] rows)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"credq-{Guid.NewGuid()}").Options);
        foreach (var (name, type, method, active) in rows)
            db.Credentials.Add(new CredentialEntity
            {
                CredentialId = Guid.NewGuid(),
                Name = name,
                Type = type,
                AuthMethod = method,
                IsActive = active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        db.SaveChanges();
        return db;
    }

    private static IQueryable<CredentialEntity> Active(AppDbContext db) =>
        db.Credentials.AsNoTracking().Where(c => c.IsActive);

    [Fact]
    public async Task Resolves_by_id()
    {
        using var db = DbWith(("Github credential", "git_token", "token", true));
        var expected = db.Credentials.Single();

        var found = await CredentialQuery.ResolveAsync(Active(db), expected.CredentialId, null, default);

        Assert.Equal(expected.CredentialId, found.CredentialId);
    }

    [Theory]
    [InlineData("Github credential")] // as stored
    [InlineData("github credential")] // as the user types it
    [InlineData("GITHUB CREDENTIAL")]
    [InlineData("  Github credential  ")]
    [InlineData("github")]            // partial — still unambiguous here
    public async Task Resolves_by_name_however_it_was_typed(string typed)
    {
        using var db = DbWith(("Github credential", "git_token", "token", true));

        var found = await CredentialQuery.ResolveAsync(Active(db), null, typed, default);

        Assert.Equal("Github credential", found.Name);
    }

    // The whole point of trying exact before substring: a rotated copy whose name
    // contains the original must not make the original unreachable.
    [Fact]
    public async Task Exact_match_wins_over_a_longer_name_containing_it()
    {
        using var db = DbWith(
            ("Github credential", "git_token", "token", true),
            ("Github credential (rotated)", "git_token", "token", true));

        var found = await CredentialQuery.ResolveAsync(Active(db), null, "Github credential", default);

        Assert.Equal("Github credential", found.Name);
    }

    [Fact]
    public async Task Ambiguous_partial_name_fails_and_names_the_candidates()
    {
        using var db = DbWith(
            ("Github personal", "git_token", "token", true),
            ("Github org", "git_token", "token", true));

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => CredentialQuery.ResolveAsync(Active(db), null, "github", default));

        Assert.Contains("Github personal", ex.Message);
        Assert.Contains("Github org", ex.Message);
        Assert.Contains("credential_id", ex.Message);
    }

    [Fact]
    public async Task Unknown_name_points_at_list_credentials_rather_than_guessing()
    {
        using var db = DbWith(("Core SSH", "ssh", "key", true));

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CredentialQuery.ResolveAsync(Active(db), null, "gitlab", default));

        Assert.Contains("list_credentials", ex.Message);
    }

    [Fact]
    public async Task Missing_both_references_says_so_instead_of_returning_nothing()
    {
        using var db = DbWith(("Core SSH", "ssh", "key", true));

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => CredentialQuery.ResolveAsync(Active(db), null, "   ", default));

        Assert.Contains("credential_id", ex.Message);
        Assert.Contains("credential_name", ex.Message);
    }

    [Fact]
    public async Task Deactivated_credentials_are_not_resolvable()
    {
        using var db = DbWith(("Github credential", "git_token", "token", false));

        await Assert.ThrowsAsync<NotFoundException>(
            () => CredentialQuery.ResolveAsync(Active(db), null, "Github credential", default));
    }

    [Fact]
    public async Task An_id_that_belongs_to_a_deactivated_row_is_not_found()
    {
        using var db = DbWith(("Github credential", "git_token", "token", false));
        var id = db.Credentials.Single().CredentialId;

        await Assert.ThrowsAsync<NotFoundException>(
            () => CredentialQuery.ResolveAsync(Active(db), id, null, default));
    }

    [Theory]
    [InlineData("git_token", 1)]
    [InlineData("GIT_TOKEN", 1)] // the agent does not know how the admin capitalised it
    [InlineData("ssh", 1)]
    [InlineData("netbox", 0)]
    public void FilterByType_is_case_insensitive(string type, int expected)
    {
        using var db = DbWith(
            ("Github credential", "git_token", "token", true),
            ("Core SSH", "ssh", "key", true));

        Assert.Equal(expected, CredentialQuery.FilterByType(Active(db), type).Count());
    }

    [Fact]
    public void FilterByAuthMethod_isolates_the_token_credentials_the_github_tools_need()
    {
        using var db = DbWith(
            ("Github credential", "git_token", "token", true),
            ("Core SSH", "ssh", "key", true));

        var tokens = CredentialQuery.FilterByAuthMethod(Active(db), "token").ToList();

        Assert.Equal("Github credential", Assert.Single(tokens).Name);
    }

    [Fact]
    public void Blank_filters_are_no_ops_rather_than_matching_nothing()
    {
        using var db = DbWith(
            ("Github credential", "git_token", "token", true),
            ("Core SSH", "ssh", "key", true));

        var q = CredentialQuery.FilterByName(
            CredentialQuery.FilterByAuthMethod(
                CredentialQuery.FilterByType(Active(db), null), "  "), "");

        Assert.Equal(2, q.Count());
    }
}
