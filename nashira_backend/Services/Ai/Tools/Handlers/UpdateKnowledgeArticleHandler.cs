using System.Text.Json;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Edits an existing knowledge article (find its id with search_knowledge). Write → autonomous.
public sealed class UpdateKnowledgeArticleHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "article_id":{"type":"string","description":"Article id from search_knowledge"},
          "title":{"type":"string"},
          "content":{"type":"string"},
          "tags":{"type":"array","items":{"type":"string"}}
        },"required":["article_id"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public UpdateKnowledgeArticleHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "update_knowledge_article";
    public string Description =>
        "Updates a knowledge-base article by id (use search_knowledge to find it). Only the fields you " +
        "provide change; changing the title regenerates its slug.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        if (Str(args, "article_id")?.Trim() is not { Length: > 0 } sid || !Guid.TryParse(sid, out var id))
            return Err("article_id (a uuid from search_knowledge) is required");

        try
        {
            var row = await KnowledgeController.UpdateAsync(
                _db, id, Str(args, "title"), Str(args, "content"), ReadTags(args), ct);
            return JsonSerializer.SerializeToElement(new
            {
                knowledge_article_id = row.KnowledgeArticleId,
                title = row.Title,
                slug = row.Slug,
                updated = true,
            });
        }
        catch (NotFoundException ex)
        {
            return Err(ex.Message);
        }
    }

    private static List<string>? ReadTags(JsonElement args)
    {
        if (!args.TryGetProperty("tags", out var t) || t.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var el in t.EnumerateArray())
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s) list.Add(s);
        return list;
    }

    private static string? Str(JsonElement a, string k) =>
        a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Err(string message) => JsonSerializer.SerializeToElement(new { error = message });
}
