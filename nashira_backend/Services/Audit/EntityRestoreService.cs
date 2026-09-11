using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;

namespace nashira_backend.Services.Audit;

public sealed record RestoreResult(string EntityType, Guid EntityId, string? Name);

// Undoes a soft delete: flips IsActive back on for the record a delete audit event
// points at. Deliberately map-driven rather than reflective over every DbSet — only
// entity types whose delete endpoints are known to be soft (IsActive = false) are
// restorable, so a type missing here fails as "cannot be restored" instead of
// resurrecting something whose controller never expected to see it again.
public sealed class EntityRestoreService
{
    private static readonly Dictionary<string, Type> Restorable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["credential"] = typeof(Credential),
        ["secret"] = typeof(Secret),
        ["user"] = typeof(User),
        ["profile"] = typeof(Profile),
        ["ai_provider"] = typeof(AIProvider),
        ["integration"] = typeof(Data.Models.Integration),
        ["integration_action"] = typeof(IntegrationAction),
        ["mcp_server"] = typeof(McpServer),
        ["ai_api_spec"] = typeof(AiApiSpec),
        ["ai_prompt_skill"] = typeof(AiPromptSkill),
        ["policy"] = typeof(Data.Models.Policy),
        ["device"] = typeof(Device),
        ["device_pool"] = typeof(DevicePool),
        ["inventory_source"] = typeof(InventorySource),
        ["workflow"] = typeof(Data.Models.Workflow),
        ["workflow_trigger"] = typeof(WorkflowTrigger),
        ["snippet"] = typeof(Snippet),
        ["vendor_command"] = typeof(VendorCommand),
        ["allowed_python_module"] = typeof(AllowedPythonModule),
        ["email_channel"] = typeof(EmailChannel),
        ["notification_channel"] = typeof(NotificationChannel),
        ["notification_channel"] = typeof(NotificationChannel),
        ["git_repository"] = typeof(GitRepository),
        ["knowledge_article"] = typeof(KnowledgeArticle),
        ["agent_learning"] = typeof(AgentLearning),
        ["ai_conversation"] = typeof(AIConversation),
        // Rows written before the filter mapped this controller carry the raw
        // snake-cased controller name; both spellings point at the same table.
        ["ai_conversations"] = typeof(AIConversation),
    };

    private readonly AppDbContext _db;

    public EntityRestoreService(AppDbContext db) => _db = db;

    public static bool CanRestore(string entityType) => Restorable.ContainsKey(entityType);

    public async Task<RestoreResult> RestoreAsync(string entityType, Guid entityId, CancellationToken ct)
    {
        if (!Restorable.TryGetValue(entityType, out var clr))
            throw new ValidationException(
                $"entity type '{entityType}' cannot be restored — it is not a soft-deleted record");

        // FindAsync resolves the primary key from EF metadata, so one call covers
        // every mapped type without a per-entity key-name switch.
        var entity = await _db.FindAsync(clr, [entityId], ct);
        if (entity is not BaseModel row)
            throw new NotFoundException("record not found — it may have been permanently removed");
        if (row.IsActive)
            throw new ConflictException("the record is already active; nothing to restore");

        row.IsActive = true;
        row.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Most types keep name/slug unique among ACTIVE rows only, so a live
            // record created after the delete can now hold the name. Surfaced as a
            // conflict the operator can act on, not a 500.
            _db.Entry(row).State = EntityState.Unchanged;
            row.IsActive = false;
            throw new ConflictException(
                "restoring collides with an active record that now uses the same unique name or slug — "
                + "rename or delete that record first");
        }
        // A conversation comes back whole: its attachments were soft-deleted with it
        // (see AiConversationsController.Delete) precisely so this can undo them.
        if (row is AIConversation conv)
        {
            var attachments = await _db.ConversationAttachments
                .Where(a => a.ConversationId == conv.AIConversationId && !a.IsActive)
                .ToListAsync(ct);
            foreach (var a in attachments)
            {
                a.IsActive = true;
                a.UpdatedAt = DateTime.UtcNow;
            }
            if (attachments.Count > 0) await _db.SaveChangesAsync(ct);
        }

        return new RestoreResult(entityType, entityId, NameOf(row));
    }

    // Best-effort display name for the toast and the audit row; each entity names
    // itself differently and none of this is load-bearing.
    private static string? NameOf(object row)
    {
        var t = row.GetType();
        foreach (var prop in (string[])["Name", "Username", "DeviceName", "Title", "Slug"])
            if (t.GetProperty(prop)?.GetValue(row) is string s && s.Length > 0)
                return s;
        return null;
    }
}
