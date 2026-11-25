namespace nashira_backend.Services.Ai.Secrets;

// Execution-local provenance, not a list of plaintext values or encodings. A
// handler given any resolved material cannot safely declassify its own output.
// AsyncLocal isolates concurrent executions; nested scopes taint their parents.
public sealed class SecretResolutionScope : IDisposable
{
    private static readonly AsyncLocal<SecretResolutionScope?> Current = new();
    private readonly SecretResolutionScope? _parent;
    public bool HasResolvedMaterial { get; private set; }

    public SecretResolutionScope()
    {
        _parent = Current.Value;
        Current.Value = this;
    }

    public static void MarkResolved()
    {
        for (var scope = Current.Value; scope is not null; scope = scope._parent)
            scope.HasResolvedMaterial = true;
    }

    public void Dispose() => Current.Value = _parent;
}
