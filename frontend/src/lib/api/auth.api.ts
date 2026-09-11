// Auth-specific API calls, mapped to nashira's AuthController (/api/auth).

import { api } from '$lib/api/client';
import { authStore, sessionFromResponse, type LoginResponseShape } from '$lib/stores/auth.svelte';

export interface MeResponse {
	user_id: string;
	username: string;
	email: string;
	role: string;
	password_changed_at: string;
}

// POST /api/auth/login (anonymous). Sets the session on success.
export async function login(username: string, password: string): Promise<void> {
	const body = await api<LoginResponseShape>('/auth/login', {
		method: 'POST',
		body: JSON.stringify({ username, password })
	});
	authStore.set(sessionFromResponse(body));
}

// POST /api/auth/logout — best-effort revoke of the refresh token; always clears
// the local session so the user ends up logged out regardless.
export async function logout(): Promise<void> {
	const token = authStore.refreshToken;
	try {
		if (token) {
			await api<unknown>('/auth/logout', {
				method: 'POST',
				body: JSON.stringify({ refresh_token: token })
			});
		}
	} catch {
		// ignore — logging out locally is what matters
	} finally {
		authStore.clear();
	}
}

// GET /api/auth/me — full identity (adds email + password_changed_at beyond what
// the login response carries).
export function me(): Promise<MeResponse> {
	return api<MeResponse>('/auth/me');
}

// POST /api/auth/change-password.
export function changePassword(currentPassword: string, newPassword: string): Promise<unknown> {
	return api<unknown>('/auth/change-password', {
		method: 'POST',
		body: JSON.stringify({ current_password: currentPassword, new_password: newPassword })
	});
}
