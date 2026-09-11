using Microsoft.EntityFrameworkCore;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Secrets;
using nashira_backend.Services.Ai.Specs;

namespace nashira_backend.Services.Ai.Seed;

// Imports the shipped OpenAPI documents (Specs/*.yaml) into ai_api_specs at boot.
//
// The runtime index only ever reads the table, so without this step a fresh install
// starts with an empty catalog and the agent's discover/detail/execute tools have
// nothing to work with. The files are the seed; the table is the source of truth.
//
// Idempotent per `api` name, not per table: a spec whose api is already present is
// never re-inserted, and a spec deleted on purpose stays deleted (the row is
// soft-deleted, so the name is still taken).
//
// Existing rows are not left frozen, though, and that distinction took a release to
// learn. These documents describe THIS build's own endpoints, so a row seeded before an
// endpoint existed goes on advertising an API the process no longer has — na_workflows
// missed /bundle and /import, and na_admin_readonly spent a release without the audit
// query parameters its endpoint had gained. For the agent, a parameter missing from the
// spec and a parameter missing from the API are the same thing.
//
// So a shipped row whose content is still exactly what we last wrote is refreshed from
// the file, and one an admin has edited in place is not — see ShippedContentHash. A
// redeploy still never quietly reverts someone's edit; it only replaces a cache of our
// own documentation that nobody has touched.
//
// These documents describe Nashira's own API, so the rows deliberately carry no base
// URL: the executor resolves it from Ai:SelfBaseUrl per call, which keeps a deployment
// move from requiring a re-seed. What they do carry is the bearer reference, and it is
// the *caller's* session token rather than a stored service credential — the agent
// calls the API as the user it is talking to, so every per-user permission check
// applies to it unchanged.
public static class BuiltinSpecSeeder
{
    // Resolved per call by SecretResolver, so no token ever lands in a spec row. The
    // key is "value" because that is what the executor's bearer branch reads; the
    // previous "token" was silently never found, and the request went out anonymous.
    private const string SelfAuthConfig = $$"""{"value":"{{SecretResolver.SessionJwtRef}}"}""";

    public static async Task SeedAsync(
        IServiceScopeFactory scopes, IWebHostEnvironment env,
        IApiSpecIndex index, ModuleSelection modules, ILogger logger, CancellationToken ct = default)
    {
        var dir = Path.Combine(env.ContentRootPath, "Specs");
        if (!Directory.Exists(dir)) return;

        var shippedFiles = Directory
            .GetFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(dir, "*.yml", SearchOption.TopDirectoryOnly))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // A document describing only capabilities this deployment does not run is not
        // seeded, refreshed or upgraded. Nothing is deleted for it either: a row an
        // all-enabled deployment already wrote stays, and the runtime index is what
        // keeps it out of the agent's catalogue until the capability returns.
        var files = shippedFiles
            .Where(f => ModuleContentCatalog.IsSpecAvailable(ApiName(f), modules))
            .ToList();
        if (files.Count < shippedFiles.Count)
            logger.LogInformation(
                "ai.spec.seed_filtered specs={Seedable} of={Shipped} — the rest describe capabilities "
                + "this deployment does not run", files.Count, shippedFiles.Count);
        if (files.Count == 0) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var shipped = new HashSet<string>(
            files.Select(ApiName),
            StringComparer.OrdinalIgnoreCase);

        // Includes soft-deleted rows on purpose: the api name is uniquely indexed, so
        // re-seeding a deleted spec would throw, and reviving one an admin removed
        // would be worse than not seeding it.
        var taken = await db.AiApiSpecs
            .AsNoTracking()
            .Select(s => s.Api)
            .ToListAsync(ct);
        var existing = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

        var now = DateTime.UtcNow;
        var seeded = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var api = ApiName(file);
            if (existing.Contains(api)) continue;

            try
            {
                var content = await File.ReadAllTextAsync(file, ct);
                // Parse before inserting: a spec that does not parse would sit in the
                // catalog advertising zero operations, which reads as "this API has
                // nothing" rather than "this file is broken".
                var operationCount = YamlSpecIndex.ParseOperations(api, content).Count();

                db.AiApiSpecs.Add(new AiApiSpec
                {
                    AiApiSpecId = Guid.NewGuid(),
                    Api = api,
                    Content = content,
                    OperationCount = operationCount,
                    BaseUrl = null,
                    AuthType = "bearer",
                    AuthConfig = SelfAuthConfig,
                    VerifySsl = true,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                    ShippedContentHash = HashOf(content),
                });
                existing.Add(api);
                seeded++;
            }
            catch (Exception ex)
            {
                // One malformed file must not stop the others from seeding.
                logger.LogWarning(ex, "ai.spec.seed_failed file={File}", Path.GetFileName(file));
            }
        }

        var upgraded = await UpgradeCatalogOnlyRowsAsync(db, shipped, now, logger, ct);
        var refreshed = await RefreshShippedRowsAsync(db, files, now, logger, ct);

        if (seeded == 0 && upgraded == 0 && refreshed == 0) return;

        await db.SaveChangesAsync(ct);
        await index.ReloadAsync(ct);
        logger.LogInformation(
            "ai.spec.seeded specs={Seeded} upgraded={Upgraded} refreshed={Refreshed}",
            seeded, upgraded, refreshed);
    }

    // Brings a shipped spec's row back in line with the file it came from.
    //
    // The predicate is the whole design. A row qualifies only when the content we last
    // wrote is still the content that is there — then replacing it loses nothing,
    // because the row is a cache of a file this repository owns. When the two differ,
    // somebody edited the spec through the API and their version stands; trimming
    // operations out of a built-in spec to save agent context is a real thing to do,
    // and a redeploy silently undoing it would be the same class of bug as never
    // refreshing at all, pointing the other way.
    //
    // A null hash is a row seeded before the column existed. There is no way to tell an
    // untouched one from an edited one, so it is adopted once — the file wins, the hash
    // is stamped, and every later release respects whatever happens to it after that.
    // The log names each row adopted, because that one pass is the only moment this
    // method can overwrite an edit it could not see.
    private static async Task<int> RefreshShippedRowsAsync(
        AppDbContext db, List<string> files, DateTime now, ILogger logger, CancellationToken ct)
    {
        var refreshed = 0;
        var adopted = new List<string>();
        var preserved = new List<string>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var api = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

            // Active rows only. A spec an admin deleted stays deleted — reviving it
            // here would be the seeder overruling a decision, not correcting a cache.
            var row = await db.AiApiSpecs.FirstOrDefaultAsync(r => r.Api == api && r.IsActive, ct);
            if (row is null) continue;

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ai.spec.refresh_read_failed file={File}", Path.GetFileName(file));
                continue;
            }

            var fileHash = HashOf(content);
            if (string.Equals(row.ShippedContentHash, fileHash, StringComparison.Ordinal)) continue;
            if (string.Equals(HashOf(row.Content), fileHash, StringComparison.Ordinal))
            {
                // Same document, no stamp yet: record it so the comparison is cheap next
                // boot, and change nothing else.
                row.ShippedContentHash = fileHash;
                refreshed++;
                continue;
            }

            // A user-authored row could claim a shipped name while that capability was
            // disabled in releases before names were reserved. CreatedBy distinguishes
            // it from a genuinely legacy seed row, which predates the hash but was still
            // written by this process. Never adopt and overwrite the user's document.
            var isLegacy = string.IsNullOrEmpty(row.ShippedContentHash)
                && row.CreatedBy is null;
            var isPristine = !isLegacy
                && row.ShippedContentHash is not null
                && string.Equals(HashOf(row.Content), row.ShippedContentHash, StringComparison.Ordinal);

            if (!isLegacy && !isPristine)
            {
                preserved.Add(api);
                continue;
            }

            int operationCount;
            try
            {
                // Parse before writing, for the same reason the insert does: a row
                // advertising zero operations reads as "this API has nothing" rather
                // than "this file is broken".
                operationCount = YamlSpecIndex.ParseOperations(api, content).Count();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ai.spec.refresh_parse_failed file={File}", Path.GetFileName(file));
                continue;
            }

            row.Content = content;
            row.OperationCount = operationCount;
            row.ShippedContentHash = fileHash;
            row.UpdatedAt = now;
            refreshed++;
            if (isLegacy) adopted.Add(api);
        }

        if (adopted.Count > 0)
            logger.LogInformation(
                "ai.spec.adopted specs={Specs} — these rows predate content tracking and were "
                + "refreshed from the shipped file; later edits to them will be preserved",
                string.Join(", ", adopted));

        if (preserved.Count > 0)
            logger.LogInformation(
                "ai.spec.refresh_skipped specs={Specs} — edited through the API, so the shipped "
                + "file was not applied",
                string.Join(", ", preserved));

        return refreshed;
    }

    // Line endings are normalised out before hashing. The file is read from disk and
    // the row came back from Postgres, and a checkout on Windows can differ from one on
    // the build agent in nothing but CRLF — which would make every boot see an "edit"
    // that is not there and refresh the same rows forever.
    private static string HashOf(string content)
    {
        var normalised = content.Replace("\r\n", "\n").Replace('\r', '\n');
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalised)));
    }

    // The `api` identifier: the filename without its extension, lowercased. It is what
    // the agent passes to discover_operations and what the row is keyed on.
    private static string ApiName(string file) =>
        Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

    // Installs that seeded before the self-call path existed hold thirteen rows with no
    // base URL and auth "none" — discoverable, and refused by execute_operation with
    // "has no base_url configured". The insert above cannot reach them: it is keyed on
    // the api name and they already own theirs.
    //
    // The predicate is deliberately narrow so the seeder's promise not to revert an
    // admin's work still holds. A row qualifies only if it is a spec we ship, is not
    // soft-deleted, and is untouched in every field this writes: no base URL, no auth
    // config, auth type still "none". An admin who has configured any of that keeps it.
    private static async Task<int> UpgradeCatalogOnlyRowsAsync(
        AppDbContext db, HashSet<string> shipped, DateTime now, ILogger logger, CancellationToken ct)
    {
        var candidates = await db.AiApiSpecs
            .Where(s => s.IsActive
                && s.CreatedBy == null
                && (s.BaseUrl == null || s.BaseUrl == "")
                && (s.AuthConfig == null || s.AuthConfig == "")
                && (s.AuthType == null || s.AuthType == "none"))
            .ToListAsync(ct);

        var upgraded = 0;
        foreach (var row in candidates)
        {
            if (!shipped.Contains(row.Api)) continue;
            row.AuthType = "bearer";
            row.AuthConfig = SelfAuthConfig;
            row.UpdatedAt = now;
            upgraded++;
        }

        if (upgraded > 0)
            logger.LogInformation(
                "ai.spec.upgraded_to_executable specs={Upgraded} — built-in specs now authenticate " +
                "self-calls with the caller's own session token", upgraded);

        return upgraded;
    }
}
