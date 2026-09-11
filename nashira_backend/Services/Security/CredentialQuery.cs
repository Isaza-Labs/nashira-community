using Microsoft.EntityFrameworkCore;
using nashira_backend.Exceptions;
using CredentialEntity = nashira_backend.Data.Models.Credential;

namespace nashira_backend.Services.Security;

// Credential lookup shared by list_credentials and by every tool that consumes a
// credential, so the two cannot disagree about what a name means.
//
// This exists because of a specific failure: list_credentials returned the name of a
// GitHub credential but not its id, github_create_repo required the uuid, and the agent
// had no tool that could bridge the two — it stopped mid-task and asked the user to
// paste a uuid out of the UI. Resolution now accepts either, and an ambiguous name is
// an error rather than a guess: picking the wrong token silently pushes to the wrong
// account.
public static class CredentialQuery
{
    public static IQueryable<CredentialEntity> FilterByType(IQueryable<CredentialEntity> q, string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return q;
        var t = type.Trim().ToLowerInvariant();
        return q.Where(c => c.Type.ToLower() == t);
    }

    public static IQueryable<CredentialEntity> FilterByAuthMethod(IQueryable<CredentialEntity> q, string? method)
    {
        if (string.IsNullOrWhiteSpace(method)) return q;
        var m = method.Trim().ToLowerInvariant();
        return q.Where(c => c.AuthMethod.ToLower() == m);
    }

    // Case-insensitive substring match, for a caller who knows what it is looking for
    // but not how the admin capitalised it.
    public static IQueryable<CredentialEntity> FilterByName(IQueryable<CredentialEntity> q, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return q;
        var n = name.Trim().ToLowerInvariant();
        return q.Where(c => c.Name.ToLower().Contains(n));
    }

    // Resolves a reference the agent can plausibly hold — the uuid, or the name the user
    // said — to a single credential. `active` must already be scoped to IsActive rows.
    public static async Task<CredentialEntity> ResolveAsync(
        IQueryable<CredentialEntity> active, Guid? id, string? name, CancellationToken ct)
    {
        if (id is { } credentialId)
            return await active.FirstOrDefaultAsync(c => c.CredentialId == credentialId, ct)
                ?? throw new NotFoundException("credential not found");

        var wanted = name?.Trim();
        if (string.IsNullOrWhiteSpace(wanted))
            throw new ValidationException(
                "credential_id or credential_name is required — call list_credentials to get one");

        // Exact (case-insensitive) first: "Github credential" must not be shadowed by
        // "Github credential (rotated)" merely because both contain the same substring.
        var lowered = wanted.ToLowerInvariant();
        var exact = await active.Where(c => c.Name.ToLower() == lowered).Take(2).ToListAsync(ct);
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) throw new ValidationException(Ambiguous(wanted, exact));

        var partial = await FilterByName(active, wanted).Take(5).ToListAsync(ct);
        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new NotFoundException(
                $"no credential named '{wanted}' — call list_credentials to see what exists"),
            _ => throw new ValidationException(Ambiguous(wanted, partial)),
        };
    }

    private static string Ambiguous(string name, IEnumerable<CredentialEntity> matches) =>
        $"'{name}' matches more than one credential ({string.Join(", ", matches.Select(c => c.Name))}) — "
        + "pass credential_id instead";
}
