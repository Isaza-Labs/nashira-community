namespace nashira_backend.Services.Ai.Secrets;

// Replaces ${secret:<source>:<id|name>:<field>} placeholders with plaintext pulled
// from the encrypted store. Callers only pass the template; the resolver owns the
// lookups and the decryption. Used by the REST executor and the integration layer
// right before hitting the wire, so plaintext only lives inside one request.
//
// Supported source shapes:
//   ${secret:secret:<name|id>:value}                → Secret.EncryptedValue
//   ${secret:credential:<name|id>:username}         → Credential.Username (plaintext)
//   ${secret:credential:<name|id>:password}         → Credential.EncryptedPassword
//   ${secret:credential:<name|id>:private_key}      → Credential.EncryptedPrivateKey
//   ${secret:credential:<name|id>:passphrase}       → Credential.EncryptedKeyPassphrase
//   ${secret:credential:<name|id>:token}            → Credential.EncryptedToken
//   ${secret:credential:<name|id>:client_secret}    → Credential.EncryptedClientSecret
//   ${secret:ai_provider:<name|id>:api_key}         → AIProvider.EncryptedApiKey
//   ${secret:integration:<name|id>:<path.in.auth>}  → Integration.AuthConfig dotted path
//   ${secret:session:current:jwt}                   → the raw JWT of the request that
//                                                     invoked the agent, so a spec
//                                                     pointed at this backend runs
//                                                     under the caller's permissions.
//
// Id-vs-name resolution is attempted in that order: a parseable Guid is looked up by
// id, anything else falls through to an exact name match.
public interface ISecretResolver
{
    // Non-templated lookup: the raw value for a single reference, or null when it
    // cannot be resolved. Used by callers that know up-front what they need (an admin
    // testing a connection, say). The session source is never reachable from here —
    // it is a per-request credential, not something an admin stored.
    Task<string?> ResolveAsync(string source, string idOrName, string field, CancellationToken ct);

    // Substitutes every ${secret:...} occurrence in `template`. Unresolved references
    // are left literal so the operator sees the original marker in debug output
    // instead of a silent empty string.
    Task<string> SubstituteAsync(string template, CancellationToken ct);

    // Same substitution, but with the session source (${secret:session:current:jwt})
    // switched on. That marker resolves to the *calling user's own bearer*, not to
    // anything an admin stored, so it is off by default and every existing call site
    // keeps behaving exactly as it did. Only the REST executor turns it on, and only
    // for requests it has already established are aimed at this backend — see the
    // comment on SecretResolver.ResolveSession for why that matters.
    Task<string> SubstituteAsync(string template, bool allowSessionRefs, CancellationToken ct);
}
