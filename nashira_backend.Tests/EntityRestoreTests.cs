using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;

namespace nashira_backend.Tests;

// EntityRestoreService undoes a soft delete from a delete audit event. The map of
// restorable types is deliberately explicit; anything outside it must refuse rather
// than resurrect a record whose controller never expected it back.
public class EntityRestoreTests
{
    private static (EntityRestoreService Svc, AppDbContext Db) Build()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"restore-{Guid.NewGuid()}")
            .Options);
        return (new EntityRestoreService(db), db);
    }

    [Fact]
    public async Task Restores_a_soft_deleted_credential()
    {
        var (svc, db) = Build();
        var id = Guid.NewGuid();
        db.Credentials.Add(new Credential { CredentialId = id, Name = "sw-admin", IsActive = false });
        await db.SaveChangesAsync();

        var result = await svc.RestoreAsync("credential", id, default);

        Assert.Equal("sw-admin", result.Name);
        var row = await db.Credentials.SingleAsync(c => c.CredentialId == id);
        Assert.True(row.IsActive);
    }

    [Fact]
    public async Task Uses_the_entity_specific_name_for_the_result()
    {
        var (svc, db) = Build();
        var id = Guid.NewGuid();
        db.Devices.Add(new Device { DeviceId = id, DeviceName = "core-sw-01", IsActive = false });
        await db.SaveChangesAsync();

        var result = await svc.RestoreAsync("device", id, default);
        Assert.Equal("core-sw-01", result.Name);
    }

    [Fact]
    public async Task An_already_active_record_conflicts()
    {
        var (svc, db) = Build();
        var id = Guid.NewGuid();
        db.Secrets.Add(new Secret { SecretId = id, Name = "live", EncryptedValue = [1], IsActive = true });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => svc.RestoreAsync("secret", id, default));
    }

    [Fact]
    public async Task An_unknown_entity_type_is_refused()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<ValidationException>(
            () => svc.RestoreAsync("agent.tool", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task A_missing_record_is_not_found()
    {
        var (svc, _) = Build();
        await Assert.ThrowsAsync<NotFoundException>(
            () => svc.RestoreAsync("credential", Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Restoring_a_conversation_brings_its_attachments_back()
    {
        var (svc, db) = Build();
        var convId = Guid.NewGuid();
        db.AIConversations.Add(new AIConversation
        {
            AIConversationId = convId,
            UserId = Guid.NewGuid(),
            Title = "matrix review",
            IsActive = false,
        });
        db.ConversationAttachments.Add(new ConversationAttachment
        {
            ConversationAttachmentId = Guid.NewGuid(),
            ConversationId = convId,
            Filename = "matrix.xlsx",
            Content = [1],
            SizeBytes = 1,
            IsActive = false, // soft-deleted with the conversation
        });
        await db.SaveChangesAsync();

        var result = await svc.RestoreAsync("ai_conversation", convId, default);

        Assert.Equal("matrix review", result.Name);
        Assert.True((await db.AIConversations.SingleAsync(c => c.AIConversationId == convId)).IsActive);
        Assert.True((await db.ConversationAttachments.SingleAsync(a => a.ConversationId == convId)).IsActive);
    }

    [Fact]
    public void CanRestore_matches_the_map()
    {
        Assert.True(EntityRestoreService.CanRestore("inventory_source"));
        Assert.True(EntityRestoreService.CanRestore("workflow"));
        // Both spellings: the audit filter now writes ai_conversation, older rows
        // carry the raw snake-cased controller name.
        Assert.True(EntityRestoreService.CanRestore("ai_conversation"));
        Assert.True(EntityRestoreService.CanRestore("ai_conversations"));
        Assert.False(EntityRestoreService.CanRestore("agent.tool"));
        Assert.False(EntityRestoreService.CanRestore("report"));
    }
}
