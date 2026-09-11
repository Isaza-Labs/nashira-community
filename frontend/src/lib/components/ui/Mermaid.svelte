<script lang="ts">
	// Draws a Mermaid source string.
	//
	// The `mermaid` package has been a dependency of this app since the snippet
	// catalogue grew a `logic_diagram_mermaid` field, and nothing imported it: the
	// diagrams were authored, validated by the API and stored, and then never drawn
	// anywhere — so the one thing they exist for (opening a python_snippet six
	// months later and seeing what the step does without reading the script) did not
	// happen. Skills/mermaid.md tells the agent "the UI renders it"; this is the
	// renderer that claim refers to.
	//
	// Imported dynamically. Mermaid pulls in its own layout engine and is by a wide
	// margin the heaviest thing in the tree; loading it eagerly would put it in the
	// entry chunk for every page, including the ones with no diagram on them.
	import { onDestroy } from 'svelte';
	import { prefs } from '$lib/stores/prefs.svelte';
	import { AlertTriangle } from 'lucide-svelte';

	let {
		code = '',
		// Shown instead of an error when the source is empty. Null renders nothing,
		// which is what a page wants when the diagram is simply absent.
		placeholder = null,
		class: className = ''
	}: { code?: string | null; placeholder?: string | null; class?: string } = $props();

	let svg = $state('');
	let error = $state('');
	let rendering = $state(false);

	const source = $derived((code ?? '').trim());

	// Every render gets its own id. Mermaid injects a temporary element under that
	// id while it measures text, and two diagrams sharing one would race.
	let seq = 0;
	// Renders overlap while someone types into the editor's textarea; only the
	// newest one is allowed to write its result.
	let latest = 0;
	let disposed = false;
	onDestroy(() => (disposed = true));

	// mermaid.initialize is global, so the theme is re-applied on every render
	// rather than once at import: the mode toggle has to repaint diagrams that are
	// already on screen, and re-initializing is the only way to move it.
	async function render(src: string, dark: boolean, token: number) {
		try {
			const mermaid = (await import('mermaid')).default;
			mermaid.initialize({
				startOnLoad: false,
				securityLevel: 'strict', // no click handlers, no raw HTML from the source
				theme: dark ? 'dark' : 'default',
				// The token, not a hardcoded stack: the SVG is inline in the document,
				// so the variable resolves, and a theme that swaps the body font swaps
				// the diagram's with it.
				fontFamily: 'var(--font-sans)',
				flowchart: { htmlLabels: false, useMaxWidth: true },
				sequence: { useMaxWidth: true }
			});
			const out = await mermaid.render(`na-mermaid-${++seq}`, src);
			if (disposed || token !== latest) return;
			svg = out.svg;
			error = '';
		} catch (e) {
			if (disposed || token !== latest) return;
			svg = '';
			// The message mermaid throws names the offending line, which is the only
			// useful thing to show someone editing the source by hand.
			error = e instanceof Error ? e.message : 'This is not a diagram Mermaid can draw.';
		} finally {
			if (token === latest) rendering = false;
		}
	}

	// Debounced, because both callers change `code` far faster than mermaid can
	// draw: the editor's textarea fires per keystroke, and in chat the component is
	// remounted on every streamed chunk with a fence that is not closed yet — so
	// without this, a diagram arriving in a reply would run a full layout pass per
	// token, each one failing on a truncated source. Waiting for the source to
	// settle means one render of the finished thing.
	const SETTLE_MS = 180;
	let timer: ReturnType<typeof setTimeout> | undefined;
	onDestroy(() => clearTimeout(timer));

	$effect(() => {
		const src = source;
		const dark = prefs.mode === 'dark';
		const token = ++latest;

		// Clearing is immediate: leaving the previous diagram up while an empty or
		// replaced source settles would show the wrong picture for a fifth of a second.
		clearTimeout(timer);
		if (!src) {
			svg = '';
			error = '';
			rendering = false;
			return;
		}
		rendering = true;
		timer = setTimeout(() => void render(src, dark, token), SETTLE_MS);
	});
</script>

<div class={className}>
	{#if !source}
		{#if placeholder}
			<p class="px-3 py-6 text-center text-xs text-surface-600-400">{placeholder}</p>
		{/if}
	{:else if error}
		<div
			class="flex items-start gap-2 rounded-md border border-warning-500/40 bg-warning-500/10 px-3 py-2 text-xs text-warning-700-300"
		>
			<AlertTriangle size={14} class="mt-0.5 shrink-0" />
			<span class="min-w-0 break-words">{error}</span>
		</div>
	{:else if svg}
		<!-- securityLevel 'strict' has mermaid sanitize the source before it becomes
		     markup, and the source itself is a stored field an operator or the agent
		     wrote, never a raw response body. -->
		<div class="na-mermaid">{@html svg}</div>
	{:else if rendering}
		<p class="px-3 py-6 text-center text-xs text-surface-600-400">Drawing diagram…</p>
	{/if}
</div>

<style>
	/* Mermaid sizes the SVG to its own measurements; without this a wide flowchart
	   overflows whatever it is sitting in instead of scaling down to it. */
	.na-mermaid :global(svg) {
		display: block;
		max-width: 100%;
		height: auto;
		margin: 0 auto;
	}
</style>
