<script lang="ts">
	// Every agent session in the installation, and what the agent actually did inside
	// each one. The audit trail next door answers "what changed" and is deliberately
	// narrow: mutations only, hash-chained. This page answers the question that comes
	// first — "what happened?" — including the turns that changed nothing, the tool
	// calls that failed, and the exact payloads that went out.
	import {
		listSessions,
		getSession,
		type SessionSummary,
		type SessionDetail,
		type SessionTurn
	} from '$lib/api/sessions.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		EmptyState,
		Pagination,
		IconButton,
		Spinner,
		ErrorState,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { timeAgo, absolute } from '$lib/utils/time';
	import { toolLabel } from '$lib/utils/tool-labels';
	import { Eye, CheckCircle2, XCircle, ChevronRight } from 'lucide-svelte';

	let items = $state<SessionSummary[]>([]);
	let total = $state(0);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let offset = $state(0);
	const limit = 50;

	let detailOpen = $state(false);
	let detail = $state<SessionDetail | null>(null);
	let detailLoading = $state(false);

	// Which turns are expanded. Collapsed by default: a session is scanned for the
	// turn that went wrong, and only that one is read.
	let expanded = $state<Record<string, boolean>>({});

	const columns: Column[] = [
		{ key: 'title', header: 'Session', sortable: false },
		{ key: 'user', header: 'User', sortable: false },
		{ key: 'activity', header: 'Activity', sortable: false },
		{ key: 'tokens', header: 'Tokens', sortable: false, align: 'right' },
		{ key: 'updated', header: 'Last activity', sortable: false },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			const res = await listSessions(limit, offset);
			items = res.items;
			total = res.total;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		offset;
		load();
	});

	async function openDetail(s: SessionSummary) {
		detailOpen = true;
		detail = null;
		expanded = {};
		detailLoading = true;
		try {
			detail = await getSession(s.id);
		} catch (err) {
			toast.fromError(err, "Couldn't load that session");
			detailOpen = false;
		} finally {
			detailLoading = false;
		}
	}

	function toggleTurn(id: string) {
		expanded = { ...expanded, [id]: !expanded[id] };
	}

	function json(v: unknown): string {
		if (v == null) return '';
		try {
			return JSON.stringify(v, null, 2);
		} catch {
			return String(v);
		}
	}

	function turnTone(t: SessionTurn): 'success' | 'warning' | 'error' {
		if (t.status === 'completed') return 'success';
		if (t.status === 'awaiting_confirmation') return 'warning';
		// The answer arrived, just not all of it: worth a look, not a failure.
		if (t.status === 'truncated') return 'warning';
		return 'error';
	}
</script>

<svelte:head><title>Sessions · Nashira</title></svelte:head>

<RoleGate require="admin">
	{#snippet fallback()}
		<PageHeader title="Sessions" description="Every agent conversation, and what it did." />
		<div class="ui-surface px-4 py-16 text-center text-sm text-surface-600-400">
			Administrator access is required to read other people's sessions.
		</div>
	{/snippet}

	<PageHeader
		title="Sessions"
		description="Every agent conversation in this installation, with the model, the tool calls and the payloads behind each turn."
	/>

	<div class="mt-4 space-y-3">
		{#if error}
			<ErrorState {error} onRetry={load} />
		{:else}
			<DataTable {loading} {columns} rows={items} rowKey={(s) => s.id} empty="No sessions yet.">
				{#snippet cell(row, col)}
					{#if col.key === 'title'}
						<div class="max-w-[320px] truncate font-medium">{row.title || 'Untitled session'}</div>
						<div class="text-xs text-surface-600-400">{row.messageCount} messages</div>
					{:else if col.key === 'user'}
						{#if row.username}
							{row.username}
						{:else}
							<span class="text-surface-600-400">—</span>
						{/if}
					{:else if col.key === 'activity'}
						<div class="flex items-center gap-1.5">
							<span class="text-xs text-surface-600-400">
								{row.turnCount}
								{row.turnCount === 1 ? 'turn' : 'turns'} · {row.toolCallCount} tool calls
							</span>
							{#if row.failedTurnCount > 0}
								<Badge tone="error">{row.failedTurnCount} failed</Badge>
							{/if}
						</div>
					{:else if col.key === 'tokens'}
						<span class="tabular-nums text-xs text-surface-600-400">
							{row.tokensIn.toLocaleString()} / {row.tokensOut.toLocaleString()}
						</span>
					{:else if col.key === 'updated'}
						<span class="text-surface-600-400" title={absolute(row.updatedAt)}>
							{timeAgo(row.updatedAt)}
						</span>
					{:else if col.key === 'actions'}
						<IconButton label="Open session" onclick={() => openDetail(row)}>
							<Eye size={14} />
						</IconButton>
					{/if}
				{/snippet}
			</DataTable>

			<Pagination {total} {limit} {offset} onchange={(o) => (offset = o)} />
		{/if}
	</div>
</RoleGate>

<Modal bind:open={detailOpen} title={detail?.title || 'Session'} size="lg">
	{#if detailLoading}
		<div class="flex justify-center py-10"><Spinner /></div>
	{:else if detail}
		<div class="space-y-4 text-sm">
			<div class="grid grid-cols-2 gap-2 text-xs">
				<div><span class="text-surface-600-400">User:</span> {detail.username || '—'}</div>
				<div><span class="text-surface-600-400">Started:</span> {absolute(detail.createdAt)}</div>
				<div>
					<span class="text-surface-600-400">Turns:</span>
					{detail.turnCount} · {detail.toolCallCount} tool calls
				</div>
				<div>
					<span class="text-surface-600-400">Tokens:</span>
					{detail.tokensIn.toLocaleString()} in / {detail.tokensOut.toLocaleString()} out
				</div>
			</div>

			<div>
				<div class="mb-1.5 text-xs font-medium text-surface-600-400">Transcript</div>
				<div class="max-h-64 space-y-2 overflow-auto rounded-md bg-surface-100-900 p-3">
					{#each detail.messages as m, i (i)}
						<div>
							<div class="text-xs font-medium text-surface-600-400">{m.role}</div>
							<div class="whitespace-pre-wrap break-words text-xs">{m.content}</div>
						</div>
					{:else}
						<div class="text-xs text-surface-600-400">No stored messages.</div>
					{/each}
				</div>
			</div>

			<div>
				<div class="mb-1.5 text-xs font-medium text-surface-600-400">
					Turns — model, tool calls and payloads
				</div>
				{#if detail.turns.length === 0}
					<!-- Turns are recorded from the moment this feature shipped; a session
					     older than that has a transcript and nothing else. -->
					<EmptyState
						title="No turn telemetry"
						description="This session predates turn recording, or its turns were never written."
					/>
				{:else}
					<div class="space-y-1.5">
						{#each detail.turns as t (t.id)}
							<div class="overflow-hidden rounded-md border border-surface-200-800/80">
								<button
									type="button"
									class="flex w-full items-center gap-2 px-3 py-2 text-left hover:bg-surface-100-900"
									onclick={() => toggleTurn(t.id)}
								>
									<ChevronRight
										size={14}
										class="shrink-0 text-surface-600-400 transition-transform {expanded[t.id]
											? 'rotate-90'
											: ''}"
									/>
									<span class="min-w-0 flex-1 truncate text-xs">{t.userMessage || '(no prompt)'}</span>
									<Badge tone={turnTone(t)}>{t.status}</Badge>
									<span class="shrink-0 text-xs tabular-nums text-surface-600-400">
										{t.toolCallCount} tools · {t.elapsedMs} ms
									</span>
								</button>

								{#if expanded[t.id]}
									<div class="space-y-2 border-t border-surface-200-800/80 px-3 py-2">
										<div class="text-xs text-surface-600-400">
											{t.model} · {t.iterations} iterations · {t.tokensIn.toLocaleString()} in / {t.tokensOut.toLocaleString()}
											out · {absolute(t.startedAt)}
										</div>

										{#if t.error}
											<div class="rounded bg-error-500/10 px-2 py-1 text-xs text-error-700-300">
												{t.error}
											</div>
										{/if}

										{#each t.toolCalls as c, i (i)}
											<div class="rounded bg-surface-100-900 p-2">
												<div class="flex items-center gap-1.5 text-xs">
													{#if c.ok}
														<CheckCircle2 size={13} class="text-success-600-400" />
													{:else}
														<XCircle size={13} class="text-error-600-400" />
													{/if}
													<span class="font-medium">{toolLabel(c.name)}</span>
													<code class="text-surface-600-400">{c.name}</code>
													<span class="ml-auto tabular-nums text-surface-600-400">
														{c.elapsed_ms} ms
													</span>
												</div>
												{#if c.arguments != null}
													<pre
														class="mt-1 max-h-32 overflow-auto text-xs text-surface-700-300"><code
															>{json(c.arguments)}</code
														></pre>
												{/if}
												{#if c.result != null}
													<pre
														class="mt-1 max-h-40 overflow-auto border-t border-surface-200-800/60 pt-1 text-xs text-surface-600-400"><code
															>{json(c.result)}</code
														></pre>
												{/if}
											</div>
										{/each}

										{#if t.assistantText}
											<div>
												<div class="mb-0.5 text-xs font-medium text-surface-600-400">Answer</div>
												<div class="whitespace-pre-wrap break-words text-xs">{t.assistantText}</div>
											</div>
										{/if}
									</div>
								{/if}
							</div>
						{/each}
					</div>
				{/if}
			</div>
		</div>
	{/if}
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (detailOpen = false)}>Close</Button>
	{/snippet}
</Modal>
