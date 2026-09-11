using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Ai.Seed;

// Seeds the shipped vendor/platform guides (KnowledgeSeed/*.md) as knowledge
// articles once, at boot.
//
// These came from netora_agent, where they were three hard-coded tools
// (get_nokia_knowledge / get_ansible_knowledge / get_awx_docs). The .NET port
// replaced those with a generic, data-driven knowledge base — which is a better
// shape, but leaves the content stranded unless it is loaded. This closes that
// gap so `search_knowledge` actually finds the guides.
//
// Idempotent per file: keyed by slug, so a redeploy will not duplicate. Existing
// articles are left untouched — an operator may have edited them on purpose.
public static class KnowledgeSeedService
{
    // Filename stem -> tags. Tags drive search relevance, so they are curated
    // rather than derived from the file name.
    private static readonly Dictionary<string, string[]> TagsByStem = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nokia_srlinux_guide"] = ["nokia", "sr-linux", "srlinux", "vendor", "cli", "configuration"],
        ["ansible_playbook_guide"] = ["ansible", "playbook", "automation", "yaml", "awx"],
        ["awx_guide"] = ["awx", "ansible-tower", "automation", "jobs", "inventory"],
    };

    public static async Task SeedAsync(IServiceScopeFactory scopes, IWebHostEnvironment env, ILogger logger, CancellationToken ct = default)
    {
        var dir = Path.Combine(env.ContentRootPath, "KnowledgeSeed");
        if (!Directory.Exists(dir)) return;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var seeded = 0;
        foreach (var file in Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly).OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var content = await File.ReadAllTextAsync(file, ct);
                var title = FirstHeading(content) ?? Humanize(stem);
                var slug = KnowledgeArticle.Slugify(stem);

                if (await db.KnowledgeArticles.AnyAsync(a => a.Slug == slug, ct)) continue;

                var now = DateTime.UtcNow;
                db.KnowledgeArticles.Add(new KnowledgeArticle
                {
                    KnowledgeArticleId = Guid.NewGuid(),
                    Title = title,
                    Slug = slug,
                    Content = content,
                    Tags = TagsByStem.TryGetValue(stem, out var tags) ? [.. tags] : [],
                    IsActive = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                seeded++;
            }
            catch (Exception ex)
            {
                // One unreadable guide must not stop the app from booting.
                logger.LogWarning(ex, "knowledge.seed.file_failed file={File}", file);
            }
        }

        if (seeded > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("knowledge.seed.done count={Count}", seeded);
        }
    }

    // First markdown H1, used as the article title.
    private static string? FirstHeading(string content)
    {
        foreach (var line in content.Split('\n').Take(20))
        {
            var t = line.Trim();
            if (t.StartsWith("# ", StringComparison.Ordinal))
            {
                var title = t[2..].Trim();
                if (title.Length > 0) return title;
            }
        }
        return null;
    }

    private static string Humanize(string stem) =>
        string.Join(' ', stem.Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
