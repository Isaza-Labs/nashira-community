using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;

namespace nashira_backend.Services.Ai.Tools.Handlers;

// Recomputes the audit hash-chain to prove it has not been tampered with. Admin-only; read → autonomous.
public sealed class VerifyAuditHandler : IToolHandler
{
    private static readonly JsonElement Schema =
        JsonDocument.Parse("""{"type":"object","properties":{},"additionalProperties":false}""").RootElement.Clone();

    private readonly AppDbContext _db;
    private readonly ICurrentUser _user;

    public VerifyAuditHandler(AppDbContext db, ICurrentUser user)
    {
        _db = db;
        _user = user;
    }

    public string Name => "verify_audit";
    public string Description =>
        "Verifies the integrity of the audit trail by recomputing its hash chain. Returns valid + the " +
        "broken sequence if tampering is detected. Admin only.";
    public JsonElement ParametersSchema => Schema;

    public async Task<JsonElement> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var ordered = await _db.AuditEvents.AsNoTracking()
            
            .OrderBy(a => a.Sequence)
            .ToListAsync(ct);
        var result = AuditChain.Verify(ordered);
        return JsonSerializer.SerializeToElement(new
        {
            valid = result.Valid,
            count = result.Count,
            broken_at_sequence = result.BrokenAtSequence,
            reason = result.Reason,
        });
    }
}
