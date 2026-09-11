<script lang="ts">
	import { PageHeader, Badge } from '$lib/components/ui';
	import { availableSections, groupSections } from '$lib/docs';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { ArrowRight } from 'lucide-svelte';

	// Docs is core, so it opens everywhere — but it describes only what this
	// deployment runs. A page explaining how to register a device, in an install with
	// no fleet, reads as a missing feature rather than as one nobody bought.
	const sections = $derived(availableSections(moduleStore.availability));
	const groups = $derived(groupSections(sections));
	const has = (slug: string) => sections.some((s) => s.slug === slug);
</script>

<svelte:head><title>Documentation · Nashira</title></svelte:head>

<PageHeader
	title="Documentation"
	description="What every part of Nashira is for, how it works, and the parameters it takes."
/>

<div class="space-y-3">
	<p class="text-sm leading-relaxed text-surface-700-300">
		{sections.length} sections, grouped in reading order: the concepts everything else assumes, then
		daily operations, then the workflow platform, governance, integrations and administration. Each
		one answers the same three questions — what it is for, how it works, and what its fields and
		endpoints are.
	</p>

	<div class="ui-surface p-4">
		<h2 class="text-sm font-semibold text-surface-900-100">Start here</h2>
		<ul class="mt-2 space-y-1.5 text-sm text-surface-700-300">
			<li>
				First time signing in →
				<a class="text-primary-700-300 underline" href="/docs/quick-start">Quick start</a>
			</li>
			<li>
				New to the console →
				<a class="text-primary-700-300 underline" href="/docs/overview">How Nashira fits together</a>
			</li>
			<!-- The hand-written starting points name sections by slug, so they have to
			     answer to the same filter as the cards below: a highlighted link into a
			     page this deployment does not document is worse than no highlight. -->
			{#if has('workflows')}
				<li>
					Automating something →
					<a class="text-primary-700-300 underline" href="/docs/workflows">Workflows</a>
					and <a class="text-primary-700-300 underline" href="/docs/snippets">Snippets</a>
				</li>
			{/if}
			{#if has('policies')}
				<li>
					Setting up guardrails →
					<a class="text-primary-700-300 underline" href="/docs/policies">Policies</a>
				</li>
			{/if}
		</ul>
	</div>
</div>

{#each groups as group (group.title)}
	<section class="mt-8">
		<h2 class="mb-3 text-xs font-semibold uppercase tracking-wider text-surface-600-400">
			{group.title}
		</h2>
		<div class="grid gap-3 sm:grid-cols-2">
			{#each group.sections as section (section.slug)}
				<a
					href={`/docs/${section.slug}`}
					class="ui-surface group flex flex-col gap-1.5 p-4 transition duration-150 hover:-translate-y-0.5"
				>
					<div class="flex items-center gap-2">
						<span class="font-semibold text-surface-900-100">{section.title}</span>
						{#if section.role && section.role !== 'Viewer'}
							<Badge tone="warning">{section.role}</Badge>
						{/if}
						<ArrowRight
							size={14}
							class="ml-auto shrink-0 text-surface-600-400/60 transition group-hover:translate-x-0.5 group-hover:text-primary-700-300"
						/>
					</div>
					<p class="text-xs leading-relaxed text-surface-600-400">{section.tagline}</p>
					{#if section.uiPath || section.apiBase}
						<div class="mt-1 flex flex-wrap gap-2 text-[11px] text-surface-600-400">
							{#if section.uiPath}<code class="font-mono">{section.uiPath}</code>{/if}
							{#if section.apiBase && section.apiBase !== '—'}
								<code class="font-mono">{section.apiBase}</code>
							{/if}
						</div>
					{/if}
				</a>
			{/each}
		</div>
	</section>
{/each}
