<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { Tone } from './types';
	import { animate, prefersReducedMotion } from '$lib/anim';

	// A single headline number. `tone` tints the value when the number itself
	// carries a verdict (devices down, failed runs) — a count of failures must not
	// read the same as a count of healthy things. `href` makes the whole card the
	// way through to the underlying list.
	let {
		label,
		value,
		hint = '',
		tone = 'neutral',
		href = undefined,
		loading = false,
		icon
	}: {
		label: string;
		value: string | number;
		hint?: string;
		tone?: Tone;
		href?: string;
		loading?: boolean;
		icon?: Snippet;
	} = $props();

	const tones: Record<Tone, string> = {
		neutral: 'text-surface-950-50',
		primary: 'text-primary-700-300',
		success: 'text-success-700-300',
		warning: 'text-warning-700-300',
		error: 'text-error-700-300'
	};

	// Numeric values count up to their target instead of appearing — the same
	// treatment Flow Weaver's stat cards use. Strings ("3/5", "—") render as-is.
	// The effect owns the element's text while animating; Svelte renders only the
	// initial frame, so the two never write the same node at the same time.
	const numericTarget = $derived.by<number | null>(() =>
		typeof value === 'number' && Number.isFinite(value) ? value : null
	);

	let valueEl = $state<HTMLElement | null>(null);
	let lastShown = $state(0);

	$effect(() => {
		if (numericTarget == null || !valueEl) return;
		const target = numericTarget;
		if (prefersReducedMotion()) {
			valueEl.textContent = String(target);
			lastShown = target;
			return;
		}
		const obj = { n: lastShown };
		animate(obj, {
			n: target,
			duration: 600,
			ease: 'outCubic',
			onUpdate: () => {
				if (valueEl) valueEl.textContent = String(Math.round(obj.n));
			},
			onComplete: () => {
				if (valueEl) valueEl.textContent = String(target);
				lastShown = target;
			}
		});
	});
</script>

{#snippet body()}
	<div class="flex items-start justify-between gap-2">
		<div class="text-sm text-surface-600-400">{label}</div>
		{#if icon}<div class="shrink-0 text-surface-600-400/70">{@render icon()}</div>{/if}
	</div>
	{#if loading}
		<div class="mt-2 h-7 w-16 animate-pulse rounded bg-surface-200-800"></div>
	{:else}
		<div bind:this={valueEl} class={`mt-1 text-2xl font-semibold tabular-nums ${tones[tone]}`}>
			{numericTarget != null ? lastShown : value}
		</div>
	{/if}
	{#if hint}<div class="mt-1 text-xs text-surface-600-400">{hint}</div>{/if}
{/snippet}

{#if href}
	<a {href} class="ui-surface block p-4 transition hover:-translate-y-0.5">{@render body()}</a>
{:else}
	<div class="ui-surface p-4">{@render body()}</div>
{/if}
