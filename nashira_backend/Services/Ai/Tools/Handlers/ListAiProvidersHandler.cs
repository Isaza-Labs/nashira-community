using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

public sealed class ListAiProvidersHandler : IToolHandler
{
    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public ListAiProvidersHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "list_ai_providers";
    public string Description => "Lists the LLM providers configured for the current tenant (no secrets).";
    public JsonElement ParametersSchema => ToolSchemas.Empty;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var rows = await _db.AIProviders.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new { name = p.Name, type = p.Type, default_model = p.DefaultModel, enabled = p.Enabled })
            .ToListAsync(ct);
        return JsonSerializer.SerializeToElement(new { providers = rows, count = rows.Count });
    }
}
