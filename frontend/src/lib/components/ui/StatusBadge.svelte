<script lang="ts">
	import type { Tone } from './types';

	// Maps a free-form status string to a tone + colored dot. Unknown statuses
	// fall back to neutral, so it is safe on any backend enum. In-flight statuses
	// get a pulsing dot: on an operations console, "happening now" versus
	// "finished" has to be readable without reading the word.
	let { status }: { status: string } = $props();

	const map: Record<string, Tone> = {
		ok: 'success',
		success: 'success',
		succeeded: 'success',
		active: 'success',
		enabled: 'success',
		healthy: 'success',
		completed: 'success',
		passed: 'success',
		ready: 'success',
		pending: 'warning',
		running: 'warning',
		queued: 'warning',
		in_progress: 'warning',
		warning: 'warning',
		stale: 'warning',
		installing: 'warning',
		draft: 'neutral',
		disabled: 'neutral',
		failed: 'error',
		error: 'error',
		unhealthy: 'error',
		cancelled: 'error',
		rejected: 'error'
	};

	// Statuses that mean "work is in flight right now".
	const IN_FLIGHT = new Set(['running', 'queued', 'in_progress', 'pending', 'syncing', 'installing']);

	const key = $derived((status ?? '').toLowerCase());
	const tone = $derived<Tone>(map[key] ?? 'neutral');
	const live = $derived(IN_FLIGHT.has(key));

	const dot: Record<Tone, string> = {
		neutral: 'bg-surface-500',
		primary: 'bg-primary-500 dark:bg-primary-400',
		success: 'bg-success-500',
		warning: 'bg-warning-500',
		error: 'bg-error-500 dark:bg-error-400'
	};
	const text: Record<Tone, string> = {
		neutral: 'text-surface-600-400',
		primary: 'text-primary-700-300',
		success: 'text-success-700-300',
		warning: 'text-warning-700-300',
		error: 'text-error-700-300'
	};
</script>

<span class={`inline-flex items-center gap-1.5 text-xs font-medium ${text[tone]}`}>
	<span class="relative inline-flex h-1.5 w-1.5 shrink-0">
		{#if live}
			<!-- Expanding halo behind the dot. The global prefers-reduced-motion
			     reset in app.css neutralizes it for users who ask for that. -->
			<span class={`absolute inline-flex h-full w-full animate-ping rounded-full opacity-75 ${dot[tone]}`}
			></span>
		{/if}
		<span class={`relative inline-flex h-1.5 w-1.5 rounded-full ${dot[tone]}`}></span>
	</span>
	{status}
</span>
