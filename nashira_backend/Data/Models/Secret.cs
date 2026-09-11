namespace nashira_backend.Data.Models;

// Named secret consumed by the REST spec executor, the integration layer and the
// agent's tools. Values are encrypted at rest with the same Data Protection
// keyring used for device credentials — the admin UI never returns plaintext
// after creation; rotation means writing a new value.
//
// Secrets resolve via
//   ${secret:<source>:<id|name>:<field>}
// where <source> = "secret" resolves to this table by Name, and the other
// sources (credential, ai_provider, integration, session) plug into
// SecretResolver without needing a row here. Name is the lookup key used in
// spec and integration templates, so uniqueness is enforced by index.
public class Secret : BaseModel
{
    public Guid SecretId { get; set; }

    // Lookup key used by `${secret:secret:<name>:value}`. Human-readable,
    // lowercase, unique.
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Ciphertext of the secret payload. Raw bytes so the encryption layer can
    // pick its own envelope format.
    public byte[] EncryptedValue { get; set; } = Array.Empty<byte>();

    // Who created/last-touched the row. Useful for audit trails when a workflow
    // breaks because a rotated secret expired.
    public string? CreatedBy { get; set; }
}
