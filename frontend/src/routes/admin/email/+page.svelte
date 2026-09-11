<script lang="ts">
	import { page } from '$app/state';
	import {
		listEmailChannels,
		createEmailChannel,
		updateEmailChannel,
		deleteEmailChannel,
		testEmailChannel,
		testEmailChannelImap,
		type EmailChannel,
		type EmailChannelPayload
	} from '$lib/api/email.api';
	import { errorMessage } from '$lib/api/client';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		IconButton,
		Input,
		Textarea,
		Select,
		Checkbox,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import RoleGate from '$lib/components/auth/RoleGate.svelte';
	import { Plus, Pencil, Trash2, Send, Inbox } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	let items = $state<EmailChannel[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let testOpen = $state(false);
	let testChannel = $state<EmailChannel | null>(null);
	let testTo = $state('');
	let testSending = $state(false);
	let testSentTo = $state<string[] | null>(null);
	let testError = $state('');

	let modalOpen = $state(false);
	let editing = $state<EmailChannel | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fDescription = $state('');
	let fHost = $state('');
	let fPort = $state('587');
	let fSecurity = $state('starttls');
	let fUsername = $state('');
	let fPassword = $state('');
	let fClearPassword = $state(false);
	let fFromAddress = $state('');
	let fFromName = $state('');
	let fRecipients = $state('');
	let fEnabled = $state(true);
	let fImapHost = $state('');
	let fImapPort = $state('993');
	let fImapSecurity = $state('ssl');
	let fImapUsername = $state('');
	let fImapPassword = $state('');
	let fClearImapPassword = $state(false);

	let imapTesting = $state<string | null>(null);

	const securityOptions = [
		{ value: 'starttls', label: 'STARTTLS (587)' },
		{ value: 'ssl', label: 'Implicit TLS (465)' },
		{ value: 'none', label: 'None — plaintext' }
	];

	const imapSecurityOptions = [
		{ value: 'ssl', label: 'Implicit TLS (993)' },
		{ value: 'starttls', label: 'STARTTLS (143)' },
		{ value: 'none', label: 'None — plaintext' }
	];

	const columns: Column[] = [
		{ key: 'name', header: 'Name' },
		{ key: 'relay', header: 'Relay' },
		{ key: 'from', header: 'From' },
		{ key: 'auth', header: 'Auth' },
		{ key: 'actions', header: '', align: 'right', sortable: false }
	];

	// Nudge the port when the security mode changes, but only off the well-known
	// values — a custom port the admin typed must not be overwritten.
	$effect(() => {
		if (fSecurity === 'ssl' && fPort === '587') fPort = '465';
		else if (fSecurity === 'starttls' && fPort === '465') fPort = '587';
	});

	$effect(() => {
		if (fImapSecurity === 'ssl' && fImapPort === '143') fImapPort = '993';
		else if (fImapSecurity === 'starttls' && fImapPort === '993') fImapPort = '143';
	});

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listEmailChannels()).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function reset() {
		formError = '';
		fName = '';
		fDescription = '';
		fHost = '';
		fPort = '587';
		fSecurity = 'starttls';
		fUsername = '';
		fPassword = '';
		fClearPassword = false;
		fFromAddress = '';
		fFromName = '';
		fRecipients = '';
		fEnabled = true;
		fImapHost = '';
		fImapPort = '993';
		fImapSecurity = 'ssl';
		fImapUsername = '';
		fImapPassword = '';
		fClearImapPassword = false;
	}

	function openCreate() {
		editing = null;
		reset();
		modalOpen = true;
	}

	function openEdit(c: EmailChannel) {
		editing = c;
		reset();
		fName = c.name;
		fDescription = c.description ?? '';
		fHost = c.host;
		fPort = String(c.port);
		fSecurity = c.security;
		fUsername = c.username ?? '';
		// Deliberately blank: the API never returns it; a placeholder here would
		// let an unrelated edit overwrite the real password with the placeholder.
		fPassword = '';
		fFromAddress = c.fromAddress;
		fFromName = c.fromName ?? '';
		fRecipients = c.defaultRecipients ?? '';
		fEnabled = c.enabled;
		fImapHost = c.imapHost ?? '';
		fImapPort = String(c.imapPort);
		fImapSecurity = c.imapSecurity;
		fImapUsername = c.imapUsername ?? '';
		fImapPassword = '';
		modalOpen = true;
	}

	function payload(): EmailChannelPayload {
		return {
			name: fName.trim(),
			description: fDescription,
			host: fHost.trim(),
			port: Number.parseInt(fPort, 10) || 587,
			security: fSecurity,
			username: fUsername.trim(),
			password: fPassword,
			clearPassword: fClearPassword,
			fromAddress: fFromAddress.trim(),
			fromName: fFromName,
			defaultRecipients: fRecipients,
			enabled: fEnabled,
			imapHost: fImapHost.trim(),
			imapPort: Number.parseInt(fImapPort, 10) || 993,
			imapSecurity: fImapSecurity,
			imapUsername: fImapUsername.trim(),
			imapPassword: fImapPassword,
			clearImapPassword: fClearImapPassword
		};
	}

	async function save() {
		if (!fName.trim() || !fHost.trim() || !fFromAddress.trim()) {
			formError = 'Name, host and from address are required.';
			return;
		}
		formError = '';
		saving = true;
		try {
			if (editing) await updateEmailChannel(editing.id, payload());
			else await createEmailChannel(payload());
			modalOpen = false;
			toast.success(editing ? 'Channel updated' : 'Channel created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the channel');
		} finally {
			saving = false;
		}
	}

	async function remove(c: EmailChannel) {
		const ok = await confirm({
			title: 'Delete email channel?',
			message: `Workflows sending through "${c.name}" will start failing.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteEmailChannel(c.id);
			items = items.filter((x) => x.id !== c.id);
			toast.success('Channel deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the channel");
		}
	}

	function openTest(c: EmailChannel) {
		testChannel = c;
		testTo = c.defaultRecipients ?? '';
		testSentTo = null;
		testError = '';
		testOpen = true;
	}

	async function sendTest() {
		if (!testChannel) return;
		testSending = true;
		testSentTo = null;
		testError = '';
		try {
			testSentTo = await testEmailChannel(testChannel.id, testTo.trim() || undefined);
		} catch (e) {
			testError = errorMessage(e);
		} finally {
			testSending = false;
		}
	}

	async function testImap(c: EmailChannel) {
		imapTesting = c.id;
		try {
			const r = await testEmailChannelImap(c.id);
			toast.success(
				`IMAP works — ${r.folders} folder(s), ${r.inboxMessages} in INBOX (${r.inboxUnread} unread)`
			);
		} catch (e) {
			toast.fromError(e, 'IMAP test failed');
		} finally {
			imapTesting = null;
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Email channels · Nashira</title></svelte:head>

<SectionNav id="integrations" />

<PageHeader
	title="Email channels"
	description="Named SMTP relays, so different workflows can mail through different servers. Add an IMAP host to let the assistant and workflows manage the mailbox too."
>
	{#snippet actions()}
		<RoleGate require="admin">
			<Button variant="primary" onclick={openCreate}><Plus size={15} />New channel</Button>
		</RoleGate>
	{/snippet}
</PageHeader>

<RoleGate require="admin">
	{#snippet fallback()}
		<div class="ui-surface px-4 py-16 text-center text-sm text-surface-600-400">
			Administrator access is required for this area.
		</div>
	{/snippet}

	{#if error}
		<ErrorState {error} onRetry={load} />
	{:else}
		<div class="space-y-3">
			<Alert tone="neutral">
				The relay configured in <code>appsettings</code> stays the deployment default; channels are
				per-workflow overrides.
			</Alert>

			<DataTable {loading} {columns} rows={items} rowKey={(c) => c.id} empty="No email channels — the appsettings relay still works; add one only to send through a different server.">
				{#snippet cell(row, col)}
					{#if col.key === 'name'}
						<div class="font-medium">{row.name}</div>
						{#if row.description}
							<div class="max-w-[20rem] truncate text-xs text-surface-600-400">{row.description}</div>
						{/if}
					{:else if col.key === 'relay'}
						<code class="text-xs text-surface-600-400">{row.host}:{row.port}</code>
						<Badge tone={row.security === 'none' ? 'warning' : 'neutral'}>{row.security}</Badge>
						{#if row.imapConfigured}<Badge tone="neutral">mailbox</Badge>{/if}
					{:else if col.key === 'from'}
						<span class="text-xs text-surface-600-400">{row.fromAddress}</span>
					{:else if col.key === 'auth'}
						{#if row.username}
							<div class="flex items-center gap-1.5">
								<span class="text-xs text-surface-600-400">{row.username}</span>
								{#if !row.hasPassword}<span class="text-xs text-warning-600-400">no password</span>{/if}
							</div>
						{:else}
							<span class="text-xs text-surface-600-400">anonymous</span>
						{/if}
						{#if !row.enabled}<Badge tone="warning">disabled</Badge>{/if}
					{:else if col.key === 'actions'}
						<div class="flex justify-end gap-1">
							<IconButton label={`Send a test through ${row.name}`} onclick={() => openTest(row)}>
								<Send size={14} />
							</IconButton>
							{#if row.imapConfigured}
								<IconButton
									label={`Test the ${row.name} mailbox (IMAP)`}
									disabled={imapTesting === row.id}
									onclick={() => testImap(row)}
								>
									<Inbox size={14} />
								</IconButton>
							{/if}
							<IconButton label="Edit channel" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
							<IconButton label="Delete channel" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
						</div>
					{/if}
				{/snippet}
			</DataTable>
		</div>
	{/if}
</RoleGate>

<Modal bind:open={modalOpen} title={editing ? 'Edit email channel' : 'New email channel'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}

		<Input label="Name" bind:value={fName} required />
		<Textarea label="Description" bind:value={fDescription} rows={2} />

		<div class="grid gap-3 sm:grid-cols-3">
			<Input label="Host" bind:value={fHost} required />
			<Input label="Port" bind:value={fPort} type="number" />
			<Select label="Security" bind:value={fSecurity} options={securityOptions} />
		</div>
		{#if fSecurity === 'none'}
			<Alert tone="warning">
				Plaintext SMTP: credentials and message bodies cross the network unencrypted. Only for a
				relay on a trusted segment.
			</Alert>
		{/if}

		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Username" bind:value={fUsername} hint="Blank for an unauthenticated relay" />
			<Input
				label="Password"
				bind:value={fPassword}
				type="password"
				hint={editing ? 'Leave blank to keep the stored password' : ''}
			/>
		</div>
		{#if editing?.hasPassword}
			<Checkbox bind:checked={fClearPassword} label="Remove the stored password" />
		{/if}

		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="From address" bind:value={fFromAddress} required />
			<Input label="From name" bind:value={fFromName} />
		</div>
		<Input
			label="Default recipients"
			bind:value={fRecipients}
			hint="Comma-separated; used when a send names nobody"
		/>

		<div class="border-t border-surface-200-800 pt-3">
			<div class="mb-1 text-sm font-medium">Mailbox (IMAP)</div>
			<p class="mb-3 text-xs text-surface-600-400">
				Optional. With an IMAP host the assistant and workflows can also read, archive and delete
				mail on this account; leave it blank for an outbound-only channel.
			</p>
			<div class="grid gap-3 sm:grid-cols-3">
				<Input label="IMAP host" bind:value={fImapHost} />
				<Input label="IMAP port" bind:value={fImapPort} type="number" />
				<Select label="IMAP security" bind:value={fImapSecurity} options={imapSecurityOptions} />
			</div>
			{#if fImapHost.trim()}
				<div class="mt-3 grid gap-3 sm:grid-cols-2">
					<Input
						label="IMAP username"
						bind:value={fImapUsername}
						hint="Blank to reuse the SMTP username"
					/>
					<Input
						label="IMAP password"
						bind:value={fImapPassword}
						type="password"
						hint={editing?.hasImapPassword
							? 'Leave blank to keep the stored password'
							: 'Blank to reuse the SMTP password'}
					/>
				</div>
				{#if editing?.hasImapPassword}
					<div class="mt-2">
						<Checkbox bind:checked={fClearImapPassword} label="Remove the stored IMAP password" />
					</div>
				{/if}
			{/if}
		</div>

		<Checkbox bind:checked={fEnabled} label="Enabled" />
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>

<Modal bind:open={testOpen} title={testChannel ? `Send a test through ${testChannel.name}` : 'Send a test'}>
	<div class="space-y-3">
		{#if testChannel}
			<p class="text-sm text-surface-600-400">
				Sends a real message through
				<code class="text-xs">{testChannel.host}:{testChannel.port}</code> — the only honest test of
				an SMTP relay.
			</p>
		{/if}

		<Input
			label="Send to"
			bind:value={testTo}
			hint="Comma-separated; blank uses the channel's default recipients"
		/>

		{#if testSentTo}
			<Alert tone="success">Test sent to {testSentTo.join(', ')}. Check the inbox (and spam).</Alert>
		{:else if testError}
			<Alert tone="error">{testError}</Alert>
		{/if}
	</div>

	{#snippet footer()}
		<Button variant="ghost" onclick={() => (testOpen = false)}>Close</Button>
		<Button variant="primary" loading={testSending} onclick={sendTest}>
			{testSentTo ? 'Send again' : 'Send test'}
		</Button>
	{/snippet}
</Modal>
