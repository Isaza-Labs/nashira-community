using System.Text.Json;
using nashira_backend.Controllers;
using nashira_backend.Data.DTos.Knowledge;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

public sealed class CreateKnowledgeArticleHandler : IToolHandler
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "title":{"type":"string","description":"Article title"},
          "content":{"type":"string","description":"Article body (markdown/plain text)"},
          "tags":{"type":"array","items":{"type":"string"},"description":"Optional tags"}
        },"required":["title"],"additionalProperties":false}
        """).RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public CreateKnowledgeArticleHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "create_knowledge_article";
    public string Description =>
        "Creates a knowledge-base article (runbook/note) so it can be searched later. " +
        "Returns the new article's id and slug.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var title = Str(args, "title");
        if (string.IsNullOrWhiteSpace(title))
            return JsonSerializer.SerializeToElement(new { error = "title is required" });

        var row = await KnowledgeController.CreateAsync(
            _db, title, Str(args, "content"), ReadTags(args), ct);

        return JsonSerializer.SerializeToElement(new
        {
            knowledge_article_id = row.KnowledgeArticleId,
            title = row.Title,
            slug = row.Slug,
        });
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
}
