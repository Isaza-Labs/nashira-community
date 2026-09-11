using System.Text;

namespace nashira_backend.Data.Models;

// A knowledge-base article (runbook, note, procedure) the agent can search and read.
// Tags map to a Postgres text[].
public class KnowledgeArticle : BaseModel
{
    public Guid KnowledgeArticleId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];

    // URL-safe slug derived from a title (unicode letters/digits kept, others → '-').
    public static string Slugify(string? title)
    {
        var sb = new StringBuilder();
        foreach (var ch in (title ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '.' or '/') sb.Append('-');
        }
        var s = sb.ToString();
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        if (s.Length > 80) s = s[..80].Trim('-');
        return s.Length == 0 ? "article" : s;
    }
}
