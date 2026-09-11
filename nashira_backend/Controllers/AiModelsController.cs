using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.DTos.AIProvider;
using Microsoft.AspNetCore.RateLimiting;
using nashira_backend.Services.Security;

namespace nashira_backend.Controllers;

// The models a chat request may name, flattened across the tenant's enabled providers.
//
// Its own controller rather than an action under /api/ai/providers for two reasons: the
// question is about the tenant, not about one provider row, and provider CRUD is
// Admin-only while *choosing* a model is not a configuration change. An [Authorize] on
// a class cannot be relaxed by one on an action — they compose — so a laxer policy needs
// its own controller.
//
// Viewer, matching the chat itself: this list is what fills the chat's model selector,
// and a role allowed to send a turn has to be able to see which models it may name.
// Anything narrower would show the picker to some users and not others while both can
// chat. Nothing here is secret — model ids and provider names, never keys.
[ApiController]
[Route("api/ai/models")]
[Authorize(Policy = "Viewer")]
[EnableRateLimiting(RateLimitingConfiguration.ReadHeavy)]
public class AiModelsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AiModelsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AIModelResponse>>> Get(CancellationToken ct)
    {
        var providers = await _db.AIProviders.AsNoTracking()
            .Where(p => p.IsActive && p.Enabled)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var models = new List<AIModelResponse>();
        foreach (var p in providers)
        {
            // Deduplicated per provider: config.models routinely repeats the default.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? id, bool isDefault)
            {
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id.Trim())) return;
                models.Add(new AIModelResponse
                {
                    Id = id.Trim(),
                    Provider = p.Name,
                    AIProviderId = p.AIProviderId,
                    ProviderType = p.Type,
                    IsDefault = isDefault,
                });
            }

            Add(p.DefaultModel, true);
            foreach (var extra in ConfiguredModels(p.Config)) Add(extra, false);
        }

        return new OkObjectResult(models);
    }

    // `config.models` is an optional string array on the provider row — a place to list
    // the models an installation actually licenses, beyond the default. Anything else in
    // that blob belongs to somebody else, so a wrong shape is ignored rather than fatal.
    private static IEnumerable<string> ConfiguredModels(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty("models", out var list)
            || list.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                yield return id;
    }
}
