<script lang="ts">
	// What replaces a page the shell refuses to mount. Two refusals, two messages,
	// because they point at different people: a permission is something an
	// administrator can grant in this deployment, while a disabled module is not here
	// to be granted. Saying "your permissions" for a capability the deployment never
	// installed sends someone hunting for a toggle that does not exist.
	//
	// Nothing here renders the page or its data — that is the point of the component:
	// a blocked route must not mount children, so it must not fire their requests.
	let { reason }: { reason: 'module' | 'permission' } = $props();

	const title = $derived(
		reason === 'module' ? 'Module unavailable for this deployment' : 'Area not available'
	);
	const description = $derived(
		reason === 'module'
			? 'This deployment does not run the capability behind this page. Enabling it is a deployment change, not a permission.'
			: 'Your navigation permissions do not include this area.'
	);
</script>

<div class="text-center">
	<h1 class="text-lg font-semibold">{title}</h1>
	<p class="mx-auto mt-1 max-w-md text-sm text-surface-600-400">{description}</p>
</div>
