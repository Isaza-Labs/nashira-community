import { api } from '$lib/api/client';

export interface NavigationVisibility {
	pageKey: string;
	visible: boolean;
}

interface NavigationVisibilityShape {
	page_key: string;
	visible: boolean;
}

function toVisibility(item: NavigationVisibilityShape): NavigationVisibility {
	return { pageKey: item.page_key, visible: item.visible };
}

export async function getMyNavigationVisibility(): Promise<NavigationVisibility[]> {
	const rows = await api<NavigationVisibilityShape[]>('/navigation-permissions/me');
	return rows.map(toVisibility);
}

export async function getRoleNavigationVisibility(role: string): Promise<NavigationVisibility[]> {
	const rows = await api<NavigationVisibilityShape[]>(
		`/navigation-permissions/roles/${encodeURIComponent(role)}`
	);
	return rows.map(toVisibility);
}

export async function getUserNavigationVisibility(userId: string): Promise<NavigationVisibility[]> {
	const rows = await api<NavigationVisibilityShape[]>(`/navigation-permissions/users/${userId}`);
	return rows.map(toVisibility);
}

async function updateVisibility(
	path: string,
	items: { pageKey: string; visible: boolean | null }[]
): Promise<NavigationVisibility[]> {
	const rows = await api<NavigationVisibilityShape[]>(path, {
		method: 'PUT',
		body: JSON.stringify({
			permissions: items.map((item) => ({
				page_key: item.pageKey,
				visible: item.visible ?? false,
				inherit: item.visible === null
			}))
		})
	});
	return rows.map(toVisibility);
}

export function updateRoleNavigationVisibility(
	role: string,
	items: { pageKey: string; visible: boolean | null }[]
): Promise<NavigationVisibility[]> {
	return updateVisibility(`/navigation-permissions/roles/${encodeURIComponent(role)}`, items);
}

export function updateUserNavigationVisibility(
	userId: string,
	items: { pageKey: string; visible: boolean | null }[]
): Promise<NavigationVisibility[]> {
	return updateVisibility(`/navigation-permissions/users/${userId}`, items);
}
