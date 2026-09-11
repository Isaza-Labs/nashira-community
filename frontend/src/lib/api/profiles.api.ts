// Assistant profile CRUD (`/api/profiles`, read Viewer / write Admin) and the
// current user's own assignment (`/api/profiles/users/me/profile`, Viewer).

import { api, type ListResponse } from '$lib/api/client';

export type Profile = {
	id: string;
	name: string;
	displayName: string;
	description: string | null;
	skills: string[];
	responseStyle: string | null;
	displayOrder: number;
	createdAt: string;
	updatedAt: string;
};

export interface NewProfile {
	name: string;
	displayName: string;
	description: string;
	skills: string[];
	responseStyle: string;
	displayOrder: number;
}

export type EditProfile = Omit<NewProfile, 'name'>;

interface ProfileShape {
	profile_id: string;
	name: string;
	display_name: string;
	description: string | null;
	skills: string[];
	response_style: string | null;
	display_order: number;
	created_at: string;
	updated_at: string;
}

function toProfile(p: ProfileShape): Profile {
	return {
		id: p.profile_id,
		name: p.name,
		displayName: p.display_name,
		description: p.description,
		skills: p.skills ?? [],
		responseStyle: p.response_style,
		displayOrder: p.display_order,
		createdAt: p.created_at,
		updatedAt: p.updated_at
	};
}

function body(p: EditProfile) {
	return {
		display_name: p.displayName,
		description: p.description || null,
		skills: p.skills,
		response_style: p.responseStyle || null,
		display_order: p.displayOrder
	};
}

export async function listProfiles(limit = 100, offset = 0): Promise<ListResponse<Profile>> {
	const res = await api<ListResponse<ProfileShape>>(`/profiles?limit=${limit}&offset=${offset}`);
	return { total: res.total, limit: res.limit, offset: res.offset, items: res.items.map(toProfile) };
}

export async function createProfile(p: NewProfile): Promise<Profile> {
	return toProfile(
		await api<ProfileShape>('/profiles', { method: 'POST', body: JSON.stringify({ name: p.name, ...body(p) }) })
	);
}

export async function updateProfile(id: string, p: EditProfile): Promise<Profile> {
	return toProfile(await api<ProfileShape>(`/profiles/${id}`, { method: 'PUT', body: JSON.stringify(body(p)) }));
}

export async function deleteProfile(id: string): Promise<void> {
	await api<ProfileShape>(`/profiles/${id}`, { method: 'DELETE' });
}

// ── the current user's own assignment ──────────────────────────────────────
// What the agent reads for this user on every turn: the assigned profile's
// skills + response style, plus their own free text. Same cap as the backend.

export const MAX_CUSTOM_PROFILE_TEXT = 500;

export type MyProfile = {
	profileId: string | null;
	customProfileText: string | null;
};

interface MyProfileShape {
	profile_id: string | null;
	custom_profile_text: string | null;
}

function toMyProfile(m: MyProfileShape): MyProfile {
	return { profileId: m.profile_id ?? null, customProfileText: m.custom_profile_text ?? null };
}

export async function getMyProfile(): Promise<MyProfile> {
	return toMyProfile(await api<MyProfileShape>('/profiles/users/me/profile'));
}

export async function setMyProfile(p: MyProfile): Promise<MyProfile> {
	return toMyProfile(
		await api<MyProfileShape>('/profiles/users/me/profile', {
			method: 'PUT',
			body: JSON.stringify({
				profile_id: p.profileId || null,
				custom_profile_text: p.customProfileText?.trim() || null
			})
		})
	);
}

// ── another user's assignment (Admin) ──────────────────────────────────────
export async function getUserProfile(userId: string): Promise<MyProfile> {
	return toMyProfile(await api<MyProfileShape>(`/profiles/users/${userId}/profile`));
}

export async function setUserProfile(userId: string, p: MyProfile): Promise<MyProfile> {
	return toMyProfile(
		await api<MyProfileShape>(`/profiles/users/${userId}/profile`, {
			method: 'PUT',
			body: JSON.stringify({
				profile_id: p.profileId || null,
				custom_profile_text: p.customProfileText?.trim() || null
			})
		})
	);
}
