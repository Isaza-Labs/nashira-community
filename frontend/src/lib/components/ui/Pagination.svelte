<script lang="ts">
	import Button from './Button.svelte';

	let {
		total,
		limit = 50,
		offset = 0,
		onchange
	}: { total: number; limit?: number; offset?: number; onchange: (offset: number) => void } =
		$props();

	const current = $derived(Math.floor(offset / limit) + 1);
	const totalPages = $derived(Math.max(1, Math.ceil(total / limit)));
</script>

<div class="flex items-center justify-between gap-4 text-sm text-surface-600-400">
	<span>
		{total === 0 ? 'No results' : `${offset + 1}–${Math.min(offset + limit, total)} of ${total}`}
	</span>
	<div class="flex items-center gap-2">
		<Button
			variant="secondary"
			size="sm"
			disabled={current <= 1}
			onclick={() => onchange(Math.max(0, offset - limit))}
		>
			Previous
		</Button>
		<span class="tabular-nums">Page {current} / {totalPages}</span>
		<Button
			variant="secondary"
			size="sm"
			disabled={current >= totalPages}
			onclick={() => onchange(offset + limit)}
		>
			Next
		</Button>
	</div>
</div>
