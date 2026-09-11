<script lang="ts">
	import { untrack } from 'svelte';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import { chatHub, type ChatAttachment, type ChatSession } from '$lib/stores/chat.svelte';
	import { getConversation } from '$lib/api/conversations.api';
	import ConversationList from '$lib/components/chat/ConversationList.svelte';
	import ChatMessage from '$lib/components/chat/ChatMessage.svelte';
	import ChatComposer from '$lib/components/chat/ChatComposer.svelte';
	import { toast } from '$lib/components/ui';
	import { ArrowDown } from 'lucide-svelte';

	const routeId = $derived(page.params.conversationId ?? null);

	// The session this route shows. Sessions live in the hub for the whole tab, so
	// navigating here only *selects* one — a turn streaming in another conversation
	// keeps streaming in its own session, untouched.
	let session = $state<ChatSession>(chatHub.sessionFor(page.params.conversationId ?? null));

	let listRefresh = $state(0);
	let loadingId = $state<string | null>(null);
	let scroller = $state<HTMLDivElement | undefined>(undefined);
	let stick = $state(true);

	// Route -> session. Keyed ONLY on routeId (session state is read untracked) so a
	// conversation id arriving mid-stream doesn't retrigger this and wipe the turn.
	$effect(() => {
		const id = routeId;
		untrack(() => reconcile(id));
	});

	function reconcile(id: string | null) {
		const s = chatHub.sessionFor(id);
		if (s !== session) {
			session = s;
			stick = true;
		}
		if (!id) return;
		// A session that already holds messages — finished or still streaming — is
		// strictly newer than the server's stored history: fetch only into blank ones.
		if (s.messages.length > 0 || s.sending) return;
		if (id === loadingId) return; // load already in flight
		loadingId = id;
		getConversation(id)
			// Re-checked at arrival: a fetch that raced a send() must not overwrite
			// the turn the user just added with the older stored history.
			.then((c) => {
				if (s.messages.length === 0) s.setHistory(c.id, c.messages, { providerId: c.aiProviderId, model: c.model });
			})
			.catch((e) => {
				toast.fromError(e, "Couldn't load that conversation");
				goto('/chat', { replaceState: true });
			})
			.finally(() => {
				if (loadingId === id) loadingId = null;
			});
	}

	// Session -> route. When a brand-new conversation gets its server id mid-stream,
	// reflect it in the URL so a refresh or deep link resolves to it. The reconcile
	// this triggers re-keys the draft in the hub and lands on the same instance.
	$effect(() => {
		const cid = session.conversationId;
		if (cid && !routeId) {
			goto(`/chat/${cid}`, { replaceState: true, noScroll: true });
		}
	});

	// Auto-scroll: follow the stream while the user is near the bottom.
	$effect(() => {
		void session.messages; // re-run on every transcript change (reassigned per token)
		if (stick && scroller) scroller.scrollTop = scroller.scrollHeight;
	});

	function onScroll() {
		if (!scroller) return;
		stick = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 80;
	}

	function jumpToLatest() {
		stick = true;
		if (scroller) scroller.scrollTop = scroller.scrollHeight;
	}

	async function onSend(text: string, attachments: ChatAttachment[] = []) {
		stick = true;
		// Pin the instance: if the user switches threads mid-turn, the stream must
		// keep folding into the session it started on, not whichever is displayed.
		const target = session;
		await target.send(text, { attachments });
		listRefresh++; // refresh the sidebar (new/updated title + ordering)
	}

	function onNew() {
		if (routeId) goto('/chat');
		else session = chatHub.freshDraft();
	}
</script>

<svelte:head><title>Chat · Nashira</title></svelte:head>

<div class="flex h-full">
	<!-- Second-level panel: flush against the app sidebar, full height, so the
	     shell reads nav → threads → transcript with no dead strip between. -->
	<aside
		class="hidden w-64 shrink-0 flex-col border-r border-surface-200-800/60 bg-surface-50-950/50 md:flex"
	>
		<ConversationList activeId={session.conversationId} refreshKey={listRefresh} onnew={onNew} />
	</aside>

	<section class="relative flex min-w-0 flex-1 flex-col px-4 pt-3 md:px-6">
		<div class="mb-2 flex items-center justify-between md:hidden">
			<span class="text-sm font-medium">Chat</span>
			<button
				type="button"
				onclick={onNew}
				class="rounded-md border border-surface-300-700 px-2.5 py-1 text-xs hover:bg-surface-100-900"
			>
				New chat
			</button>
		</div>

		<div bind:this={scroller} onscroll={onScroll} class="min-h-0 flex-1 overflow-y-auto">
			{#if session.messages.length === 0}
				<div class="flex h-full flex-col items-center justify-center gap-2 px-4 text-center">
					<h1 class="text-lg font-semibold">How can I help?</h1>
					<p class="max-w-sm text-sm text-surface-600-400">
						Ask Nashira about your devices, workflows, git repositories, knowledge base, and more.
					</p>
				</div>
			{:else}
				<div class="mx-auto flex w-full max-w-3xl flex-col gap-4 py-4">
					{#each session.messages as m (m.id)}
						<ChatMessage
							message={m}
							onapprove={(mid, tool) => session.approve(mid, tool)}
							oncancel={(mid) => session.dismissConfirmation(mid)}
						/>
					{/each}
				</div>
			{/if}
		</div>

		{#if !stick && session.messages.length > 0}
			<button
				type="button"
				onclick={jumpToLatest}
				aria-label="Jump to latest"
				class="absolute bottom-24 left-1/2 inline-flex -translate-x-1/2 items-center gap-1.5 rounded-full border border-surface-300-700 bg-surface-100-900 px-3 py-1.5 text-xs shadow-lg shadow-black/20 hover:bg-surface-200-800"
			>
				<ArrowDown size={13} />Latest
			</button>
		{/if}

		<div class="mx-auto w-full max-w-3xl pb-3 pt-3">
			<ChatComposer
				onsend={onSend}
				onstop={() => session.stop()}
				streaming={session.sending}
				providerId={session.providerId}
				model={session.model}
				providerName={session.providerName}
				onselectmodel={(p, m, name) => session.selectModel(p, m, name)}
			/>
			<p class="mt-1.5 px-1 text-center text-[11px] text-surface-600-400">
				Nashira can make mistakes. Verify important actions before confirming them.
			</p>
		</div>
	</section>
</div>
