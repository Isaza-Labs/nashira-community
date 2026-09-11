// Role helpers mirroring the backend authorization policies:
//   Viewer   = any authenticated user
//   Operator = admin or operator (write actions)
//   Admin    = admin (admin area)
// The API remains the source of truth; these drive UI affordances (route guards,
// hiding write buttons). For reactive gating in components, prefer the RoleGate
// component or read `authStore.session?.role` inside a $derived.

import { authStore } from '$lib/stores/auth.svelte';

export type Role = 'admin' | 'operator' | 'viewer';

export function currentRole(): string | null {
	return authStore.session?.role ?? null;
}

export function isViewer(): boolean {
	return authStore.isAuthenticated;
}

export function isOperator(): boolean {
	const r = currentRole();
	return r === 'admin' || r === 'operator';
}

export function isAdmin(): boolean {
	return currentRole() === 'admin';
}

export function hasRole(required: Role): boolean {
	if (required === 'admin') return isAdmin();
	if (required === 'operator') return isOperator();
	return isViewer();
}
