using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.Knowledge;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

public sealed class GetKnowledgeArticleHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "id":{"type":"string","description":"Article id (uuid)"},
          "slug":{"type":"string","description":"Article slug (alternative to id)"}
        },"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public GetKnowledgeArticleHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "get_knowledge_article";
    public string Description => "Fetches the full text of a knowledge-base article by id or slug.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var idStr = Str(args, "id");
        var slug = Str(args, "slug");
        if (string.IsNullOrWhiteSpace(idStr) && string.IsNullOrWhiteSpace(slug))
            return Err("provide id or slug");

        var q = _db.KnowledgeArticles.AsNoTracking().Where(a => a.IsActive);
        if (!string.IsNullOrWhiteSpace(idStr))
        {
            if (!Guid.TryParse(idStr, out var id)) return Err("id must be a uuid");
            q = q.Where(a => a.KnowledgeArticleId == id);
        }
        else
        {
            q = q.Where(a => a.Slug == slug);
        }

        var a = await q.FirstOrDefaultAsync(ct);
        if (a is null) return Err("knowledge article not found");

        return JsonSerializer.SerializeToElement(new KnowledgeArticleResponse
        {
            KnowledgeArticleId = a.KnowledgeArticleId,
            Title = a.Title,
            Slug = a.Slug,
            Content = a.Content,
            Tags = a.Tags,
            CreatedAt = a.CreatedAt,
            UpdatedAt = a.UpdatedAt,
        });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static JsonElement Err(string m) => JsonSerializer.SerializeToElement(new { error = m });
}
