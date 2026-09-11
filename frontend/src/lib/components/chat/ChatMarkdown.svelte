<script lang="ts">
	// Renders the LLM's markdown output with chat-appropriate styling. The HTML
	// comes from renderMarkdownSafe (escape → marked → whitelisted renderer), so
	// {@html} here is safe against prompt injection or model-emitted <script> tags.
	import { mount, unmount } from 'svelte';
	import { renderMarkdownSafe } from '$lib/utils/markdown';
	import { copyText } from '$lib/utils/clipboard';
	import { downloadExport } from '$lib/api/exports.api';
	import { downloadReportById } from '$lib/api/reports.api';
	import { toast, Mermaid } from '$lib/components/ui';

	let { content = '' }: { content?: string | null } = $props();
	const html = $derived(renderMarkdownSafe(content));

	let root = $state<HTMLElement | undefined>(undefined);

	// The agent answers with CLI commands meant to be pasted into a device. Without
	// a copy affordance the operator hand-selects multi-line output — the most
	// repeated action in the product. The markdown arrives as an HTML string, so
	// the buttons are attached imperatively after each render (which also keeps
	// them correct while a reply is still streaming in).
	$effect(() => {
		void html; // re-attach whenever the rendered markdown changes
		const host = root;
		if (!host) return;

		const cleanups: (() => void)[] = [];

		for (const pre of Array.from(host.querySelectorAll('pre'))) {
			pre.classList.add('has-copy');

			const btn = document.createElement('button');
			btn.type = 'button';
			btn.className = 'md-copy';
			btn.textContent = 'Copy';
			btn.setAttribute('aria-label', 'Copy code block');

			let timer: ReturnType<typeof setTimeout> | undefined;
			const onClick = async () => {
				// innerText (not textContent) so the copied text keeps line breaks.
				// Read the inner <code>, not the <pre>: the button lives inside the
				// <pre>, so pre.innerText would append its own "Copy" label.
				// copyText falls back to execCommand on insecure origins.
				const source = pre.querySelector<HTMLElement>('code') ?? pre;
				const text =
					source === pre
						? Array.from(pre.childNodes)
								.filter((n) => n !== btn)
								.map((n) => (n instanceof HTMLElement ? n.innerText : (n.textContent ?? '')))
								.join('')
						: source.innerText;
				const ok = await copyText(text.replace(/\n+$/, ''));
				if (!ok) return; // both mechanisms failed — leave the label as-is
				btn.textContent = 'Copied';
				btn.classList.add('is-copied');
				clearTimeout(timer);
				timer = setTimeout(() => {
					btn.textContent = 'Copy';
					btn.classList.remove('is-copied');
				}, 1600);
			};

			btn.addEventListener('click', onClick);
			pre.appendChild(btn);
			cleanups.push(() => {
				clearTimeout(timer);
				btn.removeEventListener('click', onClick);
				btn.remove();
			});
		}

		// A ```mermaid fence left a placeholder div behind (see markdown.ts); the
		// renderer is a real component, so it is mounted onto the node rather than
		// stringified into it. Mounted per placeholder and unmounted with the rest of
		// the cleanups, which matters while a reply streams: the fence is re-rendered
		// on every chunk, and a half-written diagram is a parse error the component
		// shows and then replaces once the closing fence arrives.
		for (const node of Array.from(host.querySelectorAll<HTMLElement>('.md-mermaid'))) {
			const code = node.dataset.mermaid ?? '';
			if (!code.trim()) continue;
			const app = mount(Mermaid, { target: node, props: { code } });
			cleanups.push(() => {
				void unmount(app);
			});
		}

		// Export and report artifacts arrive as markdown links, which the renderer
		// turns into buttons: the endpoints are bearer-authenticated, so navigating
		// to the href would 401 instead of downloading. The fetch goes through the
		// API client and the blob is saved from memory.
		for (const btn of Array.from(host.querySelectorAll<HTMLButtonElement>('button.md-download'))) {
			const artifactId = btn.dataset.artifactId;
			if (!artifactId) continue;
			const isReport = btn.dataset.artifactKind === 'report';

			const label = btn.querySelector<HTMLElement>('.md-download-label');
			const original = label?.textContent ?? 'Download';

			const onClick = async () => {
				if (btn.disabled) return;
				btn.disabled = true;
				if (label) label.textContent = 'Downloading…';
				try {
					await (isReport
						? downloadReportById(artifactId, btn.dataset.fileName)
						: downloadExport(artifactId, btn.dataset.fileName));
				} catch (e) {
					toast.fromError(e, "Couldn't download the file");
				} finally {
					btn.disabled = false;
					if (label) label.textContent = original;
				}
			};

			btn.addEventListener('click', onClick);
			cleanups.push(() => btn.removeEventListener('click', onClick));
		}

		return () => cleanups.forEach((fn) => fn());
	});
</script>

<div class="md" bind:this={root}>{@html html}</div>

<style>
	.md {
		line-height: 1.55;
		color: inherit;
		word-break: break-word;
	}

	/* Heading scale. Tight, because chat lives in a narrow column, but the steps
	   have to stay visible or a long answer reads as one undifferentiated wall.
	   Extra top margin does most of that work — it groups each heading with the
	   body text under it. */
	.md :global(h1) {
		font-size: 1.15rem;
		font-weight: 650;
		letter-spacing: -0.02em;
		margin: 1em 0 0.3em;
	}
	.md :global(h2) {
		font-size: 1.05rem;
		font-weight: 650;
		letter-spacing: -0.015em;
		margin: 1em 0 0.3em;
	}
	.md :global(h3),
	.md :global(h4) {
		font-size: 0.92rem;
		font-weight: 650;
		margin: 0.85em 0 0.25em;
		color: var(--color-surface-900-100);
	}

	/* Paragraphs and lists: minimal vertical rhythm. */
	.md :global(p) {
		margin: 0.25em 0;
	}
	/* Tailwind's preflight strips list markers globally, so they have to be put
	   back explicitly here — without this, every bulleted and numbered list the
	   agent emits renders as flat indented text. */
	.md :global(ul),
	.md :global(ol) {
		margin: 0.35em 0;
		padding-left: 1.35em;
	}
	.md :global(ul) {
		list-style: disc outside;
	}
	.md :global(ol) {
		list-style: decimal outside;
	}
	.md :global(ul ul) {
		list-style: circle outside;
	}
	.md :global(li::marker) {
		color: var(--color-surface-600-400);
	}
	/* Task lists render their own checkbox — a marker as well would double up. */
	.md :global(li:has(> input[type='checkbox'])) {
		list-style: none;
		margin-left: -1.35em;
	}
	.md :global(li) {
		margin: 0.1em 0;
	}
	.md :global(ul ul),
	.md :global(ol ol),
	.md :global(ul ol),
	.md :global(ol ul) {
		margin: 0.1em 0;
	}

	.md :global(input[type='checkbox']) {
		vertical-align: middle;
		margin-right: 0.4em;
		accent-color: var(--color-primary-500);
	}

	/* Download button for an export artifact (see markdown.ts). Reads as a link
	   with an affordance, not as a form control — it sits inline in a sentence. */
	.md :global(.md-download) {
		display: inline-flex;
		align-items: center;
		gap: 0.35em;
		vertical-align: baseline;
		margin: 0 0.05em;
		padding: 0.1em 0.5em;
		border: 1px solid color-mix(in oklab, var(--color-primary-500) 45%, transparent);
		border-radius: 6px;
		background: color-mix(in oklab, var(--color-primary-500) 10%, transparent);
		color: var(--color-primary-700-300);
		font: inherit;
		font-weight: 550;
		line-height: 1.4;
		cursor: pointer;
		transition:
			background 140ms ease,
			border-color 140ms ease;
	}
	.md :global(.md-download:hover:not(:disabled)) {
		background: color-mix(in oklab, var(--color-primary-500) 20%, transparent);
		border-color: var(--color-primary-500);
	}
	.md :global(.md-download:disabled) {
		cursor: progress;
		opacity: 0.7;
	}
	.md :global(.md-download-icon) {
		flex-shrink: 0;
	}

	.md :global(strong) {
		font-weight: 600;
		color: var(--color-surface-900-100);
	}
	.md :global(em) {
		font-style: italic;
	}
	.md :global(del) {
		opacity: 0.6;
	}

	/* Inline code. */
	.md :global(code) {
		font-family: var(--font-mono);
		font-size: 0.85em;
		padding: 0.08em 0.35em;
		border-radius: 4px;
		background: color-mix(in oklab, var(--color-surface-500) 16%, transparent);
		/* Mode-aware pair: the single -300 stop is a pale cyan that disappears on a
		   light bubble. Device names and CLI fragments ride in inline code, so this
		   has to stay readable in both modes. */
		color: var(--color-primary-700-300);
	}

	/* Fenced code blocks. */
	.md :global(pre.has-copy) {
		position: relative;
	}
	/* The button sits over the block and only surfaces on hover or keyboard
	   focus, so it never competes with the code itself. */
	.md :global(.md-copy) {
		position: absolute;
		top: 0.35em;
		right: 0.35em;
		padding: 0.1em 0.45em;
		border-radius: 5px;
		border: 1px solid var(--color-surface-300-700);
		background: var(--color-surface-50-950);
		color: var(--color-surface-700-300);
		font-family: var(--font-sans);
		font-size: 10px;
		font-weight: 500;
		line-height: 1.6;
		cursor: pointer;
		opacity: 0;
		transition:
			opacity 140ms ease,
			background 140ms ease;
	}
	.md :global(pre.has-copy:hover .md-copy),
	.md :global(.md-copy:focus-visible) {
		opacity: 1;
	}
	.md :global(.md-copy:hover) {
		background: var(--color-surface-100-900);
	}
	.md :global(.md-copy.is-copied) {
		color: var(--color-success-700-300);
		border-color: var(--color-success-500);
		opacity: 1;
	}

	/* A drawn diagram gets the same tinted panel a code block gets — it is the same
	   kind of block in the flow of the answer — and scrolls rather than pushing the
	   bubble wide when the flowchart is genuinely bigger than the column. */
	.md :global(.md-mermaid) {
		margin: 0.5em 0;
		padding: 0.6em;
		border-radius: 6px;
		background: color-mix(in oklab, var(--color-surface-500) 12%, transparent);
		overflow-x: auto;
	}

	.md :global(pre) {
		margin: 0.5em 0;
		padding: 0.6em 0.8em;
		border-radius: 6px;
		background: color-mix(in oklab, var(--color-surface-500) 12%, transparent);
		overflow-x: auto;
		font-family: var(--font-mono);
		font-size: 0.8rem;
		line-height: 1.45;
	}
	.md :global(pre code) {
		padding: 0;
		background: transparent;
		color: inherit;
	}

	.md :global(blockquote) {
		margin: 0.4em 0;
		padding: 0.1em 0.9em;
		border-left: 2px solid color-mix(in oklab, var(--color-primary-500) 60%, transparent);
		color: var(--color-surface-700-300);
		font-style: italic;
	}

	/* Tables — GFM pipes become real tables; scroll if they blow out the bubble. */
	.md :global(table) {
		display: block;
		max-width: 100%;
		overflow-x: auto;
		border-collapse: collapse;
		margin: 0.5em 0;
		font-size: 0.82rem;
	}
	.md :global(thead) {
		background: color-mix(in oklab, var(--color-surface-500) 10%, transparent);
	}
	.md :global(th),
	.md :global(td) {
		padding: 0.35em 0.6em;
		text-align: left;
		border: 1px solid color-mix(in oklab, var(--color-surface-500) 25%, transparent);
	}
	.md :global(th) {
		font-weight: 600;
	}

	.md :global(hr) {
		margin: 0.8em 0;
		border: 0;
		border-top: 1px solid color-mix(in oklab, var(--color-surface-500) 20%, transparent);
	}

	/* First/last child get no outer margin so the bubble padding wins. */
	.md :global(> :first-child) {
		margin-top: 0;
	}
	.md :global(> :last-child) {
		margin-bottom: 0;
	}
</style>
