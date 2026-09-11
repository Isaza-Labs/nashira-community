using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Soft-deletes a knowledge article by id. Write → single_confirm.
public sealed class DeleteKnowledgeArticleHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "article_id":{"type":"string","description":"Article id from search_knowledge"}
        },"required":["article_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public DeleteKnowledgeArticleHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "delete_knowledge_article";
    public string Description => "Removes a knowledge-base article by id (use search_knowledge to find it).";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "article_id")?.Trim() is not { Length: > 0 } sid || !Guid.TryParse(sid, out var id))
            return Err("article_id (a uuid from search_knowledge) is required");

        var row = await _db.KnowledgeArticles.FirstOrDefaultAsync(
            a => a.KnowledgeArticleId == id && a.IsActive, ct);
        if (row is null) return Err("knowledge article not found");

        row.IsActive = false;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return JsonSerializer.SerializeToElement(new { knowledge_article_id = id, deleted = true });
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
