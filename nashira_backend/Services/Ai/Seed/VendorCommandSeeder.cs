using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace nashira_backend.Services.Ai.Seed;

// Imports the shipped vendor catalogues (Skills/vendors/*.yaml) into vendor_commands
// at boot.
//
// That table is what GET /api/vendor-commands/resolve reads, and it is the whole
// mechanism behind "one workflow, many vendors": a node asks for the intent
// `show_ip_interfaces` and the device's platform decides whether that is
// `show ip interface brief` or `show interfaces terse`. With the table empty every
// resolve call 404s, and what the agent does instead is hardcode one vendor's syntax
// into the workflow — which is the failure the catalogue exists to prevent. The files
// are the seed; the table stays the source of truth.
//
// Idempotent per (intent, platform), checked against ALL rows including soft-deleted
// ones. Two reasons, and the second is not optional: the unique index on
// (Intent, Platform) is unfiltered, so inserting over a soft-deleted row throws; and
// an operator who removed `save_config` for a platform decided something, which a
// redeploy must not quietly reverse.
public static class VendorCommandSeeder
{
    // The YAML carries no vendor family: `deploy/python/nashira_ssh_parsers.py` already
    // maps device_type to family for the runner and the platform picker, and a second
    // copy here would only drift from it.
    private sealed class YamlCatalog
    {
        public string Platform { get; set; } = string.Empty;
        public string? Description { get; set; }
        public List<YamlCommand> Commands { get; set; } = [];
    }

    private sealed class YamlCommand
    {
        public string Intent { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string? Description { get; set; }

        // Defaults to true: the catalogue is overwhelmingly show commands, and a
        // missing flag on one that mutates would let the idempotency tier treat it
        // as a read.
        public bool? ReadOnly { get; set; }

        public string? ParserTemplate { get; set; }
    }

    public static async Task SeedAsync(
        IServiceScopeFactory scopes, IWebHostEnvironment env, ILogger logger,
        CancellationToken ct = default)
    {
        var dir = Path.Combine(env.ContentRootPath, "Skills", "vendors");
        if (!Directory.Exists(dir))
        {
            logger.LogDebug("vendor_commands.seed.no_directory dir={Dir}", dir);
            return;
        }

        var files = Directory.GetFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            logger.LogDebug("vendor_commands.seed.no_files dir={Dir}", dir);
            return;
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var catalogs = new List<YamlCatalog>(files.Count);
        foreach (var file in files)
        {
            try
            {
                var parsed = deserializer.Deserialize<YamlCatalog>(await File.ReadAllTextAsync(file, ct));
                if (parsed is null
                    || string.IsNullOrWhiteSpace(parsed.Platform)
                    || parsed.Commands.Count == 0)
                {
                    logger.LogWarning(
                        "vendor_commands.seed.skipped file={File} reason=empty_or_invalid",
                        Path.GetFileName(file));
                    continue;
                }
                catalogs.Add(parsed);
            }
            catch (Exception ex)
            {
                // One malformed file must not cost every other platform its catalogue.
                logger.LogError(ex, "vendor_commands.seed.parse_failed file={File}", Path.GetFileName(file));
            }
        }

        if (catalogs.Count == 0) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.VendorCommands.AsNoTracking()
            .Select(c => new { c.Intent, c.Platform })
            .ToListAsync(ct);
        var taken = existing
            .Select(r => (r.Intent, r.Platform))
            .ToHashSet();

        var now = DateTime.UtcNow;
        var seeded = 0;
        var skipped = 0;
        foreach (var catalog in catalogs)
        {
            var platform = catalog.Platform.Trim().ToLowerInvariant();

            foreach (var entry in catalog.Commands)
            {
                if (string.IsNullOrWhiteSpace(entry.Intent) || string.IsNullOrWhiteSpace(entry.Command))
                {
                    logger.LogWarning(
                        "vendor_commands.seed.entry_skipped platform={Platform} reason=missing_intent_or_command",
                        platform);
                    skipped++;
                    continue;
                }

                var intent = entry.Intent.Trim().ToLowerInvariant();
                // Covers both a row already in the table and the same intent listed
                // twice for one platform — the unique index rejects either.
                if (!taken.Add((intent, platform)))
                {
                    skipped++;
                    continue;
                }

                db.VendorCommands.Add(new VendorCommand
                {
                    VendorCommandId = Guid.NewGuid(),
                    Intent = intent,
                    Platform = platform,
                    Command = entry.Command.Trim(),
                    Description = string.IsNullOrWhiteSpace(entry.Description)
                        ? null
                        : entry.Description.Trim(),
                    ReadOnly = entry.ReadOnly ?? true,
                    ParserTemplate = string.IsNullOrWhiteSpace(entry.ParserTemplate)
                        ? null
                        : entry.ParserTemplate.Trim(),
                    CreatedBy = null,
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                seeded++;
            }
        }

        if (seeded > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "vendor_commands.seed.ok files={Files} seeded={Seeded} skipped={Skipped}",
                files.Count, seeded, skipped);
        }
        else
        {
            logger.LogDebug(
                "vendor_commands.seed.noop files={Files} skipped={Skipped}", files.Count, skipped);
        }
    }
}
