<script lang="ts">
	// A stacked bar per bucket, drawn with divs rather than a charting library.
	//
	// The data is a handful of daily totals; pulling in a chart library to render
	// thirty rectangles would cost more than everything it draws. Divs also inherit the
	// theme tokens, so the chart follows light/dark without a second palette to keep in
	// sync.
	//
	// Deliberately not a general charting primitive. It draws counts per bucket split by
	// series, which is what the dashboard needs — anything that wants axes, zooming or
	// continuous data should bring its own thing rather than bend this one.

	import type { BarBucket, BarSeries } from './types';
	import { animate, stagger, prefersReducedMotion } from '$lib/anim';

	let {
		buckets,
		series,
		height = 160,
		empty = 'No data in this window.',
		caption = 'Totals per bucket'
	}: {
		buckets: BarBucket[];
		/** Drawn bottom to top in this order. */
		series: BarSeries[];
		height?: number;
		empty?: string;
		/** Names the hidden table. Say what it counts — a page with two charts
		 *  otherwise announces the same title twice, which identifies neither. */
		caption?: string;
	} = $props();

	const totals = $derived(
		buckets.map((b) => series.reduce((sum, s) => sum + (b.counts[s.key] ?? 0), 0))
	);

	// The tallest bar defines the scale. Floored at 1 so a window whose only activity is
	// a single event still draws a full-height bar instead of dividing by zero.
	const max = $derived(Math.max(1, ...totals));
	const grandTotal = $derived(totals.reduce((a, b) => a + b, 0));

	function pct(n: number): number {
		return (n / max) * 100;
	}

	function tooltip(b: BarBucket, total: number): string {
		if (b.title) return b.title;
		const parts = series
			.filter((s) => (b.counts[s.key] ?? 0) > 0)
			.map((s) => `${s.label}: ${b.counts[s.key]}`);
		return parts.length ? `${b.label} — ${parts.join(', ')}` : `${b.label} — nothing`;
	}

	// The bars grow out of the baseline once, left to right — Flow Weaver's chart
	// entrance. Once only: dashboards refresh on a timer, and replaying the growth
	// on every poll would turn a status display into a screensaver. The effect
	// (not onMount) because the data usually arrives after the component does.
	let barsEl = $state<HTMLElement | null>(null);
	let entered = false;

	$effect(() => {
		if (entered || !barsEl || prefersReducedMotion()) return;
		const stacks = barsEl.querySelectorAll<HTMLElement>('.bar-stack');
		if (stacks.length === 0) return;
		entered = true;
		animate(stacks, {
			scaleY: [0, 1],
			duration: 520,
			ease: 'outCubic',
			delay: stagger(20)
		});
	});
</script>

{#if grandTotal === 0}
	<div
		class="flex items-center justify-center rounded-lg border border-dashed border-surface-200-800 text-sm text-surface-600-400"
		style={`height: ${height}px`}
	>
		{empty}
	</div>
{:else}
	<div>
		<!-- The bars. aria-hidden because the table below carries the same numbers in a
		     form a screen reader can actually read; a row of divs cannot. -->
		<div bind:this={barsEl} class="flex items-end gap-1" style={`height: ${height}px`} aria-hidden="true">
			{#each buckets as b, i (b.label)}
				<div
					class="group flex h-full flex-1 flex-col justify-end"
					title={tooltip(b, totals[i])}
				>
					{#if totals[i] === 0}
						<!-- A quiet day still gets a mark. An empty column is ambiguous
						     between "nothing happened" and "no data". -->
						<div class="h-px w-full bg-surface-200-800"></div>
					{:else}
						<div class="bar-stack flex w-full origin-bottom flex-col-reverse overflow-hidden rounded-sm">
							{#each series as s (s.key)}
								{@const n = b.counts[s.key] ?? 0}
								{#if n > 0}
									<div class={s.class} style={`height: ${pct(n)}%`}></div>
								{/if}
							{/each}
						</div>
					{/if}
				</div>
			{/each}
		</div>

		<div class="mt-1 flex gap-1 text-[10px] text-surface-600-400">
			{#each buckets as b (b.label)}
				<div class="flex-1 truncate text-center">{b.label}</div>
			{/each}
		</div>

		<div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1 text-xs">
			{#each series as s (s.key)}
				{@const total = buckets.reduce((sum, b) => sum + (b.counts[s.key] ?? 0), 0)}
				<span class="inline-flex items-center gap-1.5 text-surface-600-400">
					<span class={`inline-block h-2.5 w-2.5 rounded-sm ${s.class}`}></span>
					{s.label}
					<span class="tabular-nums text-surface-700-300">{total}</span>
				</span>
			{/each}
		</div>

		<!-- The same figures, reachable. Visually hidden rather than absent: a chart that
		     only exists as coloured rectangles is unreadable to anyone not looking at it.
		     The hiding goes on a wrapping div, never on the table itself: on a table the
		     position and clipping land on its wrapper box while the caption sits outside
		     the clipped area, so it escapes and paints itself across the page. -->
		<div class="sr-only">
			<table>
				<caption>{caption}</caption>
				<thead>
					<tr>
						<th scope="col">Bucket</th>
						{#each series as s (s.key)}<th scope="col">{s.label}</th>{/each}
					</tr>
				</thead>
				<tbody>
					{#each buckets as b (b.label)}
						<tr>
							<th scope="row">{b.label}</th>
							{#each series as s (s.key)}<td>{b.counts[s.key] ?? 0}</td>{/each}
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>
{/if}
