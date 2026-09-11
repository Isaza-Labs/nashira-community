namespace nashira_backend.Services.Ai.Specs;

// In-memory index of parsed OpenAPI operations. Singleton so the
// parsed structures survive across requests.
public interface IApiSpecIndex
{
    // Load the index if it isn't loaded yet (lazy warm).
    Task EnsureLoadedAsync(CancellationToken ct = default);

    // Re-parse the specs (call after a spec mutation).
    Task ReloadAsync(CancellationToken ct = default);

    IReadOnlyList<ApiOperation> All();
    IReadOnlyList<ApiOperation> Search(string keyword, string? api = null, string? method = null);
    ApiOperation? GetByOperationId(string operationId);

    /// <summary>
    /// The operation with this id <em>within the named source</em>, or null when that source
    /// does not define it. A null or blank <paramref name="api"/> falls back to the unscoped
    /// lookup, so a caller that names no source behaves exactly as it did before.
    /// </summary>
    /// <remarks>
    /// The unscoped index is keyed by operation id alone
    /// (<c>ops.GroupBy(o =&gt; o.OperationId).ToDictionary(g =&gt; g.Key, g =&gt; g.First())</c>),
    /// so two specs defining the same id collapse: one wins by load order and the other
    /// becomes unreachable by id entirely. That is tolerable for an agent browsing the
    /// catalogue and is not tolerable for a workflow, where the two candidates are different
    /// upstreams with different credentials and the step reports a well-formed success
    /// either way.
    /// </remarks>
    ApiOperation? GetByOperationId(string operationId, string? api)
    {
        // Default for implementations with no api-aware index: resolve unscoped, then refuse
        // a mismatch. It can miss an operation that a same-id sibling shadows, but it can
        // never return one from the WRONG source, which is the outcome that matters — a
        // caller gets "not found in that source" rather than another upstream's credentials.
        // The real index overrides this with a search that also finds the shadowed one.
        var op = GetByOperationId(operationId);
        if (op is null || string.IsNullOrWhiteSpace(api)) return op;
        return string.Equals(op.Api, api, StringComparison.OrdinalIgnoreCase) ? op : null;
    }
}
