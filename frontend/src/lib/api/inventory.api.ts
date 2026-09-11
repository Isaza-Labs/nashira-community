// Inventory sources (NetBox) + sync. Reads are Viewer; source CRUD is Admin;
// running a sync is Operator (the API enforces it — the UI gates the affordances).

import { api, type ListResponse } from '$lib/api/client';

export type InventorySource = {
	id: string;
	name: string;
	kind: string;
	baseUrl: string;
	tokenSecretRef: string | null;
	siteFilter: string | null;
	allowPrivateNetwork: boolean;
	lastSyncedAt: string | null;
	createdAt: string;
	updatedAt: string;
};

export interface SourcePayload {
	name: string;
	kind: string;
	baseUrl: string;
	// A secret name or ${secret:...} reference; '' clears auth, null keeps what is
	// stored (the API masks legacy literal tokens as '***' and never returns them).
	tokenSecretRef: string | null;
	siteFilter: string;
	allowPrivateNetwork: boolean;
}

export interface SyncResult {
	created: number;
	updated: number;
	unchanged: number;
	total: number;
	dryRun: boolean;
}

interface SourceShape {
	inventory_source_id: string;
	name: string;
	kind: string;
	base_url: string;
	token_secret_ref: string | null;
	site_filter: string | null;
	allow_private_network: boolean;
	last_synced_at: string | null;
	created_at: string;
	updated_at: string;
}

interface SyncShape {
	created: number;
	updated: number;
	unchanged: number;
	total: number;
	dry_run: boolean;
}

function toSource(s: SourceShape): InventorySource {
	return {
		id: s.inventory_source_id,
		name: s.name,
		kind: s.kind,
		baseUrl: s.base_url,
		tokenSecretRef: s.token_secret_ref,
		siteFilter: s.site_filter,
		allowPrivateNetwork: s.allow_private_network,
		lastSyncedAt: s.last_synced_at,
		createdAt: s.created_at,
		updatedAt: s.updated_at
	};
}

function toBody(p: SourcePayload, includeKind = true) {
	const body: Record<string, unknown> = {
		name: p.name,
		base_url: p.baseUrl,
		site_filter: p.siteFilter,
		allow_private_network: p.allowPrivateNetwork
	};
	// Omitted (null) means "keep the stored token_secret_ref" on update.
	if (p.tokenSecretRef !== null) body.token_secret_ref = p.tokenSecretRef;
	// `kind` is create-only on the backend (UpdateInventorySource has no such field).
	if (includeKind) body.kind = p.kind || 'netbox';
	return body;
}

export async function listSources(limit = 100, offset = 0): Promise<ListResponse<InventorySource>> {
	const res = await api<ListResponse<SourceShape>>(`/inventory/sources?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toSource) };
}

export async function createSource(p: SourcePayload): Promise<InventorySource> {
	return toSource(
		await api<SourceShape>('/inventory/sources', { method: 'POST', body: JSON.stringify(toBody(p)) })
	);
}

export async function updateSource(id: string, p: SourcePayload): Promise<InventorySource> {
	return toSource(
		await api<SourceShape>(`/inventory/sources/${id}`, {
			method: 'PUT',
			body: JSON.stringify(toBody(p, false))
		})
	);
}

export async function deleteSource(id: string): Promise<void> {
	await api<SourceShape>(`/inventory/sources/${id}`, { method: 'DELETE' });
}

export async function syncSource(id: string, dryRun: boolean): Promise<SyncResult> {
	const r = await api<SyncShape>(`/inventory/sources/${id}/sync?dryRun=${dryRun}`, { method: 'POST' });
	return {
		created: r.created,
		updated: r.updated,
		unchanged: r.unchanged,
		total: r.total,
		dryRun: r.dry_run
	};
}
