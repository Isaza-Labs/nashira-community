using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Net;
using nashira_backend.Services.Security;
using CredentialEntity = nashira_backend.Data.Models.Credential;

namespace nashira_backend.Services.Git;

// Thin GitHub REST v3 client. Deliberately small: only the three operations that
// cannot be done against a local checkout.
//
// The API base is configurable (GitHub:ApiBaseUrl) so GitHub Enterprise works,
// and every resolved URL goes through IUrlGuard — a misconfigured base must not
// become an SSRF pivot into the internal network.
public sealed class GitHubService : IGitHubService
{
    private const string DefaultApiBase = "https://api.github.com";

    private readonly AppDbContext _db;
    private readonly ISecretProtector _crypto;
    private readonly IUrlGuard _urlGuard;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GitHubService> _logger;
    private readonly string _apiBase;

    public GitHubService(
        AppDbContext db, ISecretProtector crypto, IUrlGuard urlGuard,
        IHttpClientFactory httpFactory, IConfiguration config, ILogger<GitHubService> logger)
    {
        _db = db;
        _crypto = crypto;
        _urlGuard = urlGuard;
        _httpFactory = httpFactory;
        _logger = logger;
        _apiBase = (config["GitHub:ApiBaseUrl"] ?? DefaultApiBase).TrimEnd('/');
    }

    public async Task<Guid> ResolveCredentialAsync(Guid? credentialId, string? credentialName, CancellationToken ct)
    {
        var cred = await CredentialQuery.ResolveAsync(ActiveCredentials(), credentialId, credentialName, ct);
        return cred.CredentialId;
    }

    public async Task<GitHubRepoResult> CreateRepoAsync(GitHubCreateRepoRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ValidationException("name is required");

        // Org repos go to /orgs/{org}/repos; personal ones to /user/repos.
        var path = string.IsNullOrWhiteSpace(req.Organization)
            ? "/user/repos"
            : $"/orgs/{Uri.EscapeDataString(req.Organization.Trim())}/repos";

        var body = new
        {
            name = req.Name.Trim(),
            description = req.Description ?? string.Empty,
            @private = req.Private,
            auto_init = req.AutoInit,
        };

        using var doc = await SendAsync(HttpMethod.Post, path, body, req.CredentialId, ct);
        var root = doc.RootElement;
        return new GitHubRepoResult
        {
            FullName = Str(root, "full_name"),
            HtmlUrl = Str(root, "html_url"),
            CloneUrl = Str(root, "clone_url"),
            Private = root.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
            DefaultBranch = Str(root, "default_branch"),
        };
    }

    public async Task<GitHubPrResult> CreatePullRequestAsync(GitHubCreatePrRequest req, CancellationToken ct)
    {
        RequireRepo(req.Owner, req.Repo);
        if (string.IsNullOrWhiteSpace(req.Title)) throw new ValidationException("title is required");
        if (string.IsNullOrWhiteSpace(req.Head)) throw new ValidationException("head is required");
        if (string.IsNullOrWhiteSpace(req.Base)) throw new ValidationException("base is required");

        var body = new
        {
            title = req.Title.Trim(),
            head = req.Head.Trim(),
            @base = req.Base.Trim(),
            body = req.Body ?? string.Empty,
            draft = req.Draft,
        };

        using var doc = await SendAsync(HttpMethod.Post, RepoPath(req.Owner, req.Repo, "/pulls"), body, req.CredentialId, ct);
        var root = doc.RootElement;
        return new GitHubPrResult
        {
            Number = root.TryGetProperty("number", out var n) && n.TryGetInt32(out var i) ? i : 0,
            HtmlUrl = Str(root, "html_url"),
            State = Str(root, "state"),
            Draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
        };
    }

    public async Task<GitHubMergeResult> MergePullRequestAsync(GitHubMergePrRequest req, CancellationToken ct)
    {
        RequireRepo(req.Owner, req.Repo);
        if (req.Number <= 0) throw new ValidationException("number must be a positive pull-request number");

        var method = (req.Method ?? "merge").Trim().ToLowerInvariant();
        if (method is not ("merge" or "squash" or "rebase"))
            throw new ValidationException("method must be 'merge', 'squash' or 'rebase'");

        var body = new { merge_method = method, commit_title = req.CommitTitle };

        using var doc = await SendAsync(
            HttpMethod.Put, RepoPath(req.Owner, req.Repo, $"/pulls/{req.Number}/merge"), body, req.CredentialId, ct);
        var root = doc.RootElement;
        return new GitHubMergeResult
        {
            Merged = root.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True,
            Sha = root.TryGetProperty("sha", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null,
            Message = Str(root, "message"),
        };
    }

    // ─── internals ──────────────────────────────────────────────────

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, object body, Guid credentialId, CancellationToken ct)
    {
        var token = await ResolveTokenAsync(credentialId, ct);
        var url = _apiBase + path;
        _urlGuard.EnsureSafe(url);

        var client = _httpFactory.CreateClient("rest_call");
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        // GitHub rejects requests without a User-Agent.
        request.Headers.UserAgent.ParseAdd("nashira");

        using var response = await client.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Surface GitHub's own message; it is far more actionable than the status
            // code alone ("Reference already exists", "Validation Failed", …).
            var detail = TryReadMessage(payload) ?? response.ReasonPhrase ?? "request failed";
            _logger.LogWarning("github.request.failed method={Method} path={Path} status={Status}",
                method.Method, path, (int)response.StatusCode);
            throw new ValidationException($"GitHub API {(int)response.StatusCode}: {detail}");
        }

        return string.IsNullOrWhiteSpace(payload)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(payload);
    }

    private IQueryable<CredentialEntity> ActiveCredentials() =>
        _db.Credentials.AsNoTracking().Where(c => c.IsActive);

    private async Task<string> ResolveTokenAsync(Guid credentialId, CancellationToken ct)
    {
        var cred = await CredentialQuery.ResolveAsync(ActiveCredentials(), credentialId, null, ct);

        // Accept the dedicated token field first; fall back to the password column
        // for rows created before auth_method 'token' existed.
        var token = _crypto.Decrypt(cred.EncryptedToken) ?? _crypto.Decrypt(cred.EncryptedPassword);
        if (string.IsNullOrWhiteSpace(token))
            throw new ValidationException(
                $"credential '{cred.Name}' has no token — use a credential with auth_method 'token'");
        return token;
    }

    private static void RequireRepo(string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ValidationException("owner is required");
        if (string.IsNullOrWhiteSpace(repo)) throw new ValidationException("repo is required");
    }

    private static string RepoPath(string owner, string repo, string suffix) =>
        $"/repos/{Uri.EscapeDataString(owner.Trim())}/{Uri.EscapeDataString(repo.Trim())}{suffix}";

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string? TryReadMessage(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
