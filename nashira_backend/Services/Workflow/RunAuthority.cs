using Microsoft.EntityFrameworkCore;
using nashira_backend.Data.Db;
using nashira_backend.Services.Identity;
using SnippetEntity = nashira_backend.Data.Models.Snippet;
using TriggerEntity = nashira_backend.Data.Models.WorkflowTrigger;
using WorkflowEntity = nashira_backend.Data.Models.Workflow;

namespace nashira_backend.Services.Workflow;

// Caps a triggered run at the role of the least-privileged person who wrote what it
// executes.
//
// A triggered run executes as the user who created the trigger (the job worker binds
// that identity), but what the run DOES is decided by whoever last saved the trigger,
// the workflow, every snippet it reaches and every subflow it calls — and Operators
// may edit all of those. Without this, an Operator could add a step that sends a
// stored secret somewhere and have it run with the role of the Admin who created the
// trigger. So each of those rows lowers the run's role to its author's current role
// before any of its content runs. The role only ever goes down.
//
// Inert until Arm(). Only the job worker arms it, for trigger-fired runs: a manual run
// already executes as the person who pressed Run, and in an HTTP request ICurrentUser
// is not the MutableCurrentUser this rebinds anyway.
public sealed class RunAuthority
{
    private static readonly Dictionary<string, int> Rank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["viewer"] = 1,
        ["operator"] = 2,
        ["admin"] = 3,
    };

    private const string Lowest = "viewer";

    private readonly AppDbContext _db;
    private readonly MutableCurrentUser _user;
    private readonly ILogger<RunAuthority> _logger;

    // Per scope, so a run asks once per author however many of their snippets it reaches.
    // A role changed mid-run is picked up by the next run, not this one.
    private readonly Dictionary<Guid, string> _roles = new();

    public RunAuthority(AppDbContext db, MutableCurrentUser user, ILogger<RunAuthority> logger)
    {
        _db = db;
        _user = user;
        _logger = logger;
    }

    public bool Armed { get; private set; }

    public void Arm() => Armed = true;

    // "Author" means whoever last saved the row, falling back to its creator for rows
    // saved before the last-editor column existed. One definition for every caller.
    public static Guid? AuthorOf(WorkflowEntity w) => w.UpdatedByUserId ?? w.CreatedByUserId;
    public static Guid? AuthorOf(TriggerEntity t) => t.UpdatedBy ?? t.CreatedBy;
    public static Guid? AuthorOf(SnippetEntity s) => s.UpdatedBy ?? s.CreatedBy;

    // Lowers the bound role to the author's, if the author ranks lower. A null author is
    // system or seeded content (or a legacy row nobody is recorded against) and changes
    // nothing: there is no person whose edit could be borrowing the run's role.
    public async Task ApplyAuthorAsync(Guid? authorUserId, string what, CancellationToken ct)
    {
        if (!Armed || authorUserId is not { } authorId) return;
        // Nothing bound means an anonymous run; there is no role to lower.
        if (!_user.IsAuthenticated) return;

        var current = Highest(_user.Roles);
        var authorRole = await RoleOfAsync(authorId, ct);
        if (RankOf(authorRole) >= RankOf(current)) return;

        // Same instance, same identity, fewer rights. SecretAccessPolicy reads Roles when
        // it is asked, so every step after this one sees the lowered role.
        _user.Bind(_user.UserId, _user.Username, [authorRole]);
        _logger.LogWarning(
            "workflow.run_authority.lowered run_as={User} from={From} to={To} because={What} author={AuthorId}",
            _user.Username, current, authorRole, what, authorId);
    }

    // A missing or deactivated author ranks lowest: a disabled account must not keep
    // lending its role to content it once saved.
    private async Task<string> RoleOfAsync(Guid userId, CancellationToken ct)
    {
        if (_roles.TryGetValue(userId, out var cached)) return cached;
        var role = await _db.Users.AsNoTracking()
            .Where(u => u.UserId == userId && u.IsActive)
            .Select(u => u.Role)
            .FirstOrDefaultAsync(ct);
        var normalized = role is not null && Rank.ContainsKey(role) ? role.ToLowerInvariant() : Lowest;
        _roles[userId] = normalized;
        return normalized;
    }

    // An unknown role ranks lowest, so a typo in the users table can only take rights away.
    private static int RankOf(string role) => Rank.GetValueOrDefault(role, 1);

    private static string Highest(IReadOnlyList<string> roles)
    {
        var best = Lowest;
        foreach (var r in roles)
            if (RankOf(r) > RankOf(best)) best = r.ToLowerInvariant();
        return best;
    }
}
