<script lang="ts">
	// Renders `children` only when the current session satisfies `require`
	// (viewer | operator | admin), reactively. Optional `fallback` snippet renders
	// otherwise. Mirrors the backend policies; the API still enforces access.
	import type { Snippet } from 'svelte';
	import { authStore } from '$lib/stores/auth.svelte';
	import type { Role } from '$lib/guards/roles';

	let {
		require: required = 'viewer',
		children,
		fallback
	}: { require?: Role; children?: Snippet; fallback?: Snippet } = $props();

	const role = $derived(authStore.session?.role ?? null);
	const allowed = $derived(
		required === 'admin'
			? role === 'admin'
			: required === 'operator'
				? role === 'admin' || role === 'operator'
				: authStore.isAuthenticated
	);
</script>

{#if allowed}
	{@render children?.()}
{:else if fallback}
	{@render fallback()}
{/if}
