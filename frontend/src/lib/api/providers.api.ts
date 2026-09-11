// AI provider CRUD (`/api/ai/providers`, Admin). The API key is write-only
// (never returned; `hasApiKey` reflects whether one is stored).

import { api, type ListResponse } from '$lib/api/client';

export type Provider = {
	id: string;
	name: string;
	type: string;
	baseUrl: string | null;
	defaultModel: string;
	hasApiKey: boolean;
	enabled: boolean;
	/** Free-form provider settings (`models`, `model_limits`, …), as stored. */
	config: Record<string, unknown>;
	createdAt: string;
	updatedAt: string;
};

export interface NewProvider {
	name: string;
	type: string;
	baseUrl: string;
	apiKey: string;
	defaultModel: string;
	enabled: boolean;
	config: Record<string, unknown>;
}

export interface EditProvider {
	name: string;
	baseUrl: string;
	apiKey: string;
	defaultModel: string;
	enabled: boolean;
	config: Record<string, unknown>;
}

interface ProviderShape {
	ai_provider_id: string;
	name: string;
	type: string;
	base_url: string | null;
	default_model: string;
	has_api_key: boolean;
	enabled: boolean;
	config: Record<string, unknown> | null;
	created_at: string;
	updated_at: string;
}

function toProvider(p: ProviderShape): Provider {
	return {
		id: p.ai_provider_id,
		name: p.name,
		type: p.type,
		baseUrl: p.base_url,
		defaultModel: p.default_model,
		hasApiKey: p.has_api_key,
		enabled: p.enabled,
		config: p.config ?? {},
		createdAt: p.created_at,
		updatedAt: p.updated_at
	};
}

export async function listProviders(limit = 100, offset = 0): Promise<ListResponse<Provider>> {
	const res = await api<ListResponse<ProviderShape>>(`/ai/providers?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toProvider) };
}

export async function createProvider(p: NewProvider): Promise<Provider> {
	return toProvider(
		await api<ProviderShape>('/ai/providers', {
			method: 'POST',
			body: JSON.stringify({
				name: p.name,
				type: p.type,
				base_url: p.baseUrl || null,
				api_key: p.apiKey || null,
				default_model: p.defaultModel,
				enabled: p.enabled,
				config: p.config
			})
		})
	);
}

export async function updateProvider(id: string, p: EditProvider): Promise<Provider> {
	const body: Record<string, unknown> = {
		name: p.name,
		base_url: p.baseUrl || null,
		default_model: p.defaultModel,
		enabled: p.enabled,
		// Always sent: the whole object is replaced, so omitting it would be the
		// only way to keep the stored one and there is no UI for "leave as is".
		config: p.config
	};
	// Only send the key when the user entered one; blank keeps the stored key.
	if (p.apiKey) body.api_key = p.apiKey;
	return toProvider(await api<ProviderShape>(`/ai/providers/${id}`, { method: 'PUT', body: JSON.stringify(body) }));
}

export async function deleteProvider(id: string): Promise<void> {
	await api<ProviderShape>(`/ai/providers/${id}`, { method: 'DELETE' });
}
