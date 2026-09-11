// Dynamic API spec CRUD (`/api/ai/specs`, Admin). The spec content (OpenAPI) is
// only returned by the detail endpoint.

import { api, type ListResponse } from '$lib/api/client';

export const SPEC_AUTH_TYPES = [
	{ value: 'none', label: 'No authentication / inherit integration' },
	{ value: 'token', label: 'Token authorization header' },
	{ value: 'bearer', label: 'Bearer token' },
	{ value: 'basic', label: 'Basic auth' },
	{ value: 'header', label: 'Custom header / API key' }
];

export type Spec = {
	id: string;
	api: string;
	operationCount: number;
	baseUrl: string | null;
	authType: string;
	verifySsl: boolean;
	// SSRF guard override for this spec's own base URL. When the spec has no
	// base URL the linked integration's flag governs instead.
	allowPrivateNetwork: boolean;
	// Null = global spec, reusable with no automatic credential pairing.
	integrationId: string | null;
	createdAt: string;
	updatedAt: string;
};

export type SpecDetail = Spec & { content: string };

export interface NewSpec {
	api: string;
	content: string;
	baseUrl: string;
	authType: string;
	verifySsl: boolean;
	allowPrivateNetwork: boolean;
	integrationId: string;
}

export type EditSpec = Omit<NewSpec, 'api'>;

interface SpecShape {
	ai_api_spec_id: string;
	api: string;
	operation_count: number;
	base_url: string | null;
	auth_type: string;
	verify_ssl: boolean;
	allow_private_network: boolean;
	integration_id: string | null;
	created_at: string;
	updated_at: string;
}

interface SpecDetailShape extends SpecShape {
	content: string;
}

function toSpec(s: SpecShape): Spec {
	return {
		id: s.ai_api_spec_id,
		api: s.api,
		operationCount: s.operation_count,
		baseUrl: s.base_url,
		authType: s.auth_type,
		verifySsl: s.verify_ssl,
		allowPrivateNetwork: s.allow_private_network,
		integrationId: s.integration_id,
		createdAt: s.created_at,
		updatedAt: s.updated_at
	};
}

// Which rows a catalog page asks for.
//
// A skill or spec bound to an integration belongs to that integration's screen, not
// here: mixed into the global list it is noise, and worse, it invites editing the
// scoped copy from a page that says nothing about which system it applies to. So the
// default is `global` and the scoped rows are reached from the integration itself
// (or via the ?integration= deep link, which is what that page's filter chip uses).
export type CatalogScope = { integrationId?: string | null };

function scopeQuery(scope?: CatalogScope): string {
	return scope?.integrationId
		? `&integrationId=${encodeURIComponent(scope.integrationId)}`
		: '&globalOnly=true';
}

export async function listSpecs(
	limit = 100,
	offset = 0,
	scope?: CatalogScope
): Promise<ListResponse<Spec>> {
	const res = await api<ListResponse<SpecShape>>(
		`/ai/specs?limit=${limit}&offset=${offset}${scopeQuery(scope)}`
	);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toSpec) };
}

export async function getSpec(id: string): Promise<SpecDetail> {
	const s = await api<SpecDetailShape>(`/ai/specs/${id}`);
	return { ...toSpec(s), content: s.content };
}

export async function createSpec(p: NewSpec): Promise<Spec> {
	return toSpec(
		await api<SpecShape>('/ai/specs', {
			method: 'POST',
			body: JSON.stringify({
				api: p.api,
				content: p.content,
				base_url: p.baseUrl || null,
				auth_type: p.authType || null,
				verify_ssl: p.verifySsl,
				allow_private_network: p.allowPrivateNetwork,
				integration_id: p.integrationId || null
			})
		})
	);
}

export async function updateSpec(id: string, p: EditSpec): Promise<Spec> {
	return toSpec(
		await api<SpecShape>(`/ai/specs/${id}`, {
			method: 'PUT',
			body: JSON.stringify({
				content: p.content,
				base_url: p.baseUrl || null,
				auth_type: p.authType || null,
				verify_ssl: p.verifySsl,
				allow_private_network: p.allowPrivateNetwork,
				// See skills.api.ts: an empty box means unlink, and that needs its
				// own flag on a partial-update DTO.
				integration_id: p.integrationId || null,
				clear_integration: !p.integrationId
			})
		})
	);
}

export async function deleteSpec(id: string): Promise<void> {
	await api<SpecShape>(`/ai/specs/${id}`, { method: 'DELETE' });
}
