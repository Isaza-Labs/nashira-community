<script lang="ts">
	// Credentials moved into the Vault, which holds every kind of sensitive entry on one
	// screen. This stays so bookmarks, runbooks and links in old chat answers keep
	// working — it lands on the Vault.
	//
	// replaceState: the old path should not sit in history for Back to land on and
	// bounce forward again.
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { Spinner } from '$lib/components/ui';

	$effect(() => {
		// ?new=1 carries over, so the old "new credential" links still open a form.
		const params = new URLSearchParams(page.url.search);
		// No type preset: a credential is one of five kinds, so a new one starts at the type picker.
		const query = params.toString();
		goto(`/admin/vault${query ? `?${query}` : ''}`, { replaceState: true });
	});
</script>

<svelte:head><title>Vault · Nashira</title></svelte:head>

<div class="flex flex-col items-center justify-center gap-3 py-24 text-sm text-surface-600-400">
	<Spinner />
	<p>Credentials now live in the Vault at <code class="font-mono">/admin/vault</code> — taking you there.</p>
</div>
