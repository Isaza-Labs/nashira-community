<script lang="ts">
	// Python modules moved into /admin/python, as the Packages tab. This stays so bookmarks,
	// runbooks and links in old chat answers keep working.
	//
	// replaceState: the old path should not sit in history for Back to land on and
	// bounce forward again.
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { Spinner } from '$lib/components/ui';
	import { PYTHON_PREDECESSORS, redirectTarget } from '$lib/nav/registry';

	$effect(() => {
		// The query carries over, so the old "?new=1" links still open the create form.
		goto(redirectTarget(PYTHON_PREDECESSORS.packages, page.url.search), { replaceState: true });
	});
</script>

<svelte:head><title>Python · Nashira</title></svelte:head>

<div class="flex flex-col items-center justify-center gap-3 py-24 text-sm text-surface-600-400">
	<Spinner />
	<p>
		Python modules now live under <code class="font-mono">/admin/python?tab=packages</code> — taking you there.
	</p>
</div>
