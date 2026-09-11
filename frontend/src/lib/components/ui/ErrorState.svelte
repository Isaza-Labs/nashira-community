<script lang="ts">
	import { CloudOff, AlertTriangle, RefreshCw } from 'lucide-svelte';
	import Button from './Button.svelte';
	import { ApiError, errorMessage, isOfflineError } from '$lib/api/client';

	// Full or compact error surface for a failed data load. Distinguishes an
	// offline/timeout failure (retry likely helps) from a server/logic error.
	let {
		error,
		onRetry,
		title,
		compact = false
	}: {
		error: unknown;
		onRetry?: () => void;
		title?: string;
		compact?: boolean;
	} = $props();

	const offline = $derived(isOfflineError(error));
	const Icon = $derived(offline ? CloudOff : AlertTriangle);
	const heading = $derived(title ?? (offline ? 'No connection to the server' : 'We couldn’t load this'));
	const message = $derived(errorMessage(error));
	const code = $derived(error instanceof ApiError && error.status > 0 ? error.status : null);
</script>

{#if compact}
	<div
		class="flex items-start gap-2.5 rounded-md bg-surface-100-900/70 p-3 ring-1 ring-inset ring-surface-300-700"
	>
		<Icon size={16} class="mt-0.5 shrink-0 text-surface-600-400" />
		<div class="min-w-0 flex-1 text-sm">
			<div class="font-medium text-surface-900-100">{heading}</div>
			<div class="mt-0.5 text-xs text-surface-600-400">{message}</div>
		</div>
		{#if onRetry}
			<Button size="sm" variant="ghost" onclick={onRetry}>
				<RefreshCw size={14} />Retry
			</Button>
		{/if}
	</div>
{:else}
	<div class="flex flex-col items-center justify-center px-6 py-12 text-center">
		<div
			class="mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-surface-200-800/60 text-surface-600-400"
		>
			<Icon size={20} />
		</div>
		<h3 class="text-sm font-semibold text-surface-900-100">{heading}</h3>
		<p class="mt-1.5 max-w-sm text-sm text-surface-600-400">{message}</p>
		{#if code}
			<p class="mt-2 font-mono text-[10px] tabular-nums text-surface-600-400">code {code}</p>
		{/if}
		{#if onRetry}
			<div class="mt-5">
				<Button variant="primary" onclick={onRetry}>
					<RefreshCw size={16} />Retry
				</Button>
			</div>
		{/if}
	</div>
{/if}
