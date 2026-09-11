<script lang="ts">
	import { page } from '$app/state';
	import { authStore } from '$lib/stores/auth.svelte';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';
	import { section, sectionPages, canViewPage, isPathActive, type NavSectionId } from '$lib/nav/registry';
	import { moduleStore } from '$lib/stores/modules.svelte';

	// The tab bar for a grouped surface. Pages that are edited together should be
	// navigated together: a spec IS an integration and a skill is how to operate
	// one, so moving between them must not cost a trip through an index.
	//
	// These are plain links, not a router: no route moved to gain the grouping, so
	// no bookmark broke to gain it either.
	let { id }: { id: NavSectionId } = $props();

	const role = $derived(authStore.session?.role ?? '');
	const meta = $derived(section(id));
	const tabs = $derived(
		sectionPages(id).filter((p) =>
			canViewPage(p, role, moduleStore.availability, navigationVisibilityStore.visibility)
		)
	);
	const current = $derived(page.url.pathname);
</script>

{#if meta && tabs.length > 1}
	<div class="mb-5 space-y-2">
		<div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
			<h2 class="text-sm font-semibold text-surface-900-100">{meta.label}</h2>
			<p class="text-xs text-surface-600-400">{meta.blurb}</p>
		</div>

		<nav
			class="flex flex-wrap gap-1 border-b border-surface-200-800 pb-px"
			aria-label={`${meta.label} sections`}
		>
			{#each tabs as tab (tab.href)}
				{@const active = isPathActive(current, tab.href)}
				<a
					href={tab.href}
					title={tab.desc}
					aria-current={active ? 'page' : undefined}
					class="-mb-px flex items-center gap-1.5 border-b-2 px-2.5 py-1.5 text-sm transition {active
						? 'border-primary-500 font-medium text-primary-700-300 dark:border-primary-400'
						: 'border-transparent text-surface-600-400 hover:border-surface-300-700 hover:text-surface-950-50'}"
				>
					<tab.icon size={14} class="shrink-0" />
					{tab.label}
				</a>
			{/each}
		</nav>
	</div>
{/if}
