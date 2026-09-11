<script lang="ts">
	import ChatMarkdown from './ChatMarkdown.svelte';
	import ChatToolCalls from './ChatToolCalls.svelte';
	import ChatThinkingDots from './ChatThinkingDots.svelte';
	import type { ChatMessage } from '$lib/stores/chat.svelte';
	import { Button, toast } from '$lib/components/ui';
	import { toolLabel } from '$lib/utils/tool-labels';
	import { copyText } from '$lib/utils/clipboard';
	import { AlertTriangle, Check, Copy, Paperclip, ShieldAlert } from 'lucide-svelte';

	let {
		message,
		onapprove,
		oncancel
	}: {
		message: ChatMessage;
		onapprove?: (messageId: number, toolName: string) => void;
		oncancel?: (messageId: number) => void;
	} = $props();

	const isUser = $derived(message.role === 'user');
	const thinking = $derived(
		message.streaming && message.content === '' && message.tools.length === 0
	);

	// Copying the whole turn. ChatMarkdown already puts a Copy on each fenced
	// block, which covers a command but not the answer around it — and nothing at
	// all covered the user's own message, which is the one people re-send after
	// editing a device name. The source is message.content, the markdown the model
	// actually emitted, not the rendered DOM: pasting `**bold**` back into a
	// prompt is what the next turn needs, and hand-selecting the bubble picks up
	// the tool-call chips above it.
	let copied = $state(false);
	let copyTimer: ReturnType<typeof setTimeout> | undefined;

	async function copyMessage() {
		if (!(await copyText(message.content))) {
			toast.error("Couldn't copy the message");
			return;
		}
		copied = true;
		clearTimeout(copyTimer);
		copyTimer = setTimeout(() => (copied = false), 1600);
	}

	$effect(() => () => clearTimeout(copyTimer));

	function argsPreview(args: unknown): string {
		if (args == null) return '';
		try {
			const s = typeof args === 'string' ? args : JSON.stringify(args);
			return s.length > 160 ? `${s.slice(0, 160)}…` : s;
		} catch {
			return '';
		}
	}
</script>

{#if isUser}
	<div class="group flex flex-col items-end gap-1">
		{#if message.content}
			<div class="flex max-w-[85%] items-end gap-1">
				{@render copyButton()}
				<div
					class="selection-on-fill min-w-0 whitespace-pre-wrap break-words rounded-2xl rounded-br-sm bg-primary-500 px-4 py-2.5 text-sm text-white dark:bg-primary-400 dark:text-primary-950"
				>
					{message.content}
				</div>
			</div>
		{/if}
		{#if message.attachments.length > 0}
			<div class="flex max-w-[85%] flex-wrap justify-end gap-1.5">
				{#each message.attachments as name, i (i)}
					<span
						class="inline-flex items-center gap-1.5 rounded-md bg-surface-200-800 px-2 py-1 text-xs text-surface-700-300"
					>
						<Paperclip size={12} class="shrink-0 text-surface-600-400" />
						<span class="max-w-[180px] truncate">{name}</span>
					</span>
				{/each}
			</div>
		{/if}
	</div>
{:else}
	<div class="group flex justify-start">
		<div class="min-w-0 max-w-[85%] space-y-2">
			{#if message.tools.length > 0}
				<ChatToolCalls calls={message.tools} />
			{/if}
			{#if thinking}
				<ChatThinkingDots />
			{:else if message.content}
				<div class="flex items-end gap-1">
					<div
						class="min-w-0 rounded-2xl rounded-bl-sm border border-surface-200-800/70 bg-surface-50-900 px-4 py-2.5 text-sm text-surface-900-100"
					>
						<ChatMarkdown content={message.content} />
					</div>
					{@render copyButton()}
				</div>
			{/if}

			{#if message.confirmation && !message.confirmation.resolved}
				{@const conf = message.confirmation}
				{@const elevated = conf.tier === 'elevated_confirm'}
				<!-- elevated_confirm marks the platform's highest-impact actions
				     (delete_user, promote_workflow, …) — the card escalates to the
				     error palette and a danger button so it cannot be mistaken for
				     a routine confirmation. -->
				<div
					class="rounded-lg border p-3 {elevated
						? 'border-error-500/50 bg-error-500/10'
						: 'border-warning-500/40 bg-warning-500/10'}"
				>
					<div class="flex items-start gap-2">
						<ShieldAlert
							size={16}
							class="mt-0.5 shrink-0 {elevated ? 'text-error-500' : 'text-warning-500'}"
						/>
						<div class="min-w-0 flex-1">
							<div class="text-sm font-medium text-surface-900-100">
								{elevated ? 'High-impact action — extra caution' : 'Confirmation required'}
							</div>
							<div class="mt-0.5 text-xs text-surface-600-400">
								The assistant wants to <span class="font-medium">{toolLabel(conf.toolName)}</span>.
								{#if elevated}This action is destructive or changes who can do what.{/if}
							</div>
							{#if argsPreview(conf.args)}
								<code class="mt-1 block truncate font-mono text-[11px] text-surface-600-400">
									{argsPreview(conf.args)}
								</code>
							{/if}
						</div>
					</div>
					<div class="mt-2 flex justify-end gap-2">
						<Button size="sm" variant="ghost" onclick={() => oncancel?.(message.id)}>Cancel</Button>
						<Button
							size="sm"
							variant={elevated ? 'danger' : 'primary'}
							onclick={() => onapprove?.(message.id, conf.toolName)}
						>
							{elevated ? 'Approve anyway' : 'Approve'}
						</Button>
					</div>
				</div>
			{/if}

			{#if message.error}
				<div
					class="flex items-start gap-2 rounded-md border border-error-500/40 bg-error-500/10 px-3 py-2 text-xs text-error-700-300"
					role="alert"
				>
					<AlertTriangle size={14} class="mt-0.5 shrink-0" />
					<span>{message.error}</span>
				</div>
			{/if}
		</div>
	</div>
{/if}

<!-- Hidden until the turn is hovered or the button itself is focused, so a long
     thread is not a column of buttons; `opacity` rather than `hidden` keeps it in
     the tab order and keeps the bubble from reflowing when it appears. -->
{#snippet copyButton()}
	<button
		type="button"
		onclick={copyMessage}
		aria-label={copied ? 'Message copied' : 'Copy message'}
		title={copied ? 'Copied' : 'Copy message'}
		class="mb-1 inline-flex h-6 w-6 shrink-0 items-center justify-center rounded-md transition hover:bg-surface-200-800 focus-visible:opacity-100 group-hover:opacity-100 {copied
			? 'text-success-700-300 opacity-100'
			: 'text-surface-600-400 opacity-0 hover:text-surface-800-200'}"
	>
		{#if copied}<Check size={13} />{:else}<Copy size={13} />{/if}
	</button>
{/snippet}
