using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using nashira_backend.Data.Db;
using nashira_backend.Data.Models;
using nashira_backend.Services.Audit;
using nashira_backend.Services.Identity;

namespace nashira_backend.Tests;

// What happens to the trail when a write fails.
//
// The logger swallows the failure rather than rethrowing, because every caller writes
// its row AFTER committing the mutation — answering a request that already took effect
// with a 500 makes the client retry and apply the change twice. But swallowing is only
// safe if the failed row is also removed from the change tracker: the DbContext is
// scoped and shared, so a row left `Added` poisons every subsequent save in the same
// request, including saves that have nothing to do with auditing.
public class AuditLoggerFailureTests
{
    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static AuditLogger NewLogger(AppDbContext db) =>
        new(db, new FakeUser(), new FakeHttp(), NullLogger<AuditLogger>.Instance);

    // Forces the first save to fail the way a real one would: a row already holding the
    // sequence the logger is about to claim. The unique index on Sequence rejects it.
    private static void Poison(AppDbContext db, long sequence)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            AuditEventId = Guid.NewGuid(),
            Sequence = sequence,
            EntityType = "seed",
            Action = "seed",
            At = DateTime.UtcNow,
            Hash = "seed",
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_failed_write_does_not_throw()
    {
        var name = $"audit-fail-{Guid.NewGuid()}";
        using var db = NewDb(name);
        var logger = NewLogger(db);

        // Two writers racing for sequence 1: the second collides.
        await logger.LogAsync("credential", Guid.NewGuid(), "create", ct: default);
        Poison(db, 2);

        // The caller's mutation has already committed by this point; this must not
        // become its exception.
        var ex = await Record.ExceptionAsync(() =>
            logger.LogAsync("credential", Guid.NewGuid(), "update", ct: default));

        Assert.Null(ex);
    }

    // The defect this file exists for: a failed row left in the tracker makes the NEXT
    // audit write try to insert both, so one transient failure silently loses every
    // remaining row of the request — and takes the caller's own unrelated save with it.
    [Fact]
    public async Task A_failed_write_does_not_poison_the_next_one()
    {
        var name = $"audit-fail-{Guid.NewGuid()}";
        using var db = NewDb(name);
        var logger = NewLogger(db);

        Poison(db, 1);

        // Collides on sequence 1 and is swallowed.
        await logger.LogAsync("credential", Guid.NewGuid(), "create", ct: default);

        // Must succeed on its own merits, not drag the failed row along.
        await logger.LogAsync("workflow", Guid.NewGuid(), "promote", ct: default);

        var written = await db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityType == "workflow").ToListAsync();
        Assert.Single(written);

        // And nothing stale is left behind to break an unrelated save later in the
        // same scope — which is how a lost audit row used to become a lost job status.
        Assert.DoesNotContain(db.ChangeTracker.Entries<AuditEvent>(),
            e => e.State == EntityState.Added);
        var unrelated = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Null(unrelated);
    }

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId => Guid.NewGuid();
        public string? Username => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => false;
    }

    private sealed class FakeHttp : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
