namespace nashira_backend.Services.Trace;

// Records what the platform is doing.
//
// Two shapes, and the choice between them matters:
//
//   EventAsync  — something happened, instantaneously. One row.
//   Begin       — something started and will end. Two rows, joined by an id, the
//                 second carrying how long it took.
//
// Use Begin for anything that can hang. The `started` row with no completion is the
// entire value of the design: work that is stuck looks exactly like work that never
// began, unless somebody wrote down that it began.
public interface ITraceLogger
{
    /// <summary>
    /// One row for something that already finished. Pass <paramref name="durationMs"/>
    /// when the caller timed it — omitting it records zero, which is honest for an
    /// instantaneous event and a lie for anything that took time.
    /// </summary>
    void Event(string category, string action, object? metadata = null, string? error = null,
        int? durationMs = null);

    /// <summary>
    /// Opens a `started` row and returns the scope that closes it. Dispose without
    /// calling Complete or Fail and the operation is recorded as failed — a scope that
    /// went out of scope through an exception did not succeed, and defaulting to
    /// success would quietly turn every crash into a clean run.
    /// </summary>
    ITraceScope Begin(string category, string action, object? metadata = null);
}

public interface ITraceScope : IDisposable
{
    /// <summary>Marks success. Metadata here replaces whatever Begin was given.</summary>
    void Complete(object? metadata = null);

    void Fail(string error, object? metadata = null);
}
