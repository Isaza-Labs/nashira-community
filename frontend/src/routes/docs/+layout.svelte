<script lang="ts">
	import { page } from '$app/state';
	import { SearchInput } from '$lib/components/ui';
	import { availableSections, groupSections, searchSections } from '$lib/docs';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { BookOpen } from 'lucide-svelte';

	// Two-column docs shell: a section index that stays put, and the page itself.
	// The filter narrows the index by searching the whole corpus, not just titles —
	// looking for "HMAC" should surface Triggers even though the word only appears
	// in one of its notes.
	let { children } = $props();

	let query = $state('');
	// The index and the search both run over what this deployment documents: finding a
	// section by search that the deployment has no screen for is the same broken
	// promise as listing it.
	const sections = $derived(availableSections(moduleStore.availability));
	const groups = $derived(groupSections(searchSections(query, sections)));
	const activeSlug = $derived(page.params.slug ?? '');
</script>

<div class="flex flex-col gap-6 lg:flex-row lg:items-start">
	<nav
		class="lg:sticky lg:top-6 lg:max-h-[calc(100vh-3rem)] lg:w-60 lg:shrink-0 lg:overflow-y-auto"
		aria-label="Documentation"
	>
		<a
			href="/docs"
			class="mb-3 inline-flex items-center gap-2 text-sm font-semibold text-surface-900-100 hover:text-primary-700-300"
		>
			<BookOpen size={16} />
			Documentation
		</a>

		<SearchInput bind:value={query} placeholder="Search the docs…" width="w-full" />

		{#if groups.length === 0}
			<p class="mt-4 text-sm text-surface-600-400">Nothing matches “{query}”.</p>
		{/if}

		<div class="mt-4 space-y-4">
			{#each groups as group (group.title)}
				<div>
					<div
						class="px-2 pb-1 text-[10px] font-semibold uppercase tracking-wider text-surface-600-400/80"
					>
						{group.title}
					</div>
					{#each group.sections as section (section.slug)}
						{@const active = section.slug === activeSlug}
						<a
							href={`/docs/${section.slug}`}
							aria-current={active ? 'page' : undefined}
							class="block rounded-md px-2 py-1.5 text-sm transition {active
								? 'bg-primary-500/12 font-medium text-primary-700-300'
								: 'text-surface-600-400 hover:bg-surface-100-900 hover:text-surface-950-50'}"
						>
							{section.title}
						</a>
					{/each}
				</div>
			{/each}
		</div>
	</nav>

	<div class="min-w-0 flex-1">
		{@render children?.()}
	</div>
</div>
