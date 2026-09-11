// Named secret store (`/api/secrets`, Admin). Values are write-only — the API returns
// metadata plus a `hasValue` flag, never the plaintext. Rotation means writing a new
// value under the same name, so every `${secret:secret:<name>:value}` reference in a
// spec or an integration keeps resolving.

import { api, apiBlob } from '$lib/api/client';

// A type alias rather than an interface: DataTable's row generic is constrained to
// Record<string, unknown>, which only an alias satisfies implicitly.
export type Secret = {
	id: string;
	name: string;
	description: string | null;
	hasValue: boolean;
	createdBy: string | null;
	createdAt: string;
	updatedAt: string;
};

interface SecretShape {
	secret_id: string;
	name: string;
	description: string | null;
	has_value: boolean;
	created_by: string | null;
	created_at: string;
	updated_at: string;
}

function toSecret(s: SecretShape): Secret {
	return {
		id: s.secret_id,
		name: s.name,
		description: s.description,
		hasValue: s.has_value,
		createdBy: s.created_by,
		createdAt: s.created_at,
		updatedAt: s.updated_at
	};
}

export interface CreateSecret {
	name: string;
	description?: string;
	value: string;
}

export interface UpdateSecret {
	description?: string;
	// Omitted entirely to leave the stored ciphertext alone — that is what makes
	// "edit the description" safe. Sending an empty string is rejected by the API.
	value?: string;
}

// The reference an admin pastes into a spec or an integration's auth config.
export function secretRef(name: string): string {
	return `\${secret:secret:${name}:value}`;
}

export async function listSecrets(): Promise<Secret[]> {
	const rows = await api<SecretShape[]>('/secrets');
	return rows.map(toSecret);
}

export async function getSecret(id: string): Promise<Secret> {
	return toSecret(await api<SecretShape>(`/secrets/${id}`));
}

export async function createSecret(p: CreateSecret): Promise<Secret> {
	const body: Record<string, unknown> = { name: p.name, value: p.value };
	if (p.description) body.description = p.description;
	return toSecret(await api<SecretShape>('/secrets', { method: 'POST', body: JSON.stringify(body) }));
}

export async function updateSecret(id: string, p: UpdateSecret): Promise<Secret> {
	const body: Record<string, unknown> = {};
	if (p.description !== undefined) body.description = p.description;
	if (p.value !== undefined) body.value = p.value;
	return toSecret(await api<SecretShape>(`/secrets/${id}`, { method: 'PUT', body: JSON.stringify(body) }));
}

export async function deleteSecret(id: string): Promise<void> {
	// DELETE returns 204; use apiBlob to consume the empty body without JSON parse.
	await apiBlob(`/secrets/${id}`, { method: 'DELETE' });
}
