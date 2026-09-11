// The models a chat turn may name (`GET /api/ai/models`, Viewer), flattened
// across the tenant's enabled providers. This is what fills the chat's model
// selector; provider CRUD lives in providers.api.ts and is Admin-only.
//
// One entry per (provider, model): a provider contributes its default model plus
// whatever `config.models` lists. No secrets — ids and names only.

import { api } from '$lib/api/client';

export interface AiModel {
	/** The model id sent to the vendor, e.g. `claude-sonnet-5`. */
	id: string;
	/** The provider row's display name, e.g. "Anthropic (prod)". */
	provider: string;
	providerId: string;
	/** `openai` | `anthropic` | `gemini` | `ollama` | `deepseek` | `kimi` | `custom`. */
	providerType: string;
	/** True for the provider's own default model. */
	isDefault: boolean;
}

interface AiModelShape {
	id: string;
	provider: string;
	ai_provider_id: string;
	provider_type: string;
	is_default: boolean;
}

export async function listModels(): Promise<AiModel[]> {
	const rows = await api<AiModelShape[]>('/ai/models');
	return rows.map((m) => ({
		id: m.id,
		provider: m.provider,
		providerId: m.ai_provider_id,
		providerType: m.provider_type,
		isDefault: m.is_default
	}));
}

/**
 * The entry a (providerId, model) pair points at, or undefined when the pair is
 * gone — a provider an admin disabled since the conversation last ran, or a
 * model dropped from `config.models`. Callers show the raw name rather than
 * pretending the selection is invalid.
 */
export function findModel(
	models: AiModel[],
	providerId: string | null,
	model: string | null
): AiModel | undefined {
	if (!providerId || !model) return undefined;
	return models.find((m) => m.providerId === providerId && m.id === model);
}
