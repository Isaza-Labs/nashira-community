using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Ai.Specs;
using nashira_backend.Services.Ai.Tools;

namespace nashira_backend.Services.Integration;

public sealed record ActionSyncResult(int Created, int Updated, int Unchanged, int Disappeared, int SpecCount);

public interface IIntegrationActionSync
{
    Task<ActionSyncResult> SyncAsync(Guid integrationId, CancellationToken ct);
}

// Rebuilds an integration's action catalog from the OpenAPI specs linked to it
// (AiApiSpec.IntegrationId).
//
// This is the point of the whole integration/spec link: before it, the agent's
// discover/execute catalog and the workflow builder's action catalog were two
// independent lists describing the same API, and they drifted. Now one upload
// feeds both — the spec stays the source of truth and IntegrationAction rows are
// its projection.
//
// Idempotent by (IntegrationId, OperationId). Actions whose operation vanished from
// the spec are disabled rather than deleted: a workflow may still reference one, and
// "this operation no longer exists upstream" is a better failure than a dangling id.
// The Enabled flag an admin set by hand is never overwritten by a re-sync.
public sealed class IntegrationActionSync : IIntegrationActionSync
{
    private static readonly HashSet<string> ReadMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    private readonly AppDbContext _db;
    private readonly ILogger<IntegrationActionSync> _logger;

    public IntegrationActionSync(AppDbContext db, ILogger<IntegrationActionSync> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ActionSyncResult> SyncAsync(Guid integrationId, CancellationToken ct)
    {
        var specs = await _db.AiApiSpecs.AsNoTracking()
            .Where(s => s.IsActive && s.IntegrationId == integrationId)
            .Select(s => new { s.Api, s.Content })
            .ToListAsync(ct);

        var existing = await _db.IntegrationActions
            .Where(a => a.IntegrationId == integrationId)
            .ToListAsync(ct);
        var byOperation = existing
            .Where(a => a.OperationId is not null)
            .ToDictionary(a => a.OperationId!, StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int created = 0, updated = 0, unchanged = 0;

        foreach (var spec in specs)
        {
            List<ApiOperation> operations;
            try
            {
                operations = YamlSpecIndex.ParseOperations(spec.Api, spec.Content).ToList();
            }
            catch (Exception ex)
            {
                // One unparseable spec must not wipe the catalog the others produced.
                _logger.LogWarning(ex, "integration.actions.spec_parse_failed api={Api}", spec.Api);
                continue;
            }

            foreach (var op in operations)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(op.OperationId)) continue;

                var slice = TrySlice(spec.Content, op);
                var name = string.IsNullOrWhiteSpace(op.Summary) ? op.OperationId : op.Summary;
                var category = op.Tags.Count > 0 ? op.Tags[0] : string.Empty;
                var pathParams = ParamsJson(slice, "path");
                var queryParams = ParamsJson(slice, "query");
                var requestBody = slice?.RequestBody is { } rb ? JsonSerializer.Serialize(rb) : null;

                if (byOperation.TryGetValue(op.OperationId, out var row))
                {
                    var changed =
                        row.Name != name || row.Description != op.Description || row.Method != op.Method
                        || row.Path != op.Path || row.Category != category
                        || row.PathParamsJson != pathParams || row.QueryParamsJson != queryParams
                        || row.RequestBodyJson != requestBody || !row.IsActive || row.DisappearedFromSpec();

                    row.Name = name;
                    row.Description = op.Description;
                    row.Method = op.Method;
                    row.Path = op.Path;
                    row.Category = category;
                    row.PathParamsJson = pathParams;
                    row.QueryParamsJson = queryParams;
                    row.RequestBodyJson = requestBody;
                    // ReadOnly is re-derived from the method, but only when the admin
                    // has not overridden it — see IntegrationAction.ReadOnly.
                    row.IsActive = true;
                    row.UpdatedAt = now;

                    if (changed) updated++; else unchanged++;
                }
                else
                {
                    _db.IntegrationActions.Add(new IntegrationAction
                    {
                        IntegrationActionId = Guid.NewGuid(),
                        IntegrationId = integrationId,
                        OperationId = op.OperationId,
                        Name = name,
                        Description = op.Description,
                        Method = op.Method,
                        Path = op.Path,
                        Category = category,
                        PathParamsJson = pathParams,
                        QueryParamsJson = queryParams,
                        RequestBodyJson = requestBody,
                        ReadOnly = ReadMethods.Contains(op.Method),
                        Enabled = true,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    created++;
                }
            }
        }

        // Synced rows the specs no longer describe. Hand-written actions (no
        // operation id) are untouched — they were never the spec's to manage.
        var disappeared = 0;
        foreach (var row in existing)
        {
            if (row.OperationId is null || seen.Contains(row.OperationId) || !row.IsActive) continue;
            row.IsActive = false;
            row.UpdatedAt = now;
            disappeared++;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "integration.actions.synced integration={Integration} specs={Specs} created={Created} updated={Updated} gone={Gone}",
            integrationId, specs.Count, created, updated, disappeared);

        return new ActionSyncResult(created, updated, unchanged, disappeared, specs.Count);
    }

    private OperationYamlSlicer.DetailSlice? TrySlice(string yaml, ApiOperation op)
    {
        try
        {
            return OperationYamlSlicer.ExtractDetail(yaml, op.Method, op.Path);
        }
        catch (Exception ex)
        {
            // The operation is still worth cataloguing without its parameter detail.
            _logger.LogDebug(ex, "integration.actions.slice_failed operation={Operation}", op.OperationId);
            return null;
        }
    }

    // The slicer returns every parameter in one list tagged with its `in`; the
    // catalog stores path and query separately because callers supply them
    // separately.
    private static string? ParamsJson(OperationYamlSlicer.DetailSlice? slice, string location)
    {
        if (slice is null || slice.Parameters.Count == 0) return null;

        var matching = slice.Parameters
            .Select(p => JsonSerializer.SerializeToElement(p))
            .Where(e => e.TryGetProperty("in", out var i)
                        && i.ValueKind == JsonValueKind.String
                        && string.Equals(i.GetString(), location, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matching.Count == 0 ? null : JsonSerializer.Serialize(matching);
    }
}

internal static class IntegrationActionExtensions
{
    // A row that a previous sync retired and this one found again.
    public static bool DisappearedFromSpec(this IntegrationAction row) => !row.IsActive;
}
