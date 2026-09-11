// Per-user tool permissions (`/api/permissions`, Admin).
//
// A stored row is a RESTRICTION: default-allow, opt-in-deny. A target with no row falls
// back to the user's role; a row granting nothing denies that target outright; and
// `inherit` deletes the row. Those are three distinct states, and collapsing the last
// two would make "deny everything on NetBox" unsayable.

import { api } from '$lib/api/client';

export interface PermissionItem {
	toolDomain: string;
	canRead: boolean;
	canWrite: boolean;
	canExecute: boolean;
	// Write-only: removes the restriction instead of storing one.
	inherit?: boolean;
}

// Everything a permission can name: capability domains plus each registered system.
export type PermissionResourceKind = 'domain' | 'integration' | 'mcp' | 'api';

export interface PermissionResource {
	key: string;
	kind: PermissionResourceKind;
	label: string;
	description: string | null;
}

interface PermItemShape {
	tool_domain: string;
	can_read: boolean;
	can_write: boolean;
	can_execute: boolean;
}

interface ResourceShape {
	key: string;
	kind: string;
	label: string;
	description: string | null;
}

function toItem(p: PermItemShape): PermissionItem {
	return {
		toolDomain: p.tool_domain,
		canRead: p.can_read,
		canWrite: p.can_write,
		canExecute: p.can_execute
	};
}

export async function getPermissionResources(): Promise<PermissionResource[]> {
	const rows = await api<ResourceShape[]>('/permissions/domains');
	return rows.map((r) => ({
		key: r.key,
		kind: (r.kind as PermissionResourceKind) ?? 'domain',
		label: r.label,
		description: r.description
	}));
}

export async function getUserPermissions(userId: string): Promise<PermissionItem[]> {
	const rows = await api<PermItemShape[]>(`/permissions/users/${userId}`);
	return rows.map(toItem);
}

export async function setUserPermissions(
	userId: string,
	items: PermissionItem[]
): Promise<PermissionItem[]> {
	const rows = await api<PermItemShape[]>(`/permissions/users/${userId}`, {
		method: 'PUT',
		body: JSON.stringify({
			permissions: items.map((i) => ({
				tool_domain: i.toolDomain,
				can_read: i.canRead,
				can_write: i.canWrite,
				can_execute: i.canExecute,
				inherit: i.inherit ?? false
			}))
		})
	});
	return rows.map(toItem);
}
