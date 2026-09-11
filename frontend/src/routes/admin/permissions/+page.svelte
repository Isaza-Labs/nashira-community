<script lang="ts">
	// Per-user restrictions on what the agent may reach on their behalf.
	//
	// The model is default-allow, opt-in-deny: a row is a RESTRICTION, so anything
	// left alone follows the user's role. That is the part people get wrong, and the
	// previous screen made it harder — four checkboxes per row, three of them disabled
	// most of the time, across every domain, integration, MCP server and API spec at
	// once. The handful of rows that actually said something were lost among a hundred
	// that said nothing.
	//
	// So the screen is organised around finding, and around what is actually set:
	//   - search across every group at once, because "can this user reach NetBox?" is
	//     the real question and it used to be answered by scrolling;
	//   - a Restricted-only filter, which turns the page into an audit of what is in
	//     force rather than a catalogue of everything that exists;
	//   - one control per row with two states instead of four checkboxes, so "the role
	//     decides" and "I have decided" stop looking the same;
	//   - an explicit Denied badge, because a restriction that grants nothing is a full
	//     denial and that is the outcome most likely to surprise someone;
	//   - dirty tracking and a sticky action bar, so Save is reachable without
	//     scrolling past everything and cannot be pressed when there is nothing to do.
	import { listUsers, type User } from '$lib/api/users.api';
	import {
		getPermissionResources,
		getUserPermissions,
		setUserPermissions,
		type PermissionResource
	} from '$lib/api/permissions.api';
	import {
		PageHeader,
		Select,
		SearchInput,
		Checkbox,
		Button,
		Badge,
		Alert,
		Spinner,
		EmptyState,
		ErrorState,
		confirm,
		toast
	} from '$lib/components/ui';
	import { untrack } from 'svelte';
	import { ShieldCheck, RotateCcw } from 'lucide-svelte';

	// $state objects are Proxies, and structuredClone refuses to clone a Proxy —
	// it throws DataCloneError. Using it here was worse than a crash: the call sat
	// inside save()'s try block, so a PUT that succeeded jumped straight to the catch
	// and told the admin the save had failed, while the dirty count stayed up and
	// invited them to press Save again. $state.snapshot is the supported way to take
	// a plain-object copy of reactive state.
	function snapshot<T>(v: T): T {
		return $state.snapshot(v) as T;
	}

	type Row = { restricted: boolean; read: boolean; write: boolean; execute: boolean };

	let users = $state<User[]>([]);
	let resources = $state<PermissionResource[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let selectedUserId = $state('');
	let matrix = $state<Record<string, Row>>({});
	// What the server returned, so "has anything changed?" is a comparison rather than
	// a flag every handler has to remember to set.
	let baseline = $state<Record<string, Row>>({});
	// Which user `matrix` was built for. The table renders only once this matches
	// appliedUserId — NOT selectedUserId, which is just where the dropdown is pointing
	// — so it never reads matrix[key] before it is populated.
	let loadedUserId = $state('');
	// The selection the panel has actually accepted. Distinct from selectedUserId,
	// which the dropdown moves before we have decided whether to honour it.
	let appliedUserId = $state('');
	let permsLoading = $state(false);
	let permsError = $state<unknown>(null);
	let saving = $state(false);
	// Switching user twice in quick succession can land the responses out of order.
	// Without a token the slower first response wins, leaving matrix holding one
	// user's rows while the header names another — and the panel stuck on a spinner
	// with no error and no retry, because loadedUserId never catches up.
	let permsRequest = 0;

	let search = $state('');
	let restrictedOnly = $state(false);

	const userOptions = $derived(users.map((u) => ({ value: u.id, label: `${u.username} (${u.role})` })));
	// Deliberately appliedUserId, not selectedUserId: this names the user whose rows
	// are on screen. The two diverge while the discard confirm is open, and every
	// label, retry and save below has to follow the data rather than the dropdown.
	const selectedUser = $derived(users.find((u) => u.id === appliedUserId) ?? null);

	const GROUPS: { kind: PermissionResource['kind']; title: string; hint: string }[] = [
		{
			kind: 'domain',
			title: 'Capability domains',
			hint: 'Every tool of that kind, whichever system it targets.'
		},
		{
			kind: 'integration',
			title: 'Integrations',
			hint: 'Registered external systems, including through the API specs bound to them.'
		},
		{ kind: 'mcp', title: 'MCP servers', hint: 'Restricting one leaves the others reachable.' },
		{ kind: 'api', title: 'API specs', hint: 'The operations discover/execute can reach.' }
	];

	function matches(r: PermissionResource, q: string): boolean {
		if (!q) return true;
		const needle = q.toLowerCase();
		// The key is searched too: an admin auditing a change reads `integration:netbox`
		// in an audit event and pastes it straight in here.
		return (
			r.label.toLowerCase().includes(needle) ||
			r.key.toLowerCase().includes(needle) ||
			(r.description ?? '').toLowerCase().includes(needle)
		);
	}

	const dirtyKeys = $derived(
		Object.keys(matrix).filter((k) => {
			const a = matrix[k];
			const b = baseline[k];
			if (!a || !b) return false;
			// Read/write/execute only count while restricted: they are ignored by the
			// server otherwise, so a stale tick underneath "role default" is not a change.
			return (
				a.restricted !== b.restricted ||
				(a.restricted && (a.read !== b.read || a.write !== b.write || a.execute !== b.execute))
			);
		})
	);
	const dirty = $derived(dirtyKeys.length > 0);

	const grouped = $derived(
		GROUPS.map((g) => ({
			...g,
			items: resources.filter(
				(r) =>
					r.kind === g.kind &&
					matches(r, search) &&
					// Dirty rows stay visible even when they no longer satisfy the
					// filter. Otherwise clicking "Role default" in Restricted-only mode
					// removes the row mid-edit — the change is still pending and will be
					// saved, but it is now invisible and cannot be reverted per row.
					(!restrictedOnly || matrix[r.key]?.restricted || dirtyKeys.includes(r.key))
			),
			// Counted over the whole group, not the filtered slice: it says what the
			// group contains, not what survived the search.
			restricted: resources.filter((r) => r.kind === g.kind && matrix[r.key]?.restricted).length,
			total: resources.filter((r) => r.kind === g.kind).length
		})).filter((g) => g.items.length > 0)
	);

	const visibleCount = $derived(grouped.reduce((n, g) => n + g.items.length, 0));
	const restrictedCount = $derived(Object.values(matrix).filter((r) => r.restricted).length);
	// A restriction that allows nothing denies the target outright. It is the most
	// consequential state on the page and the easiest to reach by accident.
	const deniedCount = $derived(
		Object.values(matrix).filter((r) => r.restricted && !r.read && !r.write && !r.execute).length
	);

	async function loadBase() {
		loading = true;
		error = null;
		try {
			const [u, r] = await Promise.all([listUsers(200, 0), getPermissionResources()]);
			users = u.items;
			resources = r;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		loadBase();
	});

	async function loadPerms(userId: string) {
		const token = ++permsRequest;
		permsLoading = true;
		permsError = null;
		try {
			const perms = await getUserPermissions(userId);
			if (token !== permsRequest) return;
			const stored = new Map(perms.map((p) => [p.toolDomain.toLowerCase(), p]));
			const next: Record<string, Row> = {};
			for (const r of resources) {
				const p = stored.get(r.key.toLowerCase());
				next[r.key] = {
					restricted: !!p,
					read: p?.canRead ?? false,
					write: p?.canWrite ?? false,
					execute: p?.canExecute ?? false
				};
			}
			matrix = next;
			// `next` is a plain object here, but going through the same helper keeps
			// the three copies from drifting if this is ever reordered.
			baseline = snapshot(next);
			loadedUserId = userId;
		} catch (e) {
			if (token !== permsRequest) return;
			permsError = e;
			// matrix/baseline still hold the PREVIOUS user's rows, so leaving
			// appliedUserId on the one that failed would attribute their unsaved edits
			// to the wrong person on the next switch. Clearing both makes the panel
			// show the error and nothing else.
			matrix = {};
			baseline = {};
			loadedUserId = '';
			toast.fromError(e, "Couldn't load permissions");
		} finally {
			// Only the newest request owns the spinner; an overtaken one clearing it
			// would uncover a panel that is still loading.
			if (token === permsRequest) permsLoading = false;
		}
	}

	// The page goes to some trouble to say there are N unsaved changes and to offer a
	// Discard button. Throwing them away silently because the dropdown moved would
	// undo exactly that.
	$effect(() => {
		const next = selectedUserId;
		if (!next || next === appliedUserId) return;
		// untrack: reading `dirty` reactively would re-run this on every checkbox.
		if (untrack(() => dirty)) {
			void confirmSwitch(next);
			return;
		}
		applyUser(next);
	});

	// The one place appliedUserId changes. Clearing the rows in the same step is what
	// keeps `dirty` from being attributed to whoever was just selected: between the
	// switch and the response, matrix still holds the PREVIOUS user's edits.
	function applyUser(next: string) {
		appliedUserId = next;
		matrix = {};
		baseline = {};
		loadedUserId = '';
		loadPerms(next);
	}

	async function confirmSwitch(next: string) {
		const ok = await confirm({
			title: 'Discard unsaved changes?',
			message: `${dirtyKeys.length} change(s) to ${
				users.find((u) => u.id === appliedUserId)?.username ?? 'this user'
			} have not been saved. Switching user discards them.`,
			tone: 'danger',
			confirmLabel: 'Discard and switch'
		});
		if (ok) {
			applyUser(next);
		} else {
			// Put the dropdown back where it was, rather than leaving it naming a user
			// whose permissions are not the ones on screen.
			selectedUserId = appliedUserId;
		}
	}

	// Switching a target to restricted starts at "reads only". Starting from nothing
	// would make the first click a silent full denial, which is a lot to do by
	// pressing one button.
	function restrict(key: string) {
		matrix[key] = { restricted: true, read: true, write: false, execute: false };
	}

	function unrestrict(key: string) {
		matrix[key] = { restricted: false, read: false, write: false, execute: false };
	}

	function reset() {
		matrix = snapshot(baseline);
	}

	async function save() {
		saving = true;
		try {
			// Unrestricted targets are sent as `inherit` rather than omitted, so lifting a
			// restriction actually deletes the row instead of leaving it behind.
			await setUserPermissions(
				appliedUserId,
				resources.map((r) => {
					const row = matrix[r.key];
					return {
						toolDomain: r.key,
						canRead: row?.read ?? false,
						canWrite: row?.write ?? false,
						canExecute: row?.execute ?? false,
						inherit: !row?.restricted
					};
				})
			);
			baseline = snapshot(matrix);
			toast.success('Permissions saved');
		} catch (e) {
			toast.fromError(e, 'Failed to save permissions');
		} finally {
			saving = false;
		}
	}
</script>

<svelte:head><title>Permissions · Nashira</title></svelte:head>

<PageHeader
	title="Permissions"
	description="Restrict what the agent may reach on a user's behalf, per capability and per system."
/>

{#if loading}
	<div class="flex justify-center py-16"><Spinner size="lg" /></div>
{:else if error}
	<ErrorState {error} onRetry={loadBase} />
{:else}
	<div class="space-y-4 pb-6">
		<div class="max-w-sm">
			<Select
				label="User"
				bind:value={selectedUserId}
				options={userOptions}
				placeholder="Select a user…"
			/>
		</div>

		{#if !selectedUserId && !appliedUserId}
			<EmptyState
				title="Pick a user"
				description="Permissions are per user. Anyone without a restriction here is governed by their role alone."
			/>
		{:else if permsError}
			<ErrorState error={permsError} onRetry={() => loadPerms(appliedUserId)} compact />
		{:else if permsLoading || loadedUserId !== appliedUserId}
			<div class="flex justify-center py-10"><Spinner /></div>
		{:else}
			<Alert tone="primary" title="These are restrictions, not grants">
				Anything left on <strong>Role default</strong> follows
				{selectedUser ? `${selectedUser.username}'s role` : "the user's role"}
				{#if selectedUser}(<strong>{selectedUser.role}</strong>){/if}. A user with no restrictions
				at all is unrestricted — a row added here only ever takes access away.
			</Alert>

			{#if deniedCount > 0}
				<Alert tone="warning" title="{deniedCount} target(s) fully denied">
					A restriction that allows nothing blocks that target outright. Tick Read, Write or
					Execute to narrow it instead of closing it.
				</Alert>
			{/if}

			<!-- Find, then decide. "Can this user reach NetBox?" used to mean scrolling
			     four tables; now it is one query. -->
			<div class="flex flex-wrap items-center gap-3">
				<SearchInput
					bind:value={search}
					placeholder="Search domains, integrations, MCP, specs…"
					width="w-80"
				/>
				<Checkbox bind:checked={restrictedOnly} label="Restricted only" />
				<div class="ml-auto flex items-center gap-2 text-xs text-surface-600-400">
					<Badge tone={restrictedCount > 0 ? 'warning' : 'neutral'}>
						{restrictedCount} restricted
					</Badge>
					<span>of {resources.length} targets</span>
				</div>
			</div>

			{#if visibleCount === 0}
				<EmptyState
					title={restrictedOnly ? 'Nothing is restricted' : 'No target matches'}
					description={restrictedOnly
						? `${selectedUser?.username ?? 'This user'} is governed entirely by their role.`
						: `Nothing matches "${search}". Keys are searchable too — integration:netbox, mcp:<server>, api:<api>.`}
				/>
			{/if}

			{#each grouped as group (group.kind)}
				<div class="overflow-hidden rounded-xl border border-surface-200-800">
					<div
						class="flex items-baseline gap-3 border-b border-surface-200-800 bg-surface-100-900 px-4 py-2"
					>
						<div class="min-w-0">
							<div class="text-sm font-medium">{group.title}</div>
							<div class="text-xs text-surface-600-400">{group.hint}</div>
						</div>
						<div class="ml-auto shrink-0 text-xs text-surface-600-400">
							{#if group.restricted > 0}
								<Badge tone="warning">{group.restricted} restricted</Badge>
							{:else}
								<span>{group.total} on role default</span>
							{/if}
						</div>
					</div>

					<ul class="divide-y divide-surface-100-900">
						{#each group.items as r (r.key)}
							{@const row = matrix[r.key]}
							{@const denied = !!row?.restricted && !row.read && !row.write && !row.execute}
							<li class="px-4 py-3">
								<div class="flex flex-wrap items-start gap-x-4 gap-y-2">
									<div class="min-w-0 flex-1">
										<div class="flex flex-wrap items-center gap-2">
											<span class="font-medium">{r.label}</span>
											<code class="text-xs text-surface-600-400">{r.key}</code>
											{#if denied}<Badge tone="error">denied</Badge>{/if}
											{#if dirtyKeys.includes(r.key)}<Badge tone="primary">unsaved</Badge>{/if}
										</div>
										{#if r.description}
											<div class="mt-0.5 text-xs text-surface-600-400">{r.description}</div>
										{/if}
									</div>

									<!-- Two states, not four checkboxes. "The role decides" and "I have
									     decided" are different kinds of thing and used to look alike. -->
									<div class="flex shrink-0 items-center gap-1 rounded-lg bg-surface-100-900 p-1">
										<button
											type="button"
											class={`rounded-md px-2.5 py-1 text-xs transition ${
												!row?.restricted
													? 'bg-surface-50-950 font-medium text-surface-950-50 shadow-sm'
													: 'text-surface-600-400 hover:text-surface-950-50'
											}`}
											onclick={() => unrestrict(r.key)}
										>
											Role default
										</button>
										<button
											type="button"
											class={`rounded-md px-2.5 py-1 text-xs transition ${
												row?.restricted
													? 'bg-surface-50-950 font-medium text-surface-950-50 shadow-sm'
													: 'text-surface-600-400 hover:text-surface-950-50'
											}`}
											onclick={() => restrict(r.key)}
										>
											Restricted
										</button>
									</div>

									<!-- Rendered only when it means something, instead of three disabled
									     boxes whose greyed-out state has to be decoded. -->
									{#if row?.restricted}
										<div class="flex shrink-0 items-center gap-3">
											<Checkbox bind:checked={matrix[r.key].read} label="Read" />
											<Checkbox bind:checked={matrix[r.key].write} label="Write" />
											<Checkbox bind:checked={matrix[r.key].execute} label="Execute" />
										</div>
									{/if}
								</div>
							</li>
						{/each}
					</ul>
				</div>
			{/each}
		{/if}
	</div>

	<!-- Sticky: the target you just changed may be the last row of the fourth group,
	     and a Save you have to go looking for is a Save people forget to press. -->
	{#if appliedUserId && loadedUserId === appliedUserId && !permsLoading && !permsError}
		<div
			class="sticky bottom-0 z-10 flex items-center gap-3 border-t border-surface-200-800 bg-surface-50-950/95 px-1 py-3 backdrop-blur"
		>
			<div class="text-sm text-surface-600-400">
				{#if dirty}
					{dirtyKeys.length} unsaved change{dirtyKeys.length === 1 ? '' : 's'}
				{:else}
					<span class="inline-flex items-center gap-1.5">
						<ShieldCheck size={14} />No pending changes
					</span>
				{/if}
			</div>
			<div class="ml-auto flex items-center gap-2">
				<Button variant="ghost" disabled={!dirty || saving} onclick={reset}>
					<RotateCcw size={15} />Discard
				</Button>
				<Button variant="primary" loading={saving} disabled={!dirty} onclick={save}>
					Save permissions
				</Button>
			</div>
		</div>
	{/if}
{/if}
