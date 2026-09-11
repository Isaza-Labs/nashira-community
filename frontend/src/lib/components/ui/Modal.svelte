<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { Size } from './types';
	import { safeAnimate } from '$lib/anim';

	// Reusable modal built on the native <dialog> (focus trap, Esc, inert
	// background for free). Own `open` in the parent with bind:open. `footer` is a
	// named snippet for actions.
	let {
		open = $bindable(false),
		title = '',
		size = 'md',
		onclose = undefined,
		children,
		footer
	}: {
		open?: boolean;
		title?: string;
		size?: Size;
		onclose?: () => void;
		children?: Snippet;
		footer?: Snippet;
	} = $props();

	let dialog = $state<HTMLDialogElement | undefined>(undefined);

	$effect(() => {
		const d = dialog;
		if (!d) return;
		if (open && !d.open) {
			d.showModal();
			// The panel settles into place rather than appearing. The backdrop
			// fades via CSS below; safeAnimate collapses this to the final frame
			// under prefers-reduced-motion.
			safeAnimate(d, {
				opacity: [0, 1],
				scale: [0.96, 1],
				translateY: [8, 0],
				duration: 220,
				ease: 'outCubic'
			});
		} else if (!open && d.open) d.close();
	});

	function requestClose() {
		open = false;
		onclose?.();
	}

	const widths: Record<Size, string> = {
		sm: 'max-w-sm',
		md: 'max-w-lg',
		lg: 'max-w-2xl'
	};
</script>

<dialog
	bind:this={dialog}
	class={`ui-surface m-auto w-[calc(100%-2rem)] ${widths[size]} p-0 text-surface-950-50 backdrop:bg-black/50`}
	oncancel={(e) => {
		e.preventDefault();
		requestClose();
	}}
	onclick={(e) => {
		if (e.target === dialog) requestClose();
	}}
>
	{#if open}
		<div
			class="flex items-center justify-between gap-4 border-b border-surface-200-800 px-5 py-3"
		>
			<h2 class="text-base font-semibold">{title}</h2>
			<button
				type="button"
				class="rounded-md px-1.5 text-xl leading-none text-surface-600-400 hover:bg-surface-100-900"
				aria-label="Close"
				onclick={requestClose}
			>
				&times;
			</button>
		</div>
		<div class="px-5 py-4">{@render children?.()}</div>
		{#if footer}
			<div class="flex justify-end gap-2 border-t border-surface-200-800 px-5 py-3">
				{@render footer()}
			</div>
		{/if}
	{/if}
</dialog>

<style>
	/* The backdrop is a pseudo-element, out of animejs's reach — CSS carries it.
	   The app-wide reduced-motion rule zeroes this like any other animation. */
	dialog[open]::backdrop {
		animation: na-fade-in 160ms cubic-bezier(0.4, 0, 0.2, 1);
	}
</style>
