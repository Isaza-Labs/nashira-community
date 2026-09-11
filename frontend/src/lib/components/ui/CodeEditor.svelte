<script lang="ts" module>
	// Register only the languages the app edits — core + 3 grammars keeps the
	// bundle small and works fully offline (air-gap: no CDN).
	import hljs from 'highlight.js/lib/core';
	import markdown from 'highlight.js/lib/languages/markdown';
	import yaml from 'highlight.js/lib/languages/yaml';
	import json from 'highlight.js/lib/languages/json';

	hljs.registerLanguage('markdown', markdown);
	hljs.registerLanguage('yaml', yaml);
	hljs.registerLanguage('json', json);

	export type CodeLanguage = 'markdown' | 'yaml' | 'json';
</script>

<script lang="ts">
	// Syntax-highlighted code editor: a transparent <textarea> (the real input)
	// stacked on a highlighted <pre> that mirrors its content. Both share the
	// exact same typography and wrapping so the caret always lands on the glyph
	// it appears over. With `readonly`, only the highlighted view renders.
	import { Upload } from 'lucide-svelte';

	// Setting `accept` adds a "Load file" button that reads a local file straight
	// into the editor, so a skill/spec can be uploaded instead of pasted. The read
	// is entirely client-side: the content still travels through the normal save
	// path, which is where server-side security validation happens.
	let {
		value = $bindable(''),
		language = 'markdown',
		label = '',
		id = undefined,
		rows = 12,
		readonly = false,
		hint = '',
		accept = '',
		onfile = undefined,
		maxBytes = 1024 * 1024
	}: {
		value?: string;
		language?: CodeLanguage;
		label?: string;
		id?: string;
		rows?: number;
		readonly?: boolean;
		hint?: string;
		accept?: string;
		// Called after a successful load with the picked file's name, so the page
		// can derive a target name instead of making the user retype it.
		onfile?: (filename: string) => void;
		maxBytes?: number;
	} = $props();

	// Auto-associate label and control when no explicit id is given (a11y).
	const uid = $props.id();
	const controlId = $derived(id ?? uid);

	let ta = $state<HTMLTextAreaElement | undefined>(undefined);
	let mirror = $state<HTMLElement | undefined>(undefined);
	let fileInput = $state<HTMLInputElement | undefined>(undefined);
	let loadError = $state('');

	const canLoad = $derived(!readonly && !!accept);

	async function onPick(e: Event) {
		const input = e.target as HTMLInputElement;
		const file = input.files?.[0];
		input.value = ''; // let the same file be picked again after an edit
		if (!file) return;

		loadError = '';
		if (file.size > maxBytes) {
			loadError = `That file is ${Math.ceil(file.size / 1024)} KB — the limit is ${Math.floor(maxBytes / 1024)} KB.`;
			return;
		}
		try {
			value = await file.text();
			onfile?.(file.name);
			queueMicrotask(syncScroll);
		} catch {
			loadError = "That file couldn't be read as text.";
		}
	}

	// Trailing newline so an empty last line still occupies height in the <pre>.
	const html = $derived(hljs.highlight(value, { language }).value + '\n');
	// Line height = 0.75rem font × 1.625 ≈ 1.22rem; +1.5rem for the p-3 padding.
	const heightStyle = $derived(`height: calc(${rows} * 1.22rem + 1.5rem)`);

	function syncScroll() {
		if (ta && mirror) {
			mirror.scrollTop = ta.scrollTop;
			mirror.scrollLeft = ta.scrollLeft;
		}
	}
</script>

<div class="space-y-1">
	{#if label || canLoad}
		<div class="flex items-end justify-between gap-3">
			<label for={controlId} class="text-sm font-medium">{label}</label>
			{#if canLoad}
				<input
					bind:this={fileInput}
					type="file"
					{accept}
					class="hidden"
					onchange={onPick}
					aria-hidden="true"
					tabindex="-1"
				/>
				<button
					type="button"
					onclick={() => fileInput?.click()}
					class="ui-control inline-flex shrink-0 items-center gap-1.5 px-2.5 py-1 text-xs font-medium text-surface-700-300 transition hover:bg-surface-100-900"
				>
					<Upload size={13} />Load file
				</button>
			{/if}
		</div>
	{/if}

	{#if readonly}
		<pre
			class="code-editor-text ui-control overflow-auto p-3"
			style={heightStyle}><code class="hljs-tokens">{@html html}</code></pre>
	{:else}
		<div class="ui-control relative overflow-hidden" style={heightStyle}>
			<pre
				bind:this={mirror}
				aria-hidden="true"
				class="code-editor-text pointer-events-none absolute inset-0 overflow-hidden p-3"><code
					class="hljs-tokens">{@html html}</code></pre>
			<textarea
				bind:this={ta}
				bind:value
				id={controlId}
				onscroll={syncScroll}
				oninput={syncScroll}
				spellcheck="false"
				autocomplete="off"
				autocapitalize="off"
				class="code-editor-text absolute inset-0 resize-none overflow-auto bg-transparent p-3 text-transparent outline-none"
				style="caret-color: var(--color-surface-950-50)"
			></textarea>
		</div>
	{/if}

	{#if loadError}
		<p class="text-xs text-error-600-400">{loadError}</p>
	{:else if hint}
		<p class="text-xs text-surface-600-400">{hint}</p>
	{/if}
</div>

<style>
	/* The overlay only works if both layers render text identically — one class
	   owns every metric that affects glyph position. */
	.code-editor-text {
		font-family: var(--font-mono);
		font-size: 0.75rem;
		line-height: 1.625;
		white-space: pre-wrap;
		word-break: break-word;
		tab-size: 2;
	}
</style>
