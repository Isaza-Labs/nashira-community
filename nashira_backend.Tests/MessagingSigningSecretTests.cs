using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using nashira_backend.Controllers;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Exceptions;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Messaging;
using nashira_backend.Services.Messaging.Providers;
using nashira_backend.Services.Security;

namespace nashira_backend.Tests;

public class MessagingSigningSecretTests
{
    private const string First = "0123456789abcdef0123456789abcdef";
    private const string Second = "fedcba9876543210fedcba9876543210";

    private sealed class Protector : ISecretProtector
    {
        public int Accesses;
        public byte[]? Encrypt(string? value) { Accesses++; return value is null ? null : Encoding.UTF8.GetBytes(value); }
        public string? Decrypt(byte[]? value) { Accesses++; return value is null ? null : Encoding.UTF8.GetString(value); }
    }

    private sealed class AuditFailure : SaveChangesInterceptor
    {
        public bool Fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Fail && data.Context!.ChangeTracker.Entries<AuditEvent>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("simulated audit storage failure");
            return ValueTask.FromResult(result);
        }
    }

    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MessagingChannelController Controller(AppDbContext db, HardeningTestUser? user = null, Protector? protector = null)
    {
        user ??= new HardeningTestUser();
        return new(db, user, protector ?? new Protector(), Options.Create(new MessagingOptions()),
            new AuditLogger(db, user, new HttpContextAccessor(), NullLogger<AuditLogger>.Instance));
    }

    private static async Task<MessagingChannelResponse> Create(MessagingChannelController controller, string? secret = First)
    {
        var response = await controller.Post(new WriteMessagingChannel
        { Provider = "telegram", Name = "test", SigningSecret = secret }, default);
        return Assert.IsType<MessagingChannelResponse>(Assert.IsType<CreatedAtActionResult>(response.Result).Value);
    }

    [Fact]
    public async Task Set_rotate_clear_are_write_only_audited_and_change_webhook_verification()
    {
        using var db = Db();
        var controller = Controller(db);
        var created = await Create(controller);
        var rotated = await controller.Update(created.Id, new WriteMessagingChannel { SigningSecret = Second }, default);
        var get = await controller.GetById(created.Id, default);
        var list = await controller.Get();
        var provider = new TelegramProvider(null!, NullLogger<TelegramProvider>.Instance);
        var row = await db.MessagingChannels.SingleAsync();
        var secret = Encoding.UTF8.GetString(row.SigningSecretEncrypted!);
        Assert.Equal(Second, secret);
        async Task<int> Verify(string? token)
        {
            var headers = new Dictionary<string, string>();
            if (token is not null) headers["X-Telegram-Bot-Api-Secret-Token"] = token;
            var result = await provider.VerifyAsync(row,
                new MessagingHttpRequest { Method = "POST", Body = [], Headers = headers },
                row.SigningSecretEncrypted is null ? null : Encoding.UTF8.GetString(row.SigningSecretEncrypted), default);
            return result.Outcome == WebhookVerifyOutcome.Verified ? 200 : result.RejectStatusCode;
        }
        Assert.Equal(401, await Verify(First));
        Assert.Equal(200, await Verify(Second));
        Assert.Equal(401, await Verify(null));
        var cleared = await controller.Update(created.Id, new WriteMessagingChannel { ClearSigningSecret = true, AllowUnsigned = true }, default);
        Assert.Null(row.SigningSecretEncrypted);
        Assert.False(cleared.Value!.HasSigningSecret);
        Assert.Equal(401, await Verify(Second));
        Assert.Equal(401, await Verify(null));
        var events = await db.AuditEvents.OrderBy(e => e.Sequence).ToListAsync();
        Assert.Equal(new[] { "signing_secret.set", "signing_secret.rotate", "signing_secret.clear" }, events.Select(e => e.Action));
        Assert.All(events, e => { Assert.Equal(created.Id, e.EntityId); Assert.Equal("hardening-test", e.Actor); });
        foreach (var payload in new object?[] { created, rotated.Value, get.Value, ((OkObjectResult)list.Result!).Value, cleared.Value, events })
        {
            var json = JsonSerializer.Serialize(payload);
            Assert.DoesNotContain(First, json);
            Assert.DoesNotContain(Second, json);
            Assert.DoesNotContain("\"signing_secret\":", json);
            Assert.DoesNotContain("SigningSecretEncrypted", json);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("short-secret")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("0123456789abcdef0123456789abcdef ")]
    [InlineData("0123456789abcdef0123456789abcdef\n")]
    public async Task Weak_or_empty_values_are_rejected_without_mutation_or_echo(string value)
    {
        using var db = Db();
        var controller = Controller(db);
        var createError = await Assert.ThrowsAsync<ValidationException>(() => Create(controller, value));
        Assert.Equal("signing_secret_invalid", createError.Code);
        Assert.Empty(db.MessagingChannels);
        var created = await Create(controller);
        var ex = await Assert.ThrowsAsync<ValidationException>(() => controller.Update(created.Id,
            new WriteMessagingChannel { SigningSecret = value }, default));
        Assert.Equal("signing_secret_invalid", ex.Code);
        Assert.Equal(First, Encoding.UTF8.GetString((await db.MessagingChannels.SingleAsync()).SigningSecretEncrypted!));
        Assert.Single(db.AuditEvents);
    }

    [Fact]
    public async Task Oversized_or_ambiguous_requests_are_rejected()
    {
        using var db = Db();
        var controller = Controller(db);
        var created = await Create(controller);
        foreach (var dto in new[] {
            new WriteMessagingChannel { SigningSecret = string.Concat(Enumerable.Repeat(First, 129)) },
            new WriteMessagingChannel { SigningSecret = Second, ClearSigningSecret = true } })
            await Assert.ThrowsAsync<ValidationException>(() => controller.Update(created.Id, dto, default));
        Assert.Single(db.AuditEvents);
    }

    [Fact]
    public async Task Unrelated_update_preserves_secret_and_does_not_emit_a_rotation()
    {
        using var db = Db();
        var controller = Controller(db);
        var created = await Create(controller);
        await controller.Update(created.Id, new WriteMessagingChannel { Name = "renamed" }, default);
        Assert.Equal(First, Encoding.UTF8.GetString((await db.MessagingChannels.SingleAsync()).SigningSecretEncrypted!));
        Assert.Single(db.AuditEvents);
    }

    [Theory]
    [InlineData("operator", true)]
    [InlineData("viewer", true)]
    [InlineData("admin", false)]
    public async Task Non_admin_cannot_set_rotate_or_clear_and_is_denied_before_secret_access(string role, bool authenticated)
    {
        using var db = Db();
        var created = await Create(Controller(db));
        var protector = new Protector();
        var controller = Controller(db, new HardeningTestUser(role, authenticated), protector);
        var post = await Assert.ThrowsAsync<ForbiddenException>(() => Create(controller));
        Assert.Equal(403, post.Status);
        foreach (var dto in new[] { new WriteMessagingChannel { SigningSecret = Second }, new WriteMessagingChannel { ClearSigningSecret = true } })
        {
            var error = await Assert.ThrowsAsync<ForbiddenException>(() => controller.Update(created.Id, dto, default));
            Assert.Equal(403, error.Status);
        }
        Assert.Equal(0, protector.Accesses);
        Assert.Single(db.AuditEvents);
        Assert.Equal("Admin", Assert.Single(typeof(MessagingChannelController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Policy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_storage_failure_prevents_secret_mutation(bool create)
    {
        var failure = new AuditFailure();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(failure).Options;
        using var db = new AppDbContext(options);
        var controller = Controller(db);
        var id = create ? Guid.Empty : (await Create(controller)).Id;
        failure.Fail = true;
        if (create) await Assert.ThrowsAsync<InvalidOperationException>(() => Create(controller));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Update(id, new WriteMessagingChannel { SigningSecret = Second }, default));
        using var verify = new AppDbContext(options);
        if (create) { Assert.Empty(verify.MessagingChannels); Assert.Empty(verify.AuditEvents); }
        else { Assert.Equal(First, Encoding.UTF8.GetString((await verify.MessagingChannels.SingleAsync()).SigningSecretEncrypted!)); Assert.Single(verify.AuditEvents); }
    }
}
