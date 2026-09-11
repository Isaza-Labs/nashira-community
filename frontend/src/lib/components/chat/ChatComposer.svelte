<script lang="ts">
	import { Send, Square, Paperclip, X } from 'lucide-svelte';
	import { toast } from '$lib/components/ui';
	import type { ChatAttachment } from '$lib/stores/chat.svelte';
	import ModelPicker from './ModelPicker.svelte';

	// Matches parse_file's server-side cap; a bigger file would attach fine and
	// then fail opaquely when the agent tries to read it.
	const MAX_ATTACHMENT_BYTES = 5 * 1024 * 1024;

	// The message input. Enter sends, Shift+Enter inserts a newline. Files can be
	// attached (inlined into the turn for the agent). While a turn streams, the send
	// button becomes a stop button.
	let {
		onsend,
		onstop,
		streaming = false,
		providerId = null,
		model = null,
		providerName = null,
		onselectmodel
	}: {
		onsend: (text: string, attachments: ChatAttachment[]) => void;
		onstop?: () => void;
		streaming?: boolean;
		// The provider answering this thread, and the callback to change it. The
		// picker is shown only when the parent wires the callback, so a composer
		// reused somewhere without a provider concept stays as it was.
		providerId?: string | null;
		model?: string | null;
		providerName?: string | null;
		onselectmodel?: (providerId: string, model: string, providerName: string) => void;
	} = $props();

	let value = $state('');
	let files = $state<ChatAttachment[]>([]);
	let ta = $state<HTMLTextAreaElement | undefined>(undefined);
	let fileInput = $state<HTMLInputElement | undefined>(undefined);

	const canSend = $derived(!!value.trim() || files.length > 0);

	// The caret belongs in this box: it is the only thing on the page anyone types
	// into, and every turn ends by handing focus back to nowhere — clicking Send
	// moves it to the button, which is then replaced by the Stop button and
	// destroyed, leaving focus on <body> and the user clicking back in every time.
	//
	// It is only taken back from `body` or from a button (i.e. from nothing, or from
	// the control that just did its job), never from another field someone has
	// deliberately clicked into.
	function refocus() {
		const active = document.activeElement;
		const stealable =
			!active || active === document.body || active === ta || active.tagName === 'BUTTON';
		if (stealable) ta?.focus();
	}

	let focusedOnce = false;
	let wasStreaming = false;

	$effect(() => {
		// Read first so the effect tracks it: the interesting moment is the turn
		// ending, when the Stop button disappears.
		const nowStreaming = streaming;
		if (!focusedOnce && ta) {
			focusedOnce = true;
			ta.focus();
		} else if (wasStreaming && !nowStreaming) {
			refocus();
		}
		wasStreaming = nowStreaming;
	});

	function submit() {
		if (!canSend || streaming) return;
		onsend(value.trim(), files);
		value = '';
		files = [];
		queueMicrotask(() => {
			autosize();
			refocus();
		});
	}

	function onKeydown(e: KeyboardEvent) {
		if (e.key === 'Enter' && !e.shiftKey) {
			e.preventDefault();
			submit();
		}
	}

	function autosize() {
		if (!ta) return;
		ta.style.height = 'auto';
		ta.style.height = `${Math.min(ta.scrollHeight, 200)}px`;
	}

	function fileToBase64(f: File): Promise<string> {
		return new Promise((resolve, reject) => {
			const r = new FileReader();
			r.onload = () => {
				const s = r.result as string;
				resolve(s.slice(s.indexOf(',') + 1)); // strip the data: prefix
			};
			r.onerror = () => reject(r.error);
			r.readAsDataURL(f);
		});
	}

	async function onPick(e: Event) {
		const input = e.target as HTMLInputElement;
		for (const f of Array.from(input.files ?? [])) {
			if (f.size > MAX_ATTACHMENT_BYTES) {
				toast.error(`"${f.name}" is too large`, { description: 'Attachments are limited to 5 MB.' });
				continue;
			}
			try {
				const contentBase64 = await fileToBase64(f);
				files = [...files, { filename: f.name, contentBase64 }];
			} catch {
				toast.error(`Couldn't read "${f.name}"`);
			}
		}
		input.value = ''; // let the same file be picked again
		refocus(); // attaching a file is a detour, not a change of subject
	}

	function removeFile(i: number) {
		files = files.filter((_, idx) => idx !== i);
		refocus();
	}
</script>

<div class="space-y-2">
	{#if onselectmodel}
		<div class="flex items-center">
			<ModelPicker {providerId} {model} {providerName} onselect={onselectmodel} />
		</div>
	{/if}

	{#if files.length > 0}
		<div class="flex flex-wrap gap-1.5">
			{#each files as f, i (i)}
				<span
					class="inline-flex items-center gap-1.5 rounded-md bg-surface-200-800 px-2 py-1 text-xs text-surface-800-200"
				>
					<Paperclip size={12} class="shrink-0 text-surface-600-400" />
					<span class="max-w-[180px] truncate">{f.filename}</span>
					<button type="button" aria-label="Remove attachment" onclick={() => removeFile(i)}>
						<X size={12} class="text-surface-600-400 hover:text-surface-800-200" />
					</button>
				</span>
			{/each}
		</div>
	{/if}

	<form
		class="flex items-end gap-2 rounded-xl border border-surface-300-700 bg-surface-50-900 p-2 focus-within:border-primary-500 dark:focus-within:border-primary-400"
		onsubmit={(e) => {
			e.preventDefault();
			submit();
		}}
	>
		<input bind:this={fileInput} type="file" multiple class="hidden" onchange={onPick} />
		<button
			type="button"
			onclick={() => fileInput?.click()}
			aria-label="Attach files"
			class="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-surface-600-400 transition hover:bg-surface-200-800"
		>
			<Paperclip size={16} />
		</button>
		<textarea
			bind:this={ta}
			bind:value
			oninput={autosize}
			onkeydown={onKeydown}
			rows={1}
			placeholder="Message Nashira…"
			aria-label="Message"
			class="max-h-[200px] flex-1 resize-none bg-transparent px-1 py-1.5 text-sm outline-none placeholder:text-surface-600-400"
		></textarea>
		{#if streaming}
			<button
				type="button"
				onclick={onstop}
				aria-label="Stop generating"
				class="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-surface-200-800 text-surface-700-300 transition hover:bg-surface-300-700"
			>
				<Square size={15} />
			</button>
		{:else}
			<button
				type="submit"
				disabled={!canSend}
				aria-label="Send message"
				class="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-primary-500 text-white transition hover:bg-primary-600 disabled:opacity-40 dark:bg-primary-400 dark:text-primary-950 dark:hover:bg-primary-300"
			>
				<Send size={15} />
			</button>
		{/if}
	</form>
</div>
