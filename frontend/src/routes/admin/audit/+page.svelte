<script lang="ts">
	// The audit screen: two trails side by side.
	//
	// Laid out after Flow Weaver's — tabs, category chips, an ANDed filter row, rows
	// that expand in place — because that is the shape people already read audit in.
	// What Nashira adds sits on top rather than replacing it: the chain verifier, the
	// hash column, and a real total from the server so pagination is exact instead of
	// inferred from whether the last page came back full.
	//
	// The two tabs are genuinely different tables, not one filtered two ways. A domain
	// event is about an entity that changed and carries before/after; the auth rows that
	// matter most changed nothing at all — a failed sign-in has no entity and no diff.
	import {
		listAudit,
		getAuditEvent,
		verifyAudit,
		restoreFromAudit,
		listAuthEvents,
		AUTH_EVENT_KINDS,
		type AuditEvent,
		type AuditEventDetail,
		type AuthEvent,
		type AuditVerify
	} from '$lib/api/audit.api';
	import {
		PageHeader,
		Button,
		Card,
		Input,
		Select,
		Checkbox,
		Tabs,
		Badge,
		Alert,
		Spinner,
		EmptyState,
		Pagination,
		ErrorState,
		toast,
		confirm,
		type Tone
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { untrack } from 'svelte';
	import { page } from '$app/state';
	import {
		ShieldCheck,
		RefreshCw,
		Download,
		ChevronDown,
		ChevronRight,
		Filter,
		Undo2
	} from 'lucide-svelte';

	type TabKey = 'domain' | 'auth';

	let tab = $state<TabKey>('domain');
	let offset = $state(0);
	const limit = 50;

	// ── Filters, ANDed. Dates are <input type="date"> values (local, YYYY-MM-DD)
	//    widened to UTC day boundaries on the way out.
	// Seeded from the query string like the two below, so a screen can link here
	// already narrowed to what it is about. /admin/policies links with `policy`.
	let fEntityType = $state(page.url.searchParams.get('entityType') ?? '');
	let fEntityId = $state('');
	let fAction = $state('');
	// Seeded from the query string, so a link into this screen arrives filtered. Read
	// once at init rather than reactively: a later navigation must not overwrite what
	// the operator has typed since. /admin/slo links here with slo.breach.
	let fActionPrefix = $state(page.url.searchParams.get('actionPrefix') ?? '');
	// Set from the URL by /admin/traces, which links here to answer "and what did that
	// request actually change" — the same id lives in both tables.
	let fRequestId = $state(page.url.searchParams.get('requestId') ?? '');
	let fActor = $state('');
	let fUserId = $state('');
	let fAuthEvent = $state('');
	let fIncludeUnattributed = $state(false);
	let fFrom = $state('');
	let fTo = $state('');

	// Quick filters over what is actually audited. Each is a set of entity types
	// rather than one, because the questions people ask span several: "security" is
	// credentials and secrets and users and permissions, and four separate chips would
	// mean four separate answers to one question.
	type Category = { key: string; label: string; entityTypes: string[]; description: string };

	const CATEGORIES: Category[] = [
		{
			key: 'security',
			label: 'Security & identity',
			entityTypes: ['credential', 'secret', 'user', 'permission', 'policy'],
			description: 'Who may do what, and with which material. The first trail an auditor opens.'
		},
		{
			key: 'external',
			label: 'External systems',
			entityTypes: ['integration', 'integration_action', 'mcp_server', 'ai_api_spec'],
			description: 'What Nashira connects to, and which operations it is allowed to call.'
		},
		{
			key: 'agent',
			label: 'Agent behaviour',
			entityTypes: ['agent.tool', 'ai_prompt_skill', 'ai_provider', 'agent_learning'],
			description: 'What the agent did, and what it was told to be. Includes every tool call.'
		},
		{
			key: 'execution',
			label: 'Execution & inventory',
			entityTypes: [
				'workflow',
				'workflow.node',
				'workflow_trigger',
				'workflow_test',
				'snippet',
				'device',
				'device_pool',
				'inventory_source'
			],
			description: 'What ran, and against what.'
		}
	];

	let activeCategory = $state<string | null>(null);

	const categoryTypes = $derived(
		activeCategory ? (CATEGORIES.find((c) => c.key === activeCategory)?.entityTypes ?? []) : []
	);

	let domainRows = $state<AuditEvent[]>([]);
	let authRows = $state<AuthEvent[]>([]);
	let total = $state(0);
	let loading = $state(true);
	let error = $state<unknown>(null);

	// Expansion is lazy: the list endpoint deliberately omits before/after, because a
	// page of fifty rows carrying two payloads each is a large response for a table
	// showing six columns. The detail is fetched when a row is actually opened.
	let expanded = $state<Set<string>>(new Set());
	let details = $state<Record<string, AuditEventDetail>>({});
	let detailLoading = $state<Set<string>>(new Set());

	let verify = $state<AuditVerify | null>(null);
	let verifying = $state(false);
	let exporting = $state(false);

	function dayStart(v: string): string | undefined {
		return v ? new Date(`${v}T00:00:00Z`).toISOString() : undefined;
	}

	function dayEnd(v: string): string | undefined {
		return v ? new Date(`${v}T23:59:59Z`).toISOString() : undefined;
	}

	function domainFilter(pageOffset: number, pageLimit: number) {
		return {
			entityType: fEntityType.trim() || undefined,
			entityTypes: categoryTypes.length ? categoryTypes : undefined,
			entityId: fEntityId.trim() || undefined,
			action: fAction.trim() || undefined,
			actionPrefix: fActionPrefix.trim() || undefined,
			requestId: fRequestId.trim() || undefined,
			actor: fActor.trim() || undefined,
			userId: fUserId.trim() || undefined,
			from: dayStart(fFrom),
			to: dayEnd(fTo),
			limit: pageLimit,
			offset: pageOffset
		};
	}

	function authFilter(pageOffset: number, pageLimit: number) {
		return {
			userId: fUserId.trim() || undefined,
			event: fAuthEvent || undefined,
			includeUnattributed: fIncludeUnattributed,
			from: dayStart(fFrom),
			to: dayEnd(fTo),
			limit: pageLimit,
			offset: pageOffset
		};
	}

	async function load() {
		loading = true;
		error = null;
		try {
			if (tab === 'domain') {
				const res = await listAudit(domainFilter(offset, limit));
				domainRows = res.items;
				total = res.total;
			} else {
				const res = await listAuthEvents(authFilter(offset, limit));
				authRows = res.items;
				total = res.total;
			}
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	// Re-reads on paging and on a tab switch, and on nothing else.
	//
	// untrack is load-bearing: load() reads every filter field synchronously, before its
	// first await, so a plain call would register all of them as dependencies of this
	// effect — and since Input writes on `oninput`, typing "credential" would fire ten
	// unsequenced requests whose responses can land out of order.
	$effect(() => {
		offset;
		tab;
		untrack(() => load());
	});

	function switchTab(next: string) {
		// Assigning `tab` re-runs the effect, which loads. Not calling load() here as
		// well: two triggers for one click is two concurrent requests racing to fill the
		// same table.
		tab = next as TabKey;
		offset = 0;
		expanded = new Set();
	}

	// No explicit load() here or below. Setting offset re-runs the effect, which loads
	// — and calling both fires the identical query twice whenever the user was not
	// already on page one. Same reasoning as switchTab.
	function applyFilters() {
		if (offset === 0) load();
		else offset = 0;
	}

	function applyCategory(key: string | null) {
		// A category replaces the manual entity filters rather than narrowing on top of
		// them: the chip is meant to show exactly the rows behind it, and an entity_type
		// left over from before would silently shrink that to almost nothing.
		activeCategory = key;
		fEntityType = '';
		fActionPrefix = '';
		if (offset === 0) load();
		else offset = 0;
	}

	function resetFilters() {
		fEntityType = '';
		fEntityId = '';
		fAction = '';
		fActionPrefix = '';
		fRequestId = '';
		fActor = '';
		fUserId = '';
		fAuthEvent = '';
		fIncludeUnattributed = false;
		fFrom = '';
		fTo = '';
		activeCategory = null;
		if (offset === 0) load();
		else offset = 0;
	}

	async function toggleRow(id: string) {
		const next = new Set(expanded);
		if (next.has(id)) {
			next.delete(id);
			expanded = next;
			return;
		}
		next.add(id);
		expanded = next;

		if (details[id]) return;
		detailLoading = new Set([...detailLoading, id]);
		try {
			// Awaited FIRST, then merged. Spreading `details` inside the assignment
			// captures a pre-fetch snapshot, so opening a second row before the first
			// resolves drops the first — its panel then stays blank until collapsed and
			// reopened.
			const detail = await getAuditEvent(id);
			details = { ...details, [id]: detail };
		} catch (e) {
			toast.fromError(e, "Couldn't load the event detail");
			const back = new Set(expanded);
			back.delete(id);
			expanded = back;
		} finally {
			const done = new Set(detailLoading);
			done.delete(id);
			detailLoading = done;
		}
	}

	// ── Restore ───────────────────────────────────────────────────────────
	//
	// A delete event whose record the server can bring back offers a Restore button
	// in its detail panel. The restore appends its own `restore` event to the trail
	// (attributed to the signed-in admin), so the list is reloaded to show it.
	let restoringId = $state<string | null>(null);

	async function restore(e: AuditEvent) {
		const d = details[e.id];
		if (!d?.restorable || restoringId) return;
		const ok = await confirm({
			title: `Restore this ${d.entityType.replace(/_/g, ' ')}?`,
			message:
				'The deleted record becomes active again, and the restore is recorded in the trail under your name.',
			confirmLabel: 'Restore'
		});
		if (!ok) return;
		restoringId = e.id;
		try {
			const r = await restoreFromAudit(e.id);
			toast.success(`Restored ${r.entityType.replace(/_/g, ' ')}${r.name ? ` "${r.name}"` : ''}`);
			details = { ...details, [e.id]: { ...d, restorable: false } };
			await load();
		} catch (err) {
			toast.fromError(err, "Couldn't restore the record");
		} finally {
			restoringId = null;
		}
	}

	async function runVerify() {
		verifying = true;
		try {
			verify = await verifyAudit();
		} catch (e) {
			toast.fromError(e, "Couldn't verify the chain");
		} finally {
			verifying = false;
		}
	}

	// ── CSV of the current view ───────────────────────────────────────────
	//
	// Re-queries with the active filters at the API's ceiling rather than exporting the
	// page on screen: someone asking for the file wants the result set, not the fifty
	// rows they happen to be looking at. Anything past the cap is reported instead of
	// silently trimmed — a truncated export that looks complete is worse than none.
	const EXPORT_LIMIT = 200;

	async function exportCsv() {
		if (exporting) return;
		exporting = true;
		try {
			const rows = tab === 'domain' ? await listAudit(domainFilter(0, EXPORT_LIMIT)) : null;
			const auth = tab === 'auth' ? await listAuthEvents(authFilter(0, EXPORT_LIMIT)) : null;

			const count = rows?.items.length ?? auth?.items.length ?? 0;
			if (count === 0) {
				toast.info('Nothing to export for the current filters');
				return;
			}

			const csv = rows ? domainCsv(rows.items) : authCsv(auth!.items);
			download(`${tab}-audit-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.csv`, csv);

			const grandTotal = rows?.total ?? auth?.total ?? 0;
			if (grandTotal > count) {
				toast.warning(`Exported the first ${count} of ${grandTotal}`, {
					description: 'Narrow the filters or the date range to export the rest.'
				});
			}
		} catch (e) {
			toast.fromError(e, 'Export failed');
		} finally {
			exporting = false;
		}
	}

	function domainCsv(rows: AuditEvent[]): string {
		const header = [
			'sequence',
			'at',
			'entity_type',
			'entity_id',
			'action',
			'username',
			'actor',
			'ip',
			'request_id',
			'hash'
		];
		return [
			header.join(','),
			...rows.map((r) =>
				[
					r.sequence,
					r.at,
					r.entityType,
					r.entityId ?? '',
					r.action,
					r.username ?? '',
					r.actor ?? '',
					r.ip ?? '',
					r.requestId ?? '',
					r.hash
				]
					.map(cell)
					.join(',')
			)
		].join('\n');
	}

	function authCsv(rows: AuthEvent[]): string {
		const header = ['at', 'event', 'username', 'user_id', 'ip', 'user_agent', 'metadata'];
		return [
			header.join(','),
			...rows.map((r) =>
				[
					r.at,
					r.event,
					r.username ?? '',
					r.userId ?? '',
					r.ip,
					r.userAgent,
					JSON.stringify(r.metadata ?? {})
				]
					.map(cell)
					.join(',')
			)
		].join('\n');
	}

	// A value that starts with =, +, - or @ is executed as a formula by spreadsheet
	// software. This table holds attacker-influenced text (a username someone tried,
	// a User-Agent), so the prefix is neutralised before it reaches a file an admin
	// will open.
	function cell(v: unknown): string {
		const s = String(v ?? '');
		const safe = /^[=+\-@]/.test(s) ? `'${s}` : s;
		// \r as well as \n: a User-Agent carrying a bare carriage return would otherwise
		// split the row when the file is opened.
		return /[",\r\n]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe;
	}

	function download(filename: string, csv: string) {
		const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
		const a = document.createElement('a');
		a.href = url;
		a.download = filename;
		a.click();
		URL.revokeObjectURL(url);
	}

	function actionTone(action: string): Tone {
		if (action === 'delete' || action === 'clear') return 'error';
		if (action === 'create' || action === 'set' || action === 'restore') return 'success';
		if (action === 'update' || action === 'promote') return 'warning';
		return 'neutral';
	}

	function authTone(event: string): Tone {
		if (event === 'login_success' || event === 'refresh') return 'success';
		if (event === 'lockout' || event === 'token_revoked') return 'error';
		if (event === 'login_failure') return 'warning';
		return 'neutral';
	}

	// A failed sign-in for a name that does not exist records the attempt redacted:
	// prefix, length and a keyed digest. Rendering that as raw JSON buries the one thing
	// an operator reads it for — whether somebody is working through admin, root, oracle.
	function attempted(
		metadata: unknown
	): { prefix: string; length: number; hash: string } | null {
		const m = metadata as Record<string, unknown> | null | undefined;
		if (!m || typeof m.attempted_hash !== 'string') return null;
		return {
			prefix: typeof m.attempted_prefix === 'string' ? m.attempted_prefix : '',
			length: typeof m.attempted_length === 'number' ? m.attempted_length : 0,
			hash: m.attempted_hash
		};
	}

	function when(iso: string): string {
		return new Date(iso).toLocaleString();
	}

	function short(v: string | null, n = 8): string {
		return v ? v.slice(0, n) : '—';
	}
</script>

<svelte:head><title>Audit · Nashira</title></svelte:head>

<PageHeader
	title="Audit log"
	description="Sign-ins, sensitive writes and entity mutations — what changed, who changed it, and when."
>
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="secondary" loading={exporting} onclick={exportCsv}>
				<Download size={15} />Download CSV
			</Button>
			<Button variant="ghost" onclick={load}><RefreshCw size={15} />Refresh</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

<RoleGate require="admin">
	{#snippet fallback()}
		<div class="ui-surface px-4 py-16 text-center text-sm text-surface-600-400">
			Administrator access is required for this area.
		</div>
	{/snippet}

	<div class="space-y-4">
		<div class="flex flex-wrap items-center justify-between gap-3">
			<Tabs
				bind:value={tab}
				tabs={[
					{ value: 'domain', label: 'Domain events' },
					{ value: 'auth', label: 'Auth events' }
				]}
				onchange={switchTab}
			/>
			<div class="inline-flex items-center gap-1.5 text-xs text-surface-600-400">
				<Filter size={12} />Filters below are ANDed together.
			</div>
		</div>

		{#if tab === 'domain'}
			<!-- The chain verifier is Nashira's own: the trail is hash-linked, so it can
			     prove it has not been rewritten rather than merely asserting it. -->
			<div class="flex flex-wrap items-center gap-2">
				<Button variant="secondary" loading={verifying} onclick={runVerify}>
					<ShieldCheck size={15} />Verify chain
				</Button>
				{#if verify}
					{#if verify.valid}
						<span class="text-xs text-success-600-400">
							{verify.count} event(s) verified — the chain is unbroken.
						</span>
					{:else}
						<span class="text-xs text-error-600-400">
							Tampering detected{verify.brokenAtSequence != null
								? ` at sequence ${verify.brokenAtSequence}`
								: ''}{verify.reason ? `: ${verify.reason}` : ''}
						</span>
					{/if}
				{/if}
			</div>

			<div class="flex flex-wrap items-center gap-2 text-xs">
				<span class="text-surface-600-400">Categories:</span>
				<button
					type="button"
					class={`rounded border px-2 py-1 transition ${
						activeCategory === null
							? 'border-primary-500 bg-primary-500 text-white dark:border-primary-400 dark:bg-primary-400 dark:text-primary-950'
							: 'border-surface-200-800 text-surface-700-300 hover:bg-surface-100-900'
					}`}
					onclick={() => applyCategory(null)}
				>
					All
				</button>
				{#each CATEGORIES as cat (cat.key)}
					<button
						type="button"
						title={cat.description}
						class={`rounded border px-2 py-1 transition ${
							activeCategory === cat.key
								? 'border-primary-500 bg-primary-500 text-white dark:border-primary-400 dark:bg-primary-400 dark:text-primary-950'
								: 'border-surface-200-800 text-surface-700-300 hover:bg-surface-100-900'
						}`}
						onclick={() => applyCategory(cat.key)}
					>
						{cat.label}
					</button>
				{/each}
			</div>
		{/if}

		<Card>
			<div class="grid grid-cols-1 gap-3 md:grid-cols-2 lg:grid-cols-4">
				{#if tab === 'domain'}
					<Input label="Entity type" bind:value={fEntityType} hint="credential, workflow, …" />
					<Input label="Entity id" bind:value={fEntityId} hint="uuid — the history of one record" />
					<Input label="Action" bind:value={fAction} hint="create / update / delete" />
					<Input
						label="Actor"
						bind:value={fActor}
						hint="username, or workflow-runner / scheduler"
					/>
					<Input
						label="Action prefix"
						bind:value={fActionPrefix}
						hint="A family of verbs at once"
					/>
					<Input
						label="Request id"
						bind:value={fRequestId}
						hint="Everything one request changed — /admin/traces links here"
					/>
				{:else}
					<Select
						label="Event"
						bind:value={fAuthEvent}
						placeholder="All events"
						options={AUTH_EVENT_KINDS.map((k) => ({ value: k, label: k }))}
					/>
					<div class="flex items-end pb-2">
						<Checkbox
							bind:checked={fIncludeUnattributed}
							label="Include unattributed attempts"
						/>
					</div>
				{/if}
				<Input label="User id" bind:value={fUserId} hint="uuid" />
				<div class="grid grid-cols-2 gap-2">
					<Input label="From" type="date" bind:value={fFrom} />
					<Input label="To" type="date" bind:value={fTo} />
				</div>
				<div class="flex items-end gap-2 md:col-span-2 lg:col-span-4">
					<Button variant="primary" onclick={applyFilters}>Apply filters</Button>
					<Button variant="ghost" onclick={resetFilters}>Reset</Button>
				</div>
			</div>
		</Card>

		{#if tab === 'auth' && !fIncludeUnattributed}
			<Alert tone="neutral">
				Sign-in attempts for a username that does not exist are hidden: on a public
				endpoint they are the noisiest rows here and would bury failures on real accounts.
				Turn them on to look for account enumeration — a burst of them is exactly that.
			</Alert>
		{/if}

		{#if error}
			<ErrorState {error} onRetry={load} />
		{:else if loading}
			<div class="flex justify-center py-12"><Spinner size="lg" /></div>
		{:else if tab === 'domain'}
			{#if domainRows.length === 0}
				<Card>
					<EmptyState
						title="No domain events"
						description="Nothing matches the current filters. Try widening the date range."
					/>
				</Card>
			{:else}
				<Card>
					<table class="w-full text-sm">
						<caption class="sr-only">Domain audit events — entity mutations with before/after state.</caption>
						<thead class="border-b border-surface-200-800">
							<tr class="text-left text-[11px] uppercase tracking-wide text-surface-600-400">
								<th class="w-8 px-4 py-2"></th>
								<th class="px-4 py-2">#</th>
								<th class="px-4 py-2">When</th>
								<th class="px-4 py-2">Entity</th>
								<th class="px-4 py-2">Action</th>
								<th class="px-4 py-2">Actor</th>
								<th class="px-4 py-2">Hash</th>
							</tr>
						</thead>
						<tbody>
							{#each domainRows as e (e.id)}
								{@const open = expanded.has(e.id)}
								<tr
									class="cursor-pointer border-b border-surface-100-900 hover:bg-surface-100-900/50"
									onclick={() => toggleRow(e.id)}
								>
									<td class="px-4 py-2 align-top text-surface-600-400">
										{#if open}<ChevronDown size={12} />{:else}<ChevronRight size={12} />{/if}
									</td>
									<td class="px-4 py-2 font-mono tabular-nums text-surface-600-400">{e.sequence}</td>
									<td class="whitespace-nowrap px-4 py-2 tabular-nums text-surface-700-300">
										{when(e.at)}
									</td>
									<td class="px-4 py-2">
										<Badge>{e.entityType}</Badge>
										{#if e.entityId}
											<div class="font-mono text-[11px] text-surface-600-400">{short(e.entityId)}</div>
										{/if}
									</td>
									<td class="px-4 py-2"><Badge tone={actionTone(e.action)}>{e.action}</Badge></td>
									<td class="px-4 py-2 text-surface-700-300">
										{e.username ?? e.actor ?? '—'}
										{#if !e.userId && e.actor}
											<!-- The distinction the actor column exists for: this change had
											     no signed-in user behind it. -->
											<div class="text-[11px] text-surface-600-400">automation</div>
										{/if}
									</td>
									<td class="px-4 py-2 font-mono text-[11px] text-surface-600-400">
										{short(e.hash, 10)}
										{#if e.hashVersion === 1}
											<span title="Signed before the actor field existed">·v1</span>
										{/if}
									</td>
								</tr>
								{#if open}
									<tr class="border-b border-surface-100-900 bg-surface-50-950">
										<td colspan="7" class="space-y-3 px-4 py-3 text-xs">
											{#if detailLoading.has(e.id)}
												<div class="flex justify-center py-4"><Spinner size="sm" /></div>
											{:else if details[e.id]}
												{@const d = details[e.id]}
												<div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
													<div>
														<div class="mb-1 text-[11px] font-semibold uppercase tracking-wide text-surface-600-400">
															Before
														</div>
														<pre class="max-h-48 overflow-auto rounded bg-surface-100-900 p-2 text-[11px]">{JSON.stringify(
																d.before ?? {},
																null,
																2
															)}</pre>
													</div>
													<div>
														<div class="mb-1 text-[11px] font-semibold uppercase tracking-wide text-surface-600-400">
															After
														</div>
														<pre class="max-h-48 overflow-auto rounded bg-surface-100-900 p-2 text-[11px]">{JSON.stringify(
																d.after ?? {},
																null,
																2
															)}</pre>
													</div>
												</div>
												{#if d.restorable}
													<div class="flex items-center gap-3">
														<Button
															size="sm"
															variant="secondary"
															loading={restoringId === e.id}
															onclick={() => restore(e)}
														>
															<Undo2 size={13} />Restore this {d.entityType.replace(/_/g, ' ')}
														</Button>
														<span class="text-[11px] text-surface-600-400">
															Reactivates the deleted record and appends a restore event under your name.
														</span>
													</div>
												{/if}
												<div class="flex flex-wrap gap-4 text-[11px] text-surface-600-400">
													{#if d.ip}<div>IP: <span class="font-mono">{d.ip}</span></div>{/if}
													{#if d.requestId}
														<div>Request: <span class="font-mono">{d.requestId}</span></div>
													{/if}
													{#if d.userAgent}
														<div class="truncate">UA: <span class="font-mono">{d.userAgent}</span></div>
													{/if}
													<div>Prev hash: <span class="font-mono">{short(d.prevHash, 10)}</span></div>
												</div>
											{/if}
										</td>
									</tr>
								{/if}
							{/each}
						</tbody>
					</table>
				</Card>
				<Pagination {total} {limit} {offset} onchange={(o) => (offset = o)} />
			{/if}
		{:else if authRows.length === 0}
			<Card>
				<EmptyState
					title="No auth events"
					description="Sign-ins, lockouts and token changes appear here as they happen."
				/>
			</Card>
		{:else}
			<Card>
				<table class="w-full text-sm">
					<caption class="sr-only">Authentication events — sign-ins, lockouts and token changes.</caption>
					<thead class="border-b border-surface-200-800">
						<tr class="text-left text-[11px] uppercase tracking-wide text-surface-600-400">
							<th class="px-4 py-2">When</th>
							<th class="px-4 py-2">Event</th>
							<th class="px-4 py-2">User</th>
							<th class="px-4 py-2">IP</th>
							<th class="px-4 py-2">Context</th>
						</tr>
					</thead>
					<tbody>
						{#each authRows as e (e.id)}
							{@const attempt = attempted(e.metadata)}
							<tr class="border-b border-surface-100-900">
								<td class="whitespace-nowrap px-4 py-2 tabular-nums text-surface-700-300">
									{when(e.at)}
								</td>
								<td class="px-4 py-2"><Badge tone={authTone(e.event)}>{e.event}</Badge></td>
								<td class="px-4 py-2 text-surface-700-300">
									{e.username ?? '—'}
									{#if !e.userId}
										<!-- No account to point at: the attempt named a username that does
										     not exist. Kept because a run of them is enumeration. -->
										<div class="text-[11px] text-surface-600-400">unattributed</div>
									{/if}
								</td>
								<td class="px-4 py-2 font-mono text-[11px] text-surface-600-400">{e.ip || '—'}</td>
								<td class="px-4 py-2">
									{#if attempt}
										<!-- The attempted name is stored redacted, so it is rendered as what
										     it is rather than as raw JSON: a recognisable prefix, the length,
										     and the digest that makes repeats correlate. -->
										<code class="text-[11px] text-surface-700-300" title={`digest ${attempt.hash}`}>
											{attempt.prefix}…({attempt.length})
										</code>
									{:else if e.metadata && Object.keys(e.metadata as object).length > 0}
										<code class="text-[11px] text-surface-600-400">
											{JSON.stringify(e.metadata)}
										</code>
									{:else}
										<span class="text-surface-600-400">—</span>
									{/if}
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</Card>
			<Pagination {total} {limit} {offset} onchange={(o) => (offset = o)} />
		{/if}
	</div>
</RoleGate>
