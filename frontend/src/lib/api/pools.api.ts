// Device pools (`/api/device-pools`). A pool groups devices by explicit members
// plus optional rules; its allow trio applies on top of each member's.

import { api, type ListResponse } from '$lib/api/client';

export type DevicePool = {
	id: string;
	name: string;
	slug: string;
	description: string | null;
	filterRules: string | null;
	staticMembers: string[];
	allowDraft: boolean;
	allowQa: boolean;
	allowProduction: boolean;
	memberCount: number;
	createdAt: string;
	updatedAt: string;
};

export interface DevicePoolPayload {
	name: string;
	description: string;
	filterRules: string;
	staticMembers: string[];
	allowDraft: boolean;
	allowQa: boolean;
	allowProduction: boolean;
}

export type PoolMember = { deviceId: string; name: string; ip: string };

export type PoolResolution = {
	environment: string;
	poolAllowsEnvironment: boolean;
	members: PoolMember[];
	count: number;
	excluded: string[];
};

interface PoolShape {
	device_pool_id: string;
	name: string;
	slug: string;
	description: string | null;
	filter_rules: string | null;
	static_members: string[];
	allow_draft: boolean;
	allow_qa: boolean;
	allow_production: boolean;
	member_count: number;
	created_at: string;
	updated_at: string;
}

function toPool(p: PoolShape): DevicePool {
	return {
		id: p.device_pool_id,
		name: p.name,
		slug: p.slug,
		description: p.description,
		filterRules: p.filter_rules,
		staticMembers: p.static_members ?? [],
		allowDraft: p.allow_draft,
		allowQa: p.allow_qa,
		allowProduction: p.allow_production,
		memberCount: p.member_count,
		createdAt: p.created_at,
		updatedAt: p.updated_at
	};
}

function toBody(p: DevicePoolPayload) {
	return {
		name: p.name,
		description: p.description || null,
		filter_rules: p.filterRules.trim() || null,
		static_members: p.staticMembers,
		allow_draft: p.allowDraft,
		allow_qa: p.allowQa,
		allow_production: p.allowProduction
	};
}

export async function listPools(limit = 100, offset = 0): Promise<ListResponse<DevicePool>> {
	const res = await api<ListResponse<PoolShape>>(`/device-pools?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toPool) };
}

export async function createPool(p: DevicePoolPayload): Promise<DevicePool> {
	return toPool(await api<PoolShape>('/device-pools', { method: 'POST', body: JSON.stringify(toBody(p)) }));
}

export async function updatePool(id: string, p: DevicePoolPayload): Promise<DevicePool> {
	return toPool(
		await api<PoolShape>(`/device-pools/${id}`, { method: 'PUT', body: JSON.stringify(toBody(p)) })
	);
}

export async function deletePool(id: string): Promise<void> {
	await api<PoolShape>(`/device-pools/${id}`, { method: 'DELETE' });
}

// Resolved membership. Without an environment it is the raw list; with one, it
// also reports who the environment excludes and why.
export async function resolveMembers(id: string, environment?: string): Promise<PoolResolution> {
	const qs = environment ? `?environment=${encodeURIComponent(environment)}` : '';
	const r = await api<{
		environment?: string;
		pool_allows_environment?: boolean;
		members: { device_id: string; name: string; ip: string }[];
		count: number;
		excluded?: string[];
	}>(`/device-pools/${id}/members${qs}`);

	return {
		environment: r.environment ?? '',
		poolAllowsEnvironment: r.pool_allows_environment ?? true,
		members: r.members.map((m) => ({ deviceId: m.device_id, name: m.name, ip: m.ip })),
		count: r.count,
		excluded: r.excluded ?? []
	};
}
