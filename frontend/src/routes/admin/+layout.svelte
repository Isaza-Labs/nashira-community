<script lang="ts">
	// Guards the /admin/* subtree with the role the nav registry declares for the
	// current path, so the guard can never contradict the sidebar: a page the menu
	// shows to a viewer (the read-only catalogs — snippets, pools, vendor commands,
	// integrations, MCP) opens for a viewer, and everything undeclared under /admin
	// fails closed to admin. The API enforces access regardless.
	import { page } from '$app/state';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { minRoleForPath } from '$lib/nav/registry';

	let { children } = $props();
	const required = $derived(minRoleForPath(page.url.pathname));
</script>

<RoleGate require={required}>
	{#snippet fallback()}
		<div class="ui-surface px-4 py-16 text-center text-sm text-surface-600-400">
			Administrator access is required for this area.
		</div>
	{/snippet}
	{@render children?.()}
</RoleGate>
