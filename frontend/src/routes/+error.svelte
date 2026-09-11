<script lang="ts">
	import { page } from '$app/state';
	import { Button } from '$lib/components/ui';

	const status = $derived(page.status);
	const message = $derived(
		page.error?.message || (status === 404 ? 'This page could not be found.' : 'Something went wrong.')
	);
</script>

<svelte:head><title>{status} · Nashira</title></svelte:head>

<div class="flex min-h-[60vh] flex-col items-center justify-center gap-3 px-4 text-center">
	<div class="text-4xl font-semibold tabular-nums">{status}</div>
	<p class="max-w-sm text-surface-600-400">{message}</p>
	<div class="mt-2">
		<Button href="/chat" variant="primary">Back to chat</Button>
	</div>
</div>
