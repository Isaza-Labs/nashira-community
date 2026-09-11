using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Configuration.Modules;
using nashira_backend.Data.Db;
using nashira_backend.Services.Ai.Seed;

namespace nashira_backend.Services.Ai.Skills;

// Concatenates the built-in Skills/*.md files (base.md first) with the tenant's
// GLOBAL AiPromptSkill rows (IntegrationId null, ordered by priority), substitutes
// {tool_list} / {current_date}, and caches the result. The cache key is the files'
// max mtime plus a version counter bumped on skill changes, so uploads hot-reload.
//
// Rows tied to an integration are not here: ScopedSkillCatalog indexes them and the
// runner loads their text only when that integration is in play. Concatenating them
// all meant every integration's rules rode in every turn about anything else.
//
// Shipped skills of a disabled capability are left out of both the prompt and the
// listing (ModuleContentCatalog): the agent is told what it can do, and a page of rules
// about devices it cannot reach is an invitation to try. Rows an operator wrote are
// never filtered — those are theirs, and nothing here classifies them.
public sealed class SkillPromptLoader : ISkillPromptLoader
{
    private const string Fallback =
        "You are Nashira, a network automation assistant operated by natural language. " +
        "Answer concisely and act via the available tools when they help. Keep IDs verbatim.\n\n" +
        "Tools:\n{tool_list}\n\nToday: {current_date}";

    // Subdirectory of Skills/ that holds seed data rather than prompt text.
    private const string DataDir = "vendors";

    private static long _version;

    private readonly string _skillsDir;
    private readonly IServiceScopeFactory _scopes;
    private readonly ModuleSelection _modules;
    private readonly ILogger<SkillPromptLoader> _logger;
    private CacheEntry? _cache;

    private sealed record CacheEntry(long Version, DateTime FileStamp, string Text);

    public SkillPromptLoader(
        IWebHostEnvironment env, IServiceScopeFactory scopes, ModuleSelection modules,
        ILogger<SkillPromptLoader> logger)
    {
        _skillsDir = Path.Combine(env.ContentRootPath, "Skills");
        _scopes = scopes;
        _modules = modules;
        _logger = logger;
    }

    public async Task<string> LoadAsync(string toolList, CancellationToken ct)
    {
        var raw = await LoadRawAsync(ct);
        return raw
            .Replace("{tool_list}", toolList)
            .Replace("{current_date}", DateTime.UtcNow.ToString("yyyy-MM-dd"));
    }

    public void Invalidate() => Interlocked.Increment(ref _version);

    public async Task<IReadOnlyList<BuiltinSkill>> ListBuiltinsAsync(CancellationToken ct)
    {
        var (files, _) = FileSkills();
        var result = new List<BuiltinSkill>(files.Count);
        foreach (var f in files)
        {
            try
            {
                result.Add(new BuiltinSkill(RelativeName(f), await File.ReadAllTextAsync(f, ct)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ai.skills.file_read_failed file={File}", f);
            }
        }
        return result;
    }

    public async Task<bool> SaveBuiltinAsync(string name, string content, CancellationToken ct)
    {
        if (!ModuleContentCatalog.IsSkillAvailable(name, _modules)) return false;
        var full = ResolveBuiltin(name);
        if (full is null) return false;
        await File.WriteAllTextAsync(full, content, ct);
        _logger.LogInformation("ai.skills.builtin_saved file={File} bytes={Bytes}", name, content.Length);
        return true;
    }

    // Maps a builtin name (relative path, forward slashes) back to a real file.
    // Null unless it lands INSIDE the Skills dir, ends in .md, and already exists —
    // so the API can only overwrite shipped skills, never create or touch anything else.
    private string? ResolveBuiltin(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var root = Path.GetFullPath(_skillsDir);
            var full = Path.GetFullPath(Path.Combine(root, name));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            if (!full.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
            // Nothing under Skills/vendors is a skill, so nothing there is editable as
            // one — the listing hides it and this keeps the API from reaching it by name.
            if (IsData(full)) return null;
            return File.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<string> LoadRawAsync(CancellationToken ct)
    {
        var (files, fileStamp) = FileSkills();
        var version = Interlocked.Read(ref _version);
        if (_cache is { } cached && cached.Version == version && cached.FileStamp == fileStamp)
            return cached.Text;

        var sb = new StringBuilder();
        foreach (var f in files)
        {
            try
            {
                sb.AppendLine(await File.ReadAllTextAsync(f, ct));
                sb.AppendLine("\n---\n");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ai.skills.file_read_failed file={File}", f);
            }
        }

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dbSkills = await db.AiPromptSkills.AsNoTracking()
                .Where(s => s.IsActive && s.IntegrationId == null)
                .OrderBy(s => s.Priority).ThenBy(s => s.Name)
                .Select(s => s.Content)
                .ToListAsync(ct);
            foreach (var content in dbSkills)
            {
                sb.AppendLine(content);
                sb.AppendLine("\n---\n");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai.skills.db_read_failed");
        }

        var text = sb.Length == 0 ? Fallback : sb.ToString();
        _cache = new CacheEntry(version, fileStamp, text);
        return text;
    }

    private (List<string> Files, DateTime Stamp) FileSkills()
    {
        try
        {
            if (!Directory.Exists(_skillsDir)) return ([], DateTime.MinValue);
            var files = Directory.GetFiles(_skillsDir, "*.md", SearchOption.AllDirectories)
                .Where(f => !IsData(f))
                .Where(f => ModuleContentCatalog.IsSkillAvailable(RelativeName(f), _modules))
                .OrderBy(f => Path.GetFileName(f).Equals("base.md", StringComparison.OrdinalIgnoreCase) ? "" : Path.GetFileName(f))
                .ToList();
            var stamp = files.Count == 0 ? DateTime.MinValue : files.Max(File.GetLastWriteTimeUtc);
            return (files, stamp);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai.skills.enumerate_failed dir={Dir}", _skillsDir);
            return ([], DateTime.MinValue);
        }
    }

    // The name a shipped skill is known by: its path relative to Skills/, forward
    // slashes — the same one ListBuiltinsAsync returns and the catalog is keyed on.
    private string RelativeName(string file) =>
        Path.GetRelativePath(_skillsDir, file).Replace('\\', '/');

    // Skills/vendors holds seed data for VendorCommandSeeder, not prompt text. Its
    // README documents that schema for whoever adds a platform, and the enumeration
    // above is recursive — without this the README would land in every system prompt
    // and show up in Admin → Skills as an editable built-in.
    private bool IsData(string file)
    {
        var relative = Path.GetRelativePath(_skillsDir, file);
        return relative.StartsWith(DataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(DataDir + '/', StringComparison.OrdinalIgnoreCase);
    }
}
