using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Knowledge;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

public sealed class SearchKnowledgeHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "query":{"type":"string","description":"Text to match in title, content, or tags"},
          "limit":{"type":"integer","minimum":1,"maximum":50,"default":10}
        },"required":["query"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public SearchKnowledgeHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "search_knowledge";
    public string Description =>
        "Searches the knowledge base (runbooks, notes, procedures) by keyword and returns " +
        "matching article summaries with a snippet. Use get_knowledge_article for the full text.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            return JsonSerializer.SerializeToElement(new { error = "query is required" });
        var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 50) : 10;

        var pattern = $"%{query}%";
        var tag = query.Trim().ToLowerInvariant();
        var rows = await _db.KnowledgeArticles.AsNoTracking()
            .Where(a => a.IsActive
                && (EF.Functions.ILike(a.Title, pattern) || EF.Functions.ILike(a.Content, pattern) || a.Tags.Contains(tag)))
            .OrderByDescending(a => a.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);

        var results = rows.Select(a => new KnowledgeArticleSummary
        {
            KnowledgeArticleId = a.KnowledgeArticleId,
            Title = a.Title,
            Slug = a.Slug,
            Tags = a.Tags,
            Snippet = a.Content.Length <= 240 ? a.Content : a.Content[..240] + "…",
        });
        return JsonSerializer.SerializeToElement(new { results, count = rows.Count });
    }
}
