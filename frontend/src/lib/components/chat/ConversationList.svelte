<script lang="ts">
	import {
		listConversations,
		deleteConversation,
		type ConversationSummary
	} from '$lib/api/conversations.api';
	import { confirm, toast, Spinner, ErrorState } from '$lib/components/ui';
	import { Plus, Trash2, MessageSquare } from 'lucide-svelte';

	// Sidebar of the caller's conversations. `refreshKey` is bumped by the parent
	// after a turn so titles / ordering update; `activeId` highlights the open one.
	let {
		activeId = null,
		refreshKey = 0,
		onnew
	}: { activeId?: string | null; refreshKey?: number; onnew?: () => void } = $props();

	let items = $state<ConversationSummary[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listConversations(50, 0)).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	// Reload on mount and whenever refreshKey changes.
	$effect(() => {
		refreshKey;
		load();
	});

	async function remove(c: ConversationSummary) {
		const ok = await confirm({
			title: 'Delete conversation?',
			message: c.title ?? 'This conversation will be removed.',
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteConversation(c.id);
			items = items.filter((x) => x.id !== c.id);
			toast.success('Conversation deleted');
		} catch (err) {
			toast.fromError(err, "Couldn't delete the conversation");
		}
	}
</script>

<div class="flex h-full flex-col">
	<div class="p-2">
		<button
			type="button"
			onclick={onnew}
			class="flex w-full items-center gap-2 rounded-lg border border-surface-300-700 px-3 py-2 text-sm font-medium text-surface-800-200 transition hover:bg-surface-100-900"
		>
			<Plus size={15} />New chat
		</button>
	</div>

	<div class="min-h-0 flex-1 overflow-y-auto px-2 pb-2">
		{#if loading && items.length === 0}
			<div class="flex justify-center py-6"><Spinner size="sm" /></div>
		{:else if error}
			<ErrorState {error} onRetry={load} compact />
		{:else if items.length === 0}
			<p class="px-3 py-6 text-center text-xs text-surface-600-400">No conversations yet.</p>
		{:else}
			<ul class="space-y-0.5">
				{#each items as c (c.id)}
					<li
						class="group flex items-center rounded-lg {c.id === activeId
							? 'bg-surface-200-800'
							: 'hover:bg-surface-100-900'}"
					>
						<a
							href={`/chat/${c.id}`}
							class="flex min-w-0 flex-1 items-center gap-2 px-3 py-2 text-sm {c.id === activeId
								? 'text-surface-900-100'
								: 'text-surface-700-300'}"
						>
							<MessageSquare size={14} class="shrink-0 text-surface-600-400" />
							<span class="flex-1 truncate">{c.title || 'Untitled'}</span>
						</a>
						<button
							type="button"
							onclick={() => remove(c)}
							aria-label="Delete conversation"
							class="mr-1 shrink-0 rounded p-1 text-surface-600-400 opacity-0 transition hover:bg-error-500/15 hover:text-error-600-400 focus:opacity-100 group-hover:opacity-100"
						>
							<Trash2 size={13} />
						</button>
					</li>
				{/each}
			</ul>
		{/if}
	</div>
</div>
