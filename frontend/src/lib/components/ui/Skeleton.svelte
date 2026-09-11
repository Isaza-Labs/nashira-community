<script lang="ts">
	let { class: klass = 'h-4 w-full' }: { class?: string } = $props();
</script>

<div class={`animate-pulse rounded bg-surface-200-800 ${klass}`} aria-hidden="true"></div>
