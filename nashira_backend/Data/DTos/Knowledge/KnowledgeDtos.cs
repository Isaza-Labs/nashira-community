using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos.Knowledge;

public class CreateKnowledgeArticle
{
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
}

public class UpdateKnowledgeArticle
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
}

public class KnowledgeArticleResponse
{
    [JsonPropertyName("knowledge_article_id")] public Guid KnowledgeArticleId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

public class KnowledgeArticleSummary
{
    [JsonPropertyName("knowledge_article_id")] public Guid KnowledgeArticleId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonPropertyName("snippet")] public string Snippet { get; set; } = string.Empty;
}
