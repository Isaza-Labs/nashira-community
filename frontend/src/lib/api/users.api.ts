// Admin user CRUD (`/api/users`, Admin). `User` is a `type` alias for DataTable.

import { api, type ListResponse } from '$lib/api/client';

export type User = {
	id: string;
	username: string;
	email: string;
	role: string;
	isActive: boolean;
	locked: boolean;
	profileId: string | null;
	createdAt: string;
	updatedAt: string;
};

export interface NewUser {
	username: string;
	email: string;
	password: string;
	role: string;
}

export interface EditUser {
	email: string;
	role: string;
	isActive: boolean;
	password?: string;
}

interface UserShape {
	user_id: string;
	username: string;
	email: string;
	role: string;
	is_active: boolean;
	locked: boolean;
	profile_id: string | null;
	password_changed_at: string;
	created_at: string;
	updated_at: string;
	// Present only on create: whether the credentials email went out, and the
	// admin-facing warning when it did not.
	credentials_email_sent?: boolean;
	warning?: string | null;
}

function toUser(u: UserShape): User {
	return {
		id: u.user_id,
		username: u.username,
		email: u.email,
		role: u.role,
		isActive: u.is_active,
		locked: u.locked,
		profileId: u.profile_id ?? null,
		createdAt: u.created_at,
		updatedAt: u.updated_at
	};
}

export async function listUsers(limit = 100, offset = 0): Promise<ListResponse<User>> {
	const res = await api<ListResponse<UserShape>>(`/users?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toUser) };
}

export async function createUser(p: NewUser): Promise<{ user: User; warning: string | null }> {
	const res = await api<UserShape>('/users', {
		method: 'POST',
		body: JSON.stringify({ username: p.username, email: p.email, password: p.password, role: p.role })
	});
	return { user: toUser(res), warning: res.warning ?? null };
}

export async function updateUser(id: string, p: EditUser): Promise<User> {
	const body: Record<string, unknown> = { email: p.email, role: p.role, is_active: p.isActive };
	if (p.password) body.password = p.password;
	return toUser(await api<UserShape>(`/users/${id}`, { method: 'PUT', body: JSON.stringify(body) }));
}

export async function deleteUser(id: string): Promise<void> {
	await api<UserShape>(`/users/${id}`, { method: 'DELETE' });
}
