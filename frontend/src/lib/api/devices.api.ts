// Device inventory CRUD. Reads are Viewer; create/update/delete are Operator (the
// API enforces it — the UI gates the affordances). snake_case DTOs mapped to
// idiomatic types; `Device` is a type alias so it satisfies DataTable's
// `T extends Record<string, unknown>` row constraint.

import { api, type ListResponse } from '$lib/api/client';

export type Device = {
	id: string;
	name: string;
	ipAddress: string;
	platform: string;
	vendor: string;
	osVersion: string;
	site: string;
	role: string;
	status: string;
	credentialId: string | null;
	// Provenance. Set on rows an inventory sync produced; both null when the device
	// was registered by hand.
	sourceId: string | null;
	externalId: string | null;
	lastSyncAt: string | null;
	// Free-form attributes carried over from the source (custom fields, tags, serial).
	properties: Record<string, unknown>;
	// Which promotion stage may dispatch a workflow run to this device.
	allowDraft: boolean;
	allowQa: boolean;
	allowProduction: boolean;
	hasHostKeyFingerprint: boolean;
	createdAt: string;
	updatedAt: string;
};

// Editable fields surfaced by the form. Provenance (source/external id/last sync) is
// written by the sync and deliberately read-only here — editing it by hand would
// break the idempotency the sync relies on.
export interface DevicePayload {
	name: string;
	ipAddress: string;
	platform: string;
	vendor: string;
	osVersion: string;
	site: string;
	role: string;
	status: string;
	credentialId: string;
	allowDraft: boolean;
	allowQa: boolean;
	allowProduction: boolean;
	fingerprint: string;
}

interface DeviceShape {
	device_id: string;
	device_name: string;
	ip_address: string;
	platform: string;
	vendor: string;
	os_version: string;
	site: string;
	role: string;
	status: string;
	credential_id: string | null;
	source_id: string | null;
	external_id: string | null;
	last_sync_at: string | null;
	properties: Record<string, unknown> | null;
	allow_draft: boolean;
	allow_qa: boolean;
	allow_production: boolean;
	has_host_key_fingerprint: boolean;
	created_at: string;
	updated_at: string;
}

function toDevice(d: DeviceShape): Device {
	return {
		id: d.device_id,
		name: d.device_name,
		ipAddress: d.ip_address,
		platform: d.platform,
		vendor: d.vendor,
		osVersion: d.os_version,
		site: d.site,
		role: d.role,
		status: d.status,
		credentialId: d.credential_id,
		sourceId: d.source_id,
		externalId: d.external_id,
		lastSyncAt: d.last_sync_at,
		properties: d.properties ?? {},
		allowDraft: d.allow_draft,
		allowQa: d.allow_qa,
		allowProduction: d.allow_production,
		hasHostKeyFingerprint: d.has_host_key_fingerprint,
		createdAt: d.created_at,
		updatedAt: d.updated_at
	};
}

function toBody(p: DevicePayload) {
	return {
		device_name: p.name,
		ip_address: p.ipAddress,
		platform: p.platform,
		vendor: p.vendor,
		os_version: p.osVersion,
		site: p.site,
		role: p.role,
		status: p.status,
		// null (empty) keeps the existing value on update.
		credential_id: p.credentialId || null,
		allow_draft: p.allowDraft,
		allow_qa: p.allowQa,
		allow_production: p.allowProduction,
		expected_ssh_host_key_fingerprint: p.fingerprint || null
	};
}

export async function listDevices(limit = 100, offset = 0, q?: string): Promise<ListResponse<Device>> {
	const params = new URLSearchParams({ limit: String(limit), offset: String(offset) });
	if (q) params.set('q', q);
	const res = await api<ListResponse<DeviceShape>>(`/device?${params}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toDevice) };
}

// Whole-inventory aggregates for dashboards. `listDevices` pages (the server caps a
// page at 200), so counting its items undercounts any inventory bigger than a page.
export type DeviceStats = { total: number; byStatus: Record<string, number> };

export async function deviceStats(): Promise<DeviceStats> {
	const res = await api<{ total: number; by_status: Record<string, number> }>('/device/stats');
	return { total: res.total, byStatus: res.by_status };
}

export async function createDevice(p: DevicePayload): Promise<Device> {
	return toDevice(await api<DeviceShape>('/device', { method: 'POST', body: JSON.stringify(toBody(p)) }));
}

export async function updateDevice(id: string, p: DevicePayload): Promise<Device> {
	return toDevice(await api<DeviceShape>(`/device/${id}`, { method: 'PUT', body: JSON.stringify(toBody(p)) }));
}

export async function deleteDevice(id: string): Promise<void> {
	await api<DeviceShape>(`/device/${id}`, { method: 'DELETE' });
}
