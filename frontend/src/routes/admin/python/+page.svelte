<script lang="ts">
	// Both halves of the Python surface on one page: Packages (what a python_snippet
	// may import, and the pip installs behind it) and Environments (uv virtualenvs
	// for SSH and snippets). The open tab lives in the URL — `?tab=environments`,
	// Packages by default — so a link lands on the right one and Back steps between
	// them.
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { PageHeader, Tabs } from '$lib/components/ui';
	import PythonModulesPanel from '$lib/components/python/PythonModulesPanel.svelte';
	import PythonEnvironmentsPanel from '$lib/components/python/PythonEnvironmentsPanel.svelte';
	import { PYTHON_PATH, visibleTabs } from '$lib/nav/registry';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { navigationVisibilityStore } from '$lib/stores/navigation-visibility.svelte';

	// From the registry, so the strip, the palette and the navigation settings agree
	// on which tabs exist and which this deployment and its settings allow.
	const tabs = $derived(
		visibleTabs(PYTHON_PATH, moduleStore.availability, navigationVisibilityStore.visibility)
	);

	// An unknown or unavailable `?tab=` falls back to the first tab that is offered.
	const tab = $derived.by(() => {
		const asked = page.url.searchParams.get('tab');
		return tabs.find((t) => t.value === asked)?.value ?? tabs[0]?.value ?? '';
	});

	const titles: Record<string, string> = {
		packages: 'Python packages',
		environments: 'Python environments'
	};

	function select(next: string) {
		const params = new URLSearchParams(page.url.search);
		params.set('tab', next);
		// `new` belongs to the tab being left: carried over, it would open the other
		// tab's create form.
		params.delete('new');
		// A history entry per switch, so Back returns to the previous tab.
		goto(`${page.url.pathname}?${params.toString()}`, { keepFocus: true, noScroll: true });
	}
</script>

<svelte:head><title>{titles[tab] ?? 'Python'} · Nashira</title></svelte:head>

<PageHeader
	title="Python"
	description="What a python_snippet may import, and the uv environments SSH sessions and snippets run under."
/>

{#if tabs.length === 0}
	<div class="ui-surface px-4 py-16 text-center text-sm text-surface-600-400">
		Nothing here is available in this deployment.
	</div>
{:else}
	<div class="mb-4">
		<Tabs
			tabs={tabs.map((t) => ({ value: t.value, label: t.label }))}
			value={tab}
			onchange={select}
		/>
	</div>

	{#if tab === 'packages'}
		<PythonModulesPanel />
	{:else if tab === 'environments'}
		<PythonEnvironmentsPanel />
	{/if}
{/if}
