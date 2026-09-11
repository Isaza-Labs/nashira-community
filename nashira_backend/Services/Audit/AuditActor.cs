namespace nashira_backend.Services.Audit;

// Who to record as the actor when nobody is signed in.
//
// Every automation path writes audit rows with UserId null, because there is no
// authenticated principal: the job worker executing a scheduled run, a webhook
// starting one, the retention sweep deleting expired reports. Those all looked
// identical in the trail — "user: null" — which makes the one question an audit log
// exists to answer unanswerable.
//
// An AsyncLocal rather than a scoped service: ICurrentUser is registered once,
// process-wide, as the HTTP-backed implementation, and swapping it per background
// scope would mean re-registering it in every one. The ambient value flows into the
// async continuations the work already runs on, and nowhere else.
//
// Usage:
//     using var _ = AuditActor.Use(AuditActor.WorkflowRunner);
//
// Nested uses restore the outer value on dispose, so a webhook that enqueues a job
// does not leave "webhook" attached to whatever the worker does afterwards.
public static class AuditActor
{
    // The identities actually bound today. Constants because they are read back as
    // filter values in the audit UI, and a typo would silently create a second,
    // near-identical actor.
    //
    // Only what is wired is declared. A list of six identities of which one is used
    // reads as coverage that exists, and the next person to look would conclude the
    // scheduler is attributed when it is not — add a constant when the call site that
    // binds it lands, not before.
    public const string WorkflowRunner = "workflow-runner";

    // The daily service-level objective sweep. Its rows are the only ones in the trail
    // that record a measurement rather than a change, which is precisely why they need
    // an actor: "who decided this was a breach" is the platform itself.
    public const string SloWatcher = "slo-watcher";

    private static readonly AsyncLocal<string?> Ambient = new();

    public static string? Current => Ambient.Value;

    public static IDisposable Use(string actor)
    {
        var previous = Ambient.Value;
        Ambient.Value = string.IsNullOrWhiteSpace(actor) ? null : actor;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
