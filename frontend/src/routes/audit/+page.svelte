<script lang="ts">
	// Audit moved under the admin panel, alongside the rest of it. This stays so the
	// links people bookmarked — and the ones written into runbooks — keep working. A
	// 404 on an audit URL during an investigation is the worst possible moment for one.
	//
	// replaceState: the old path should not sit in history for Back to land on and
	// bounce forward again.
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { Spinner } from '$lib/components/ui';

	$effect(() => {
		// The query string carries over, so /audit?entityType=credential still means
		// what it meant.
		goto(`/admin/audit${page.url.search}`, { replaceState: true });
	});
</script>

<svelte:head><title>Audit · Nashira</title></svelte:head>

<div class="flex flex-col items-center justify-center gap-3 py-24 text-sm text-surface-600-400">
	<Spinner />
	<p>Audit now lives at <code class="font-mono">/admin/audit</code> — taking you there.</p>
</div>
