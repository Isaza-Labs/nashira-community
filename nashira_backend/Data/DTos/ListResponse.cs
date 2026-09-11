using System.Text.Json.Serialization;

namespace nashira_backend.Data.DTos;

// Paginated list envelope returned by IBaseService.GetAsync.
public class ListResponse<T>
{
    [JsonPropertyName("items")]
    public IReadOnlyList<T> Items { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }
}
