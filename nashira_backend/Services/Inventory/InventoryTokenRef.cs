using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Inventory;

// Grammar for InventorySource.TokenSecretRef: empty (no auth), the bare name of a
// stored secret, or a full ${secret:<source>:<id|name>:<field>} reference. Raw tokens
// are rejected at write time — they would be persisted unencrypted and echoed back to
// every viewer through the list/get endpoints. Rows written before this rule may still
// hold a literal token; the sync resolves them with a fallback (see NetBoxSyncService)
// and the API masks them, but they cannot be (re)saved.
public static partial class InventoryTokenRef
{
    // What the API returns in place of a stored literal token. Update endpoints treat
    // it as "keep what is stored" so an edit form can echo a response back unchanged.
    public const string Masked = "***";

    [GeneratedRegex(@"^\$\{secret:(?<source>[a-z_]+):(?<idOrName>[^:}]+):(?<field>[^}]+)\}$")]
    private static partial Regex RefRegex();

    // Mirrors SecretsController's name grammar so every valid secret name is accepted.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9_-]{1,62}[a-z0-9])?$")]
    private static partial Regex SecretNameRegex();

    // Normalizes a client-supplied token_secret_ref into the stored form, or throws
    // ValidationException. A bare secret name becomes a full reference so the sync
    // has one format to resolve.
    public static async Task<string?> NormalizeAsync(AppDbContext db, string raw, CancellationToken ct)
    {
        var value = raw.Trim();
        if (value.Length == 0) return null;

        var m = RefRegex().Match(value);
        if (m.Success)
        {
            await EnsureTargetExistsAsync(db, m, ct);
            return value;
        }

        if (SecretNameRegex().IsMatch(value)
            && await db.Secrets.AnyAsync(s => s.IsActive && s.Name == value, ct))
            return $"${{secret:secret:{value}:value}}";

        // Deliberately does not echo the rejected value: it is most likely a pasted
        // raw token, which must not land in logs or error toasts.
        throw new ValidationException(
            "token_secret_ref must name a stored secret or be a ${secret:...} reference; "
            + "raw tokens are not accepted — store the token as a secret first");
    }

    // Catches a typo at save time instead of a confusing NetBox 401 at sync time.
    private static async Task EnsureTargetExistsAsync(AppDbContext db, Match m, CancellationToken ct)
    {
        var source = m.Groups["source"].Value;
        var idOrName = m.Groups["idOrName"].Value;
        var isGuid = Guid.TryParse(idOrName, out var id);
        var exists = source switch
        {
            "secret" => isGuid
                ? await db.Secrets.AnyAsync(s => s.IsActive && s.SecretId == id, ct)
                : await db.Secrets.AnyAsync(s => s.IsActive && s.Name == idOrName, ct),
            "credential" => isGuid
                ? await db.Credentials.AnyAsync(c => c.IsActive && c.CredentialId == id, ct)
                : await db.Credentials.AnyAsync(c => c.IsActive && c.Name == idOrName, ct),
            // integration / ai_provider refs are resolved by SecretResolver at sync
            // time; the sync reports an unresolved reference by name.
            _ => true,
        };
        if (!exists)
            throw new ValidationException(
                $"token_secret_ref references a {source} that does not exist: {idOrName}");
    }

    // ${secret:...} references are inert identifiers and pass through; anything else
    // is (legacy) secret material and is masked.
    public static string? Mask(string? stored) =>
        string.IsNullOrEmpty(stored) || stored.Contains("${secret:", StringComparison.Ordinal)
            ? stored
            : Masked;
}
