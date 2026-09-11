<script lang="ts">
	import { goto } from '$app/navigation';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import Spinner from '$lib/components/ui/Spinner.svelte';

	// Chat is the app's primary surface and its home — where the deployment runs it.
	// Without chat there is no conversation to land in, so the entrance falls back to
	// Overview, which is core and therefore always there.
	//
	// An effect rather than onMount: the shell holds this page back until the manifest
	// has answered, but reading it reactively means a redirect that is never decided
	// from the fail-closed placeholder.
	$effect(() => {
		goto(moduleStore.isEnabled('chat') ? '/chat' : '/overview', { replaceState: true });
	});
</script>

<div class="grid min-h-[50vh] place-items-center">
	<Spinner size="lg" />
</div>
