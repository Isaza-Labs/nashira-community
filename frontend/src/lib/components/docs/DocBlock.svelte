<script lang="ts">
	import { Alert, Badge } from '$lib/components/ui';
	import { inline, type Block, type Role, type Method } from '$lib/docs';

	// Renders one documentation block. Every authored string goes through inline(),
	// which escapes before applying the `code`/**bold** grammar — that ordering is
	// what makes {@html} safe here.
	let { block }: { block: Block } = $props();

	const roleTone: Record<Role, 'neutral' | 'primary' | 'warning' | 'success'> = {
		Public: 'warning',
		Viewer: 'neutral',
		Operator: 'primary',
		Admin: 'warning'
	};

	const methodColor: Record<Method, string> = {
		GET: 'text-success-700-300',
		POST: 'text-primary-700-300',
		PUT: 'text-warning-700-300',
		DELETE: 'text-error-700-300'
	};
</script>

{#if block.kind === 'heading'}
	<h2 class="mt-8 border-b border-surface-200-800 pb-1.5 text-lg font-semibold text-surface-900-100">
		{block.text}
	</h2>
{:else if block.kind === 'prose'}
	<p class="text-sm leading-relaxed text-surface-700-300">{@html inline(block.text)}</p>
{:else if block.kind === 'list'}
	<ul class="space-y-1.5 text-sm leading-relaxed text-surface-700-300">
		{#each block.items as item, i (i)}
			<li class="flex gap-2.5">
				<span class="mt-[0.55em] h-1 w-1 shrink-0 rounded-full bg-surface-400-600"></span>
				<span>{@html inline(item)}</span>
			</li>
		{/each}
	</ul>
{:else if block.kind === 'steps'}
	<ol class="space-y-2 text-sm leading-relaxed text-surface-700-300">
		{#each block.items as item, i (i)}
			<li class="flex gap-3">
				<span
					class="grid h-5 w-5 shrink-0 place-items-center rounded-full bg-primary-500/15 text-[11px] font-semibold tabular-nums text-primary-700-300"
				>
					{i + 1}
				</span>
				<span>{@html inline(item)}</span>
			</li>
		{/each}
	</ol>
{:else if block.kind === 'note'}
	<Alert tone={block.tone} title={block.title ?? ''}>
		<span class="leading-relaxed">{@html inline(block.text)}</span>
	</Alert>
{:else if block.kind === 'code'}
	<figure class="space-y-1.5">
		{#if block.caption}
			<figcaption class="text-xs font-medium text-surface-600-400">{block.caption}</figcaption>
		{/if}
		<div class="overflow-x-auto rounded-md border border-surface-200-800 bg-surface-100-900">
			<pre class="p-3 font-mono text-xs leading-relaxed text-surface-800-200">{block.text}</pre>
		</div>
	</figure>
{:else if block.kind === 'values'}
	<div class="space-y-2">
		{#if block.title}
			<h3 class="text-sm font-semibold text-surface-900-100">{block.title}</h3>
		{/if}
		<dl class="ui-surface divide-y divide-surface-200-800 p-0">
			{#each block.rows as row (row.value)}
				<div class="grid gap-1 px-3 py-2.5 sm:grid-cols-[minmax(8rem,auto)_1fr] sm:gap-4">
					<dt>
						<code
							class="rounded bg-surface-200-800 px-1.5 py-0.5 font-mono text-xs text-surface-800-200"
							>{row.value}</code
						>
					</dt>
					<dd class="text-sm leading-relaxed text-surface-700-300">{@html inline(row.desc)}</dd>
				</div>
			{/each}
		</dl>
	</div>
{:else if block.kind === 'params'}
	<div class="space-y-2">
		{#if block.title}
			<h3 class="text-sm font-semibold text-surface-900-100">{block.title}</h3>
		{/if}
		{#if block.intro}
			<p class="text-sm leading-relaxed text-surface-700-300">{@html inline(block.intro)}</p>
		{/if}
		<div class="ui-surface overflow-x-auto p-0">
			<table class="w-full min-w-[34rem] text-left text-sm">
				<thead class="border-b border-surface-200-800 text-xs uppercase tracking-wide text-surface-600-400">
					<tr>
						<th scope="col" class="px-3 py-2 font-medium">Field</th>
						<th scope="col" class="px-3 py-2 font-medium">Type</th>
						<th scope="col" class="px-3 py-2 font-medium">Description</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-surface-200-800">
					{#each block.rows as row (row.name)}
						<tr class="align-top">
							<td class="whitespace-nowrap px-3 py-2.5">
								<code class="font-mono text-xs text-surface-900-100">{row.name}</code>
								{#if row.required}
									<span class="ml-1 text-xs text-error-600-400" title="Required">*</span>
								{/if}
							</td>
							<td class="whitespace-nowrap px-3 py-2.5 text-xs text-surface-600-400">
								{row.type}
								{#if row.default}
									<div class="text-[11px]">default <code class="font-mono">{row.default}</code></div>
								{/if}
							</td>
							<td class="px-3 py-2.5 leading-relaxed text-surface-700-300">
								{@html inline(row.desc)}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
		<!-- Only tables with a required field describe a writable record; read-only
		     shapes (audit events, runs, sync results) would carry a footnote about nothing. -->
		{#if block.rows.some((row) => row.required)}
			<p class="text-xs text-surface-600-400">
				<span class="text-error-600-400">*</span> required on create. Update endpoints are partial: an omitted
				field keeps its stored value.
			</p>
		{/if}
	</div>
{:else if block.kind === 'endpoints'}
	<div class="space-y-2">
		{#if block.title}
			<h3 class="text-sm font-semibold text-surface-900-100">{block.title}</h3>
		{/if}
		<div class="ui-surface overflow-x-auto p-0">
			<table class="w-full min-w-[34rem] text-left text-sm">
				<thead class="border-b border-surface-200-800 text-xs uppercase tracking-wide text-surface-600-400">
					<tr>
						<th scope="col" class="px-3 py-2 font-medium">Endpoint</th>
						<th scope="col" class="px-3 py-2 font-medium">Role</th>
						<th scope="col" class="px-3 py-2 font-medium">Description</th>
					</tr>
				</thead>
				<tbody class="divide-y divide-surface-200-800">
					{#each block.rows as row (`${row.method} ${row.path}`)}
						<tr class="align-top">
							<td class="px-3 py-2.5">
								<div class="flex items-baseline gap-2 whitespace-nowrap">
									<span class={`font-mono text-[11px] font-semibold ${methodColor[row.method]}`}>
										{row.method}
									</span>
									<code class="font-mono text-xs text-surface-900-100">{row.path}</code>
								</div>
							</td>
							<td class="px-3 py-2.5"><Badge tone={roleTone[row.role]}>{row.role}</Badge></td>
							<td class="px-3 py-2.5 leading-relaxed text-surface-700-300">
								{@html inline(row.desc)}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
{/if}
