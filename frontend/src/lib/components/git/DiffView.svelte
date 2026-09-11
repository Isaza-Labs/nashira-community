<script lang="ts">
	// Renders a unified-diff patch with per-line coloring (added / removed / hunk).
	let { patch }: { patch: string } = $props();

	const lines = $derived(patch ? patch.split('\n') : []);

	function cls(line: string): string {
		if (line.startsWith('+') && !line.startsWith('+++')) return 'bg-success-500/10 text-success-700-300';
		if (line.startsWith('-') && !line.startsWith('---')) return 'bg-error-500/10 text-error-700-300';
		if (line.startsWith('@@')) return 'text-primary-700-300';
		if (
			line.startsWith('diff ') ||
			line.startsWith('index ') ||
			line.startsWith('+++') ||
			line.startsWith('---')
		)
			return 'text-surface-600-400';
		return 'text-surface-700-300';
	}
</script>

{#if !patch.trim()}
	<p
		class="rounded-xl border border-surface-200-800 px-4 py-10 text-center text-sm text-surface-600-400"
	>
		No changes in the working tree.
	</p>
{:else}
	<div class="overflow-x-auto rounded-xl border border-surface-200-800 bg-surface-100-900">
		<pre class="py-2 text-xs leading-relaxed"><code
				>{#each lines as line, i (i)}<span class="block px-4 {cls(line)}">{line || ' '}</span
					>{/each}</code
			></pre>
	</div>
{/if}
