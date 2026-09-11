namespace nashira_backend.Services.Ssh;

// Spawns the Python netmiko runner as a subprocess and returns its parsed result.
// Receives already-decrypted credentials; never touches the database or tenant.
public interface ISshCommandRunner
{
    Task<SshRunOutcome> RunAsync(SshRunRequest request, CancellationToken ct);
}
