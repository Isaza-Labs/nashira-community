using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Exceptions;
using nashira_backend.Services.Worker;

namespace nashira_backend.Services.Workflow;

// The write-time gate that asks whether a definition's nodes point at anything real.
//
// `WorkflowValidator` answers "is this workflow.v1, and is it acyclic". Both were true
// of a workflow whose only meaningful node carried `snippet_id: "__ping__"` — a string
// nobody had ever defined. It stored, promoted and ran, and every node reported
// no_change, which is indistinguishable from a run that did its job and found nothing
// to change. The engine now fails an unresolvable reference at run time, but by then
// the author has left and the workflow is scheduled.
//
// So the reference is checked where it is written. The schema deliberately does not do
// this — it validates shape, and shape cannot know which snippets exist — which is why
// this is a separate gate rather than another rule in the JSON schema.
public sealed class WorkflowReferenceChecker
{
    // The literals workflow.v1 allows in place of a snippet id. `__start__` and
    // `__end__` are structural; `subflow` denotes work this build cannot execute, so it
    // is accepted at authoring time and reported as skipped at run time rather than
    // rejected here — refusing to store it would make the schema and the API disagree.
    private static readonly HashSet<string> Literals =
        new(StringComparer.Ordinal) { "__start__", "__end__", "subflow" };

    private readonly AppDbContext _db;
    private readonly ISnippetHandlerRegistry _registry;

    public WorkflowReferenceChecker(AppDbContext db, ISnippetHandlerRegistry registry)
    {
        _db = db;
        _registry = registry;
    }

    public async Task EnsureResolvableAsync(JsonElement nodes, CancellationToken ct = default)
    {
        if (nodes.ValueKind != JsonValueKind.Array) return;

        var malformed = new List<string>();
        var referenced = new Dictionary<Guid, List<string>>();

        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            var nodeId = node.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? "?"
                : "?";
            if (!node.TryGetProperty("snippet_id", out var refEl) || refEl.ValueKind != JsonValueKind.String)
                continue;

            var reference = refEl.GetString() ?? string.Empty;
            if (Literals.Contains(reference)) continue;

            if (Guid.TryParse(reference, out var snippetId))
            {
                if (!referenced.TryGetValue(snippetId, out var nodesForId))
                    referenced[snippetId] = nodesForId = [];
                nodesForId.Add(nodeId);
                continue;
            }

            // Not a literal and not a UUID. The most common shape of this mistake is an
            // invented placeholder in the style of the real literals, so the message
            // quotes what was written rather than only naming the node.
            malformed.Add($"'{nodeId}' → '{reference}'");
        }

        if (malformed.Count > 0)
            throw new ValidationException(
                $"{Describe(malformed.Count)} a snippet_id that is neither a snippet UUID nor one of "
                + $"__start__, __end__, subflow: {string.Join(", ", malformed)}. "
                + "List the catalogue to find the snippet you want, or create one first.",
                code: "snippet_reference_invalid");

        if (referenced.Count == 0) return;

        var ids = referenced.Keys.ToList();
        var found = await _db.Snippets.AsNoTracking()
            .Where(s => ids.Contains(s.SnippetId) && s.IsActive)
            .Select(s => new { s.SnippetId, s.Type })
            .ToListAsync(ct);
        var foundById = found.ToDictionary(snippet => snippet.SnippetId);

        var missing = referenced
            .Where(pair => !foundById.ContainsKey(pair.Key))
            .SelectMany(pair => pair.Value.Select(n => $"'{n}' → {pair.Key}"))
            .ToList();

        if (missing.Count > 0)
            throw new ValidationException(
                $"{Describe(missing.Count)} a snippet that does not exist: {string.Join(", ", missing)}.",
                code: "snippet_not_found");

        var knownTypes = _registry.KnownTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unavailable = found
            .Where(snippet => !knownTypes.Contains(snippet.Type))
            .SelectMany(snippet => referenced[snippet.SnippetId]
                .Select(node => $"'{node}' → '{snippet.Type}'"))
            .ToList();

        if (unavailable.Count > 0)
            throw new ValidationException(
                $"{Describe(unavailable.Count)} a snippet type unavailable in this deployment: "
                + $"{string.Join(", ", unavailable)}.",
                code: "snippet_type_unavailable");
    }

    private static string Describe(int count) =>
        count == 1 ? "one node references" : $"{count} nodes reference";
}
