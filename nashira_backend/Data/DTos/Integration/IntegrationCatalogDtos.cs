using System.Text.Json.Serialization;
using nashira_backend.Data.DTos.AiApiSpec;
using nashira_backend.Data.DTos.Skill;

namespace nashira_backend.Data.DTos.Integration;

// Attach or replace one OpenAPI spec on an existing integration.
//
// Deliberately not the full CreateAiApiSpec shape: base_url, auth_type and the rest
// come from the integration this spec is being attached to. Accepting them here
// would let a spec silently override the credentials of the system it claims to
// belong to, which is the exact confusion the integration link exists to remove.
public class AttachSpecRequest
{
    [JsonPropertyName("api")] public string Api { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;

    // Take over a spec that currently belongs to nobody. Without it, an api name
    // already used by a global spec is refused — re-scoping one changes where its
    // calls authenticate, and that is not something to do because a name collided.
    [JsonPropertyName("adopt")] public bool? Adopt { get; set; }
}

// Attach or replace one prompt skill on an existing integration.
public class AttachSkillRequest
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("priority")] public int? Priority { get; set; }

    // Take over a global skill of the same name instead of refusing. Explicit,
    // because that skill is already merged into every conversation's prompt and
    // scoping it changes what the agent knows in contexts nobody was looking at.
    [JsonPropertyName("adopt")] public bool? Adopt { get; set; }
}

// Everything scoped to one integration, so the integrations screen can show what an
// upload actually produced without three round trips to three different endpoints.
public class IntegrationBundleView
{
    [JsonPropertyName("skills")] public List<AiPromptSkillResponse> Skills { get; set; } = [];
    [JsonPropertyName("specs")] public List<AiApiSpecResponse> Specs { get; set; } = [];
}

public class AttachSpecResult
{
    [JsonPropertyName("spec")] public AiApiSpecResponse Spec { get; set; } = new();

    // What the upload did to the action catalogue. Reported because "the spec saved"
    // and "the actions changed" are different outcomes, and an admin uploading a spec
    // is almost always after the second one.
    [JsonPropertyName("actions_created")] public int ActionsCreated { get; set; }
    [JsonPropertyName("actions_updated")] public int ActionsUpdated { get; set; }
    [JsonPropertyName("actions_unchanged")] public int ActionsUnchanged { get; set; }
    [JsonPropertyName("actions_disappeared")] public int ActionsDisappeared { get; set; }
}

// One skill staged alongside a new integration.
public class BundledSkill
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    [JsonPropertyName("priority")] public int? Priority { get; set; }
}

// One spec staged alongside a new integration. No base_url or auth: it inherits the
// integration's, which is the reason for bundling them in the first place.
public class BundledSpec
{
    [JsonPropertyName("api")] public string Api { get; set; } = string.Empty;
    [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
}

// Create an integration and everything that belongs to it in one call.
//
// Attaching after the fact works and is what the /specs and /skills paths are for.
// But an integration is rarely useful on its own — it is a base URL until a spec
// gives it operations — and creating it in three steps means every failure leaves a
// half-configured system behind that someone has to notice and clean up. Here the
// whole thing lands or none of it does.
public class CreateIntegrationBundle
{
    [JsonPropertyName("integration")] public CreateIntegration Integration { get; set; } = new();
    [JsonPropertyName("skills")] public List<BundledSkill> Skills { get; set; } = [];
    [JsonPropertyName("specs")] public List<BundledSpec> Specs { get; set; } = [];
}

public class CreateIntegrationBundleResult
{
    [JsonPropertyName("integration")] public IntegrationResponse Integration { get; set; } = new();
    [JsonPropertyName("skills_created")] public int SkillsCreated { get; set; }
    [JsonPropertyName("specs_created")] public int SpecsCreated { get; set; }

    // Materialised from every staged spec together, which is why this is one number
    // rather than one per spec.
    [JsonPropertyName("actions_created")] public int ActionsCreated { get; set; }
}
