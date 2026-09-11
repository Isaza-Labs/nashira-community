using System.Text.Json;
using nashira_backend.Data.Models;

namespace nashira_backend.Services.Messaging;

// Reads the two free-form JSON columns on a channel.
//
// They are stored as text rather than typed columns because their shape is
// per-provider and changes with the platform (WhatsApp's graph_version, Teams'
// tenant_id). Every read goes through here so a hand-edited or half-written blob
// degrades to "no config" instead of throwing somewhere in the middle of an
// inbound webhook.
public static class MessagingChannelConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    // The non-secret provider config as a dictionary of string values. Non-string
    // JSON values are rendered with their raw text so a number or boolean written
    // by hand still reaches the provider intact.
    public static IReadOnlyDictionary<string, string> External(MessagingChannel channel)
        => Parse(channel.ExternalConfigJson);

    // A single config key, or null when absent/blank.
    public static string? External(MessagingChannel channel, string key)
        => External(channel).TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return map;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var value = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    _ => p.Value.GetRawText(),
                };
                if (!string.IsNullOrWhiteSpace(value)) map[p.Name] = value;
            }
        }
        catch (JsonException)
        {
            // A broken blob is treated as empty. The provider will then fail with
            // "phone_number_id is required", which points at the field to fix —
            // better than a parse exception from inside the webhook path.
        }

        return map;
    }

    // Serializes a config map back to storage, dropping blanks so an untouched
    // optional key is not persisted as "" and trips the provider's "empty" branch
    // instead of its "missing" one.
    public static string? Serialize(IReadOnlyDictionary<string, string?>? config)
    {
        if (config is null) return null;
        var clean = config
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value!.Trim());
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean, Options);
    }

    // The allowlist of external user ids. Empty means "no allowlist" — anyone who
    // passes the linking rules may talk to the bot.
    public static IReadOnlyList<string> AllowedExternalIds(MessagingChannel channel)
        => ParseIdList(channel.AllowedExternalIdsJson);

    public static IReadOnlyList<string> ParseIdList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? SerializeIdList(IEnumerable<string>? ids)
    {
        var clean = (ids ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean, Options);
    }
}
