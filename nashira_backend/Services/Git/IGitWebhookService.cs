using nashira_backend.Data.DTos;
using nashira_backend.Data.DTos.Git;

namespace nashira_backend.Services.Git;

// CRUD over the inbound webhooks attached to a registered repository. The receiving
// end (POST /api/git/hooks/{route}) is deliberately not here: it runs anonymously,
// and keeping it out of this interface means no authenticated caller can reach the
// dispatch path by accident.
public interface IGitWebhookService
{
    Task<ListResponse<GitWebhookResponse>> ListAsync(Guid repositoryId, CancellationToken ct);
    Task<GitWebhookResponse> GetAsync(Guid repositoryId, Guid webhookId, CancellationToken ct);
    Task<GitWebhookResponse> CreateAsync(Guid repositoryId, CreateGitWebhook dto, CancellationToken ct);
    Task<GitWebhookResponse> UpdateAsync(Guid repositoryId, Guid webhookId, UpdateGitWebhook dto, CancellationToken ct);
    Task<GitWebhookResponse> DeleteAsync(Guid repositoryId, Guid webhookId, CancellationToken ct);

    // Issues a new secret and returns it once. The previous one stops working
    // immediately; a rotation that kept both valid would not be a rotation.
    Task<GitWebhookResponse> RotateSecretAsync(Guid repositoryId, Guid webhookId, CancellationToken ct);

    Task<ListResponse<GitWebhookDeliveryResponse>> ListDeliveriesAsync(
        Guid repositoryId, Guid webhookId, int limit, CancellationToken ct);

    // Answers "would a push to this branch do anything" without a push. The filter and
    // the enabled flags are the parts operators get wrong, and finding out from a
    // silent no-op hours later is how a webhook gets declared broken when it is only
    // configured for a different branch.
    Task<GitWebhookTestResponse> DryRunAsync(
        Guid repositoryId, Guid webhookId, string? branch, CancellationToken ct);
}
