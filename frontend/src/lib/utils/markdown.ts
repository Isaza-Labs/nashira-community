// Safe markdown rendering for anything produced by the LLM (chat bubbles, future
// tool-result previews). The user content is HTML-escaped *before* being fed to
// marked, so raw <script> or <img onerror=…> tags the model may emit can't reach
// the DOM. Marked then renders a whitelisted subset of markdown (GFM tables +
// lists + code + bold/italic + headings). Links are rewritten to text-only so a
// hostile markdown link can't turn into a click target — with one exception, this
// app's own download endpoints, which are turned into buttons (see below).
//
// Lifted near-verbatim from flow-weaver. Usage:
//   import { renderMarkdownSafe } from '$lib/utils/markdown';
//   <div class="prose">{@html renderMarkdownSafe(message)}</div>

import { marked, type MarkedOptions } from 'marked';

// One renderer instance reused everywhere. Customizing per-call would recompile
// the same rules on every message render.
const renderer = new marked.Renderer();

// The only hrefs the agent is allowed to hand back: a file it just produced.
// There are two kinds and both have to be recognized — matching only `export`
// silently dropped every link to a report, which is what `save_report`,
// `list_reports`, `read_report` and the workflow snippet handler all return.
export type DownloadArtifact = { kind: 'export' | 'report'; id: string };

const GUID = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';

// Matches the `download_url` the tools return (`/api/export/{id}/download` and
// `/api/reports/{id}/download`), with or without an origin in front since the
// model sometimes writes it out in full, and with or without a file name pinned
// on the end — a shape the model invents often enough to be worth accepting.
const ARTIFACT_FILE_NAME = String.raw`(?:/[\w.()\-]{1,80}\.[A-Za-z0-9]{1,8})?`;
const ARTIFACT_DOWNLOAD_HREF = new RegExp(
	String.raw`^(?:https?://[^/?#\s]+)?/?api/(export|reports)/(${GUID})/download${ARTIFACT_FILE_NAME}/?(?:[?#]\S*)?$`,
	'i'
);

// The same URL loose in prose, so it can be linkified before parsing.
const BARE_ARTIFACT_URL = new RegExp(
	String.raw`(?:https?://[^/?#\s]+)?/api/(?:export|reports)/${GUID}/download${ARTIFACT_FILE_NAME}`,
	'gi'
);

// Fenced blocks, code spans and existing link destinations: skipped by the
// linkifier so it can't rewrite a URL the author already framed deliberately.
const CODE_OR_LINK_TARGET = /(```[\s\S]*?```|`[^`\n]*`|\]\([^)\s]*\))/g;

// A code span holding nothing but a download URL, so it can be promoted whole —
// replacing inside the backticks would only produce literal `[Download](…)` text.
const CODE_SPAN = /^`([^`\n]*)`$/;

export function artifactFromHref(href: string | null | undefined): DownloadArtifact | null {
	const match = ARTIFACT_DOWNLOAD_HREF.exec((href ?? '').trim());
	if (!match) return null;
	return {
		kind: match[1].toLowerCase() === 'reports' ? 'report' : 'export',
		id: match[2].toLowerCase()
	};
}

// Only the shapes a file name actually takes. The link text is otherwise a
// sentence ("the CSV you asked for"), which would make a terrible file name —
// in that case the name from the response's Content-Disposition is used instead.
const FILE_NAME_TEXT = /^[\w][\w .()\-]{0,80}\.[A-Za-z0-9]{1,8}$/;

function attr(value: string): string {
	// `&` and `<` are already escaped upstream; quotes are not, and here the value
	// does land inside an attribute.
	return value.replaceAll('"', '&quot;').replaceAll("'", '&#39;');
}

// Strip <a> — the text survives, the href doesn't. Removes the most common
// phishing vector for LLM output without losing readability. The exception is an
// export or report artifact: those endpoints need the bearer token, so a plain
// anchor would navigate away and 401 rather than download. It renders as a button
// carrying the artifact kind and id, and ChatMarkdown performs the authenticated
// fetch on click.
renderer.link = ({ href, text }) => {
	const artifact = artifactFromHref(href);
	if (!artifact) return text;

	const label = text.trim() || 'Download';
	const fileName = FILE_NAME_TEXT.test(label) ? ` data-file-name="${attr(label)}"` : '';
	return (
		`<button type="button" class="md-download" data-artifact-kind="${artifact.kind}" ` +
		`data-artifact-id="${artifact.id}"${fileName}>` +
		'<svg class="md-download-icon" viewBox="0 0 16 16" width="12" height="12" aria-hidden="true" focusable="false">' +
		'<path d="M8 1.75v7.5m0 0 2.75-2.75M8 9.25 5.25 6.5M2.75 12.5h10.5" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>' +
		`</svg><span class="md-download-label">${label}</span></button>`
	);
};

// Images are rare in our flows and a data: URI is a viable exfil path. Drop.
renderer.image = ({ text }) => text || '';

// A ```mermaid fence becomes a drawn diagram instead of a wall of arrow syntax.
//
// The agent emits these constantly — every python_snippet it proposes carries a
// `logic_diagram_mermaid`, and Skills/mermaid.md has it write the diagram out
// before asking for approval. Rendered as code, the one artefact whose entire
// purpose is being readable at a glance was the least readable thing in the
// answer.
//
// Only a placeholder is emitted here: this module is a pure string renderer and
// mermaid is an async, DOM-measuring library, so ChatMarkdown mounts the real
// component onto these nodes after the HTML lands.
//
// `token.text` is the fence body as it reached marked — which is to say already
// run through escapeHtml below, since that runs over the whole document first.
// Dropping it into an attribute and reading it back out of `dataset` undoes
// exactly that escaping, so the component receives the source the model wrote.
//
// Every other language is emitted here too rather than falling through to marked's
// default, which escapes the body a SECOND time (`escape(text, true)`, encoding
// unconditionally) — so `if x < 5 & y:` reached the bubble as the literal text
// `if x &lt; 5 &amp; y:`, and the block's Copy button handed that to whoever pasted
// it into a device. Pre-escaped text goes in as it is; a `<` cannot open a tag once
// it is already `&lt;`.
renderer.code = ({ text, lang }) => {
	const language = (lang ?? '').trim().split(/\s+/)[0].toLowerCase();
	if (language === 'mermaid') {
		return `<div class="md-mermaid" data-mermaid="${attr(text)}"></div>`;
	}
	// Only the characters a class name may contain, so a fence info string cannot
	// close the attribute or add one of its own.
	const safeLang = language.replace(/[^a-z0-9+#._-]/g, '');
	const cls = safeLang ? ` class="language-${safeLang}"` : '';
	return `<pre><code${cls}>${text.replace(/\n+$/, '')}\n</code></pre>\n`;
};

// Same double-escape, same fix, one severity up: inline code is where every device
// name, interface and CLI fragment in an answer lives, so `Gi0/1 <-> Gi0/2` reading
// back as `Gi0/1 &lt;-&gt; Gi0/2` mid-sentence is the version people actually saw.
renderer.codespan = ({ text }) => `<code>${text}</code>`;

const OPTIONS: MarkedOptions = {
	breaks: true, // treat single newlines as <br> — matches how the LLM writes
	gfm: true, // GitHub-flavored: tables, strikethrough, task lists
	renderer
};

function escapeHtml(input: string): string {
	// Escape `&` and `<` only. `>` is deliberately NOT escaped: it can't open a
	// tag without a `<` in front (already neutralized), and markdown uses a
	// leading `>` for blockquotes — escaping it would break them. Quotes are left
	// alone because marked emits text nodes, not attribute values, so quote-based
	// attribute injection isn't possible once `<` is gone.
	return input.replaceAll('&', '&amp;').replaceAll('<', '&lt;');
}

// A bare download URL in prose is the same file, just written without markdown
// link syntax. Promote it so it reaches the renderer above instead of dying as
// text.
function linkifyBareArtifacts(markdown: string): string {
	// split() with a capturing group keeps the delimiters at the odd indices —
	// those are the code/link spans.
	return markdown
		.split(CODE_OR_LINK_TARGET)
		.map((part, i) => {
			if (i % 2 === 0) return part.replace(BARE_ARTIFACT_URL, (url) => `[Download](${url})`);
			// A code/link span, left exactly as it came — except a code span whose
			// whole body is one download URL. The model writes the link that way
			// often enough that rendering it as code is the same as losing it.
			const inner = CODE_SPAN.exec(part)?.[1].trim();
			return inner && artifactFromHref(inner) ? `[Download](${inner})` : part;
		})
		.join('');
}

export function renderMarkdownSafe(text: string | null | undefined): string {
	if (!text) return '';
	try {
		// marked.parse is synchronous when no async-aware extension is enabled.
		return marked.parse(linkifyBareArtifacts(escapeHtml(text)), OPTIONS) as string;
	} catch {
		// Fall back to the pre-escaped text so a parse error still renders
		// something — worst case the user sees their plain text.
		return escapeHtml(text);
	}
}
