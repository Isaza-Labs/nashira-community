<script lang="ts">
	import { page } from '$app/state';
	import { PageHeader, Badge, Button, EmptyState } from '$lib/components/ui';
	import DocBlock from '$lib/components/docs/DocBlock.svelte';
	import { availableSections, sectionBySlug, inline } from '$lib/docs';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { ArrowLeft, ArrowRight, ExternalLink } from 'lucide-svelte';

	// Only what this deployment documents. A section of a capability it does not run
	// is not a 404 of its own making — it simply is not part of this install's manual,
	// and reads the same as a slug that never existed.
	const sections = $derived(availableSections(moduleStore.availability));
	const section = $derived(
		sections.some((s) => s.slug === page.params.slug)
			? sectionBySlug(page.params.slug ?? '')
			: undefined
	);

	// Previous/next follow the registry order, which is the reading order — through
	// the sections this deployment has, so the chain never steps into a hidden one.
	const index = $derived(sections.findIndex((s) => s.slug === page.params.slug));
	const prev = $derived(index > 0 ? sections[index - 1] : null);
	const next = $derived(index >= 0 && index < sections.length - 1 ? sections[index + 1] : null);

	// A uiPath with a placeholder segment (/workflows/{id}) is not navigable.
	const uiHref = $derived(
		section?.uiPath && !section.uiPath.includes('{') && section.uiPath.startsWith('/')
			? section.uiPath
			: null
	);
</script>

<svelte:head>
	<title>{section ? `${section.title} · Docs` : 'Not found'} · Nashira</title>
</svelte:head>

{#if !section}
	<EmptyState
		title="No such documentation page"
		description={`There is no section called "${page.params.slug}".`}
	>
		{#snippet actions()}
			<Button href="/docs" variant="primary">Back to the index</Button>
		{/snippet}
	</EmptyState>
{:else}
	<article>
		<PageHeader title={section.title} description={section.tagline}>
			{#snippet actions()}
				{#if uiHref}
					<Button href={uiHref} variant="ghost">
						Open {section.title}<ExternalLink size={14} />
					</Button>
				{/if}
			{/snippet}
		</PageHeader>

		<div class="mb-5 flex flex-wrap items-center gap-2 text-xs text-surface-600-400">
			<Badge>{section.group}</Badge>
			{#if section.role}<Badge tone={section.role === 'Admin' ? 'warning' : 'neutral'}>
					{section.role}+
				</Badge>{/if}
			{#if section.uiPath}<code class="font-mono">{section.uiPath}</code>{/if}
			{#if section.apiBase && section.apiBase !== '—'}
				<code class="font-mono">{section.apiBase}</code>
			{/if}
		</div>

		<div class="ui-surface mb-6 border-l-2 border-l-primary-500/60 p-4">
			<h2 class="text-xs font-semibold uppercase tracking-wider text-surface-600-400">
				What it's for
			</h2>
			<p class="mt-1.5 text-sm leading-relaxed text-surface-800-200">
				{@html inline(section.purpose)}
			</p>
		</div>

		<div class="space-y-4">
			{#each section.blocks as block, i (i)}
				<DocBlock {block} />
			{/each}
		</div>

		<nav class="mt-10 flex items-stretch gap-3 border-t border-surface-200-800 pt-4" aria-label="Pagination">
			{#if prev}
				<a
					href={`/docs/${prev.slug}`}
					class="ui-surface group flex flex-1 items-center gap-2 p-3 transition hover:-translate-y-0.5"
				>
					<ArrowLeft size={14} class="shrink-0 text-surface-600-400" />
					<span class="min-w-0">
						<span class="block text-[11px] text-surface-600-400">Previous</span>
						<span class="block truncate text-sm font-medium text-surface-900-100">{prev.title}</span>
					</span>
				</a>
			{/if}
			{#if next}
				<a
					href={`/docs/${next.slug}`}
					class="ui-surface group flex flex-1 items-center justify-end gap-2 p-3 text-right transition hover:-translate-y-0.5"
				>
					<span class="min-w-0">
						<span class="block text-[11px] text-surface-600-400">Next</span>
						<span class="block truncate text-sm font-medium text-surface-900-100">{next.title}</span>
					</span>
					<ArrowRight size={14} class="shrink-0 text-surface-600-400" />
				</a>
			{/if}
		</nav>
	</article>
{/if}
