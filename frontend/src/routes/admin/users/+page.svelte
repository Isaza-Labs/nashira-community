<script lang="ts">
	import { page } from '$app/state';
	import { listUsers, createUser, updateUser, deleteUser, type User } from '$lib/api/users.api';
	import {
		listProfiles,
		getUserProfile,
		setUserProfile,
		MAX_CUSTOM_PROFILE_TEXT,
		type Profile
	} from '$lib/api/profiles.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		Badge,
		StatusBadge,
		IconButton,
		Input,
		Select,
		Checkbox,
		Textarea,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import { ApiError } from '$lib/api/client';
	import { authStore } from '$lib/stores/auth.svelte';
	import { moduleStore } from '$lib/stores/modules.svelte';
	import { passwordChecks, passwordPolicyError, PASSWORD_MIN_LENGTH } from '$lib/auth/password';
	import { Plus, Pencil, Trash2, Check, Dot } from 'lucide-svelte';

	// Creating an account is core's; delivering the password is not. Where the
	// deployment has no Communications, the form says so before the account exists.
	const canEmailCredentials = $derived(moduleStore.isEnabled('communications'));

	const roleOptions = [
		{ value: 'viewer', label: 'Viewer' },
		{ value: 'operator', label: 'Operator' },
		{ value: 'admin', label: 'Admin' }
	];

	let items = $state<User[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<User | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fUsername = $state('');
	let fEmail = $state('');
	let fRole = $state('viewer');
	let fActive = $state(true);
	let fPassword = $state('');

	// Assistant profile assignment (edit only — a new user has no id yet). The
	// text is the user's own; it is read on open so saving does not wipe it.
	let profiles = $state<Profile[]>([]);
	let fProfileId = $state('');
	let fCustomText = $state('');
	let loadedProfileId = $state('');
	let loadedCustomText = $state('');
	const profileOptions = $derived([
		{ value: '', label: 'No profile' },
		...profiles.map((p) => ({ value: p.id, label: p.displayName }))
	]);
	const profileName = $derived(new Map(profiles.map((p) => [p.id, p.displayName])));
	const profileChanged = $derived(fProfileId !== loadedProfileId || fCustomText !== loadedCustomText);

	// On edit a blank password means "keep the current one", so the rules only apply
	// once something has been typed.
	const passwordRequired = $derived(!editing);
	const checks = $derived(passwordChecks(fPassword, fUsername));

	const columns: Column[] = [
		{ key: 'username', header: 'Username' },
		{ key: 'email', header: 'Email' },
		{ key: 'role', header: 'Role' },
		{ key: 'profile', header: 'Profile' },
		{ key: 'status', header: 'Status' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			const [users, prof] = await Promise.all([listUsers(), listProfiles()]);
			items = users.items;
			profiles = [...prof.items].sort((a, b) => a.displayOrder - b.displayOrder);
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	function openCreate() {
		editing = null;
		formError = '';
		fUsername = '';
		fEmail = '';
		fRole = 'viewer';
		fActive = true;
		fPassword = '';
		modalOpen = true;
	}

	async function openEdit(u: User) {
		editing = u;
		formError = '';
		fUsername = u.username;
		fEmail = u.email;
		fRole = u.role;
		fActive = u.isActive;
		fPassword = '';
		fProfileId = loadedProfileId = u.profileId ?? '';
		fCustomText = loadedCustomText = '';
		modalOpen = true;
		try {
			const mine = await getUserProfile(u.id);
			fProfileId = loadedProfileId = mine.profileId ?? '';
			fCustomText = loadedCustomText = mine.customProfileText ?? '';
		} catch (e) {
			toast.fromError(e, "Couldn't read the user's assistant profile");
		}
	}

	async function save() {
		if (!editing && (!fUsername.trim() || !fPassword)) {
			formError = 'Username and password are required.';
			return;
		}
		if (!fEmail.trim()) {
			formError = 'Email is required.';
			return;
		}
		if (fCustomText.length > MAX_CUSTOM_PROFILE_TEXT) {
			formError = `The profile text must be at most ${MAX_CUSTOM_PROFILE_TEXT} characters.`;
			return;
		}
		// Checked here so the rejection lands in this modal. The server enforces the
		// same policy, but its answer arrives as a toast the open modal covers.
		if (passwordRequired || fPassword) {
			const weak = passwordPolicyError(fPassword, fUsername);
			if (weak) {
				formError = weak;
				return;
			}
		}
		formError = '';
		saving = true;
		try {
			if (editing) {
				await updateUser(editing.id, {
					email: fEmail.trim(),
					role: fRole,
					isActive: fActive,
					password: fPassword || undefined
				});
				if (profileChanged)
					await setUserProfile(editing.id, { profileId: fProfileId || null, customProfileText: fCustomText });
				modalOpen = false;
				toast.success('User updated');
			} else {
				const { warning } = await createUser({
					username: fUsername.trim(),
					email: fEmail.trim(),
					password: fPassword,
					role: fRole
				});
				modalOpen = false;
				// The server says whether the credentials email actually went out; a
				// missing SMTP relay — or a deployment without Communications at all —
				// must not read as "the user got their password".
				if (warning) toast.warning(warning);
				else toast.success('User created — credentials sent by email.');
			}
			await load();
		} catch (e) {
			// A rejected field belongs next to the field. Only genuine request failures
			// (network, 500, expired session) go to a toast, which the modal would hide.
			if (e instanceof ApiError && (e.status === 400 || e.status === 409)) {
				formError = e.userMessage;
			} else {
				toast.fromError(e, 'Failed to save the user');
			}
		} finally {
			saving = false;
		}
	}

	async function remove(u: User) {
		if (u.id === authStore.session?.userId) {
			toast.warning('You cannot delete your own account.');
			return;
		}
		const ok = await confirm({
			title: 'Delete user?',
			message: `"${u.username}" will be deactivated.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteUser(u.id);
			items = items.filter((x) => x.id !== u.id);
			toast.success('User deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the user");
		}
	}

	// The command palette's create action lands here with ?new=1.
	$effect(() => {
		if (page.url.searchParams.get('new') === '1' && !modalOpen) openCreate();
	});
</script>

<svelte:head><title>Users · Nashira</title></svelte:head>

<PageHeader title="Users" description="Accounts and roles.">
	{#snippet actions()}
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New user</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(u) => u.id} empty="No users.">
		{#snippet cell(row, col)}
			{#if col.key === 'username'}
				<span class="font-medium">{row.username}</span>
			{:else if col.key === 'email'}
				<span class="text-surface-600-400">{row.email}</span>
			{:else if col.key === 'role'}
				<Badge tone={row.role === 'admin' ? 'primary' : 'neutral'}>{row.role}</Badge>
			{:else if col.key === 'profile'}
				{#if row.profileId && profileName.has(row.profileId)}
					<span>{profileName.get(row.profileId)}</span>
				{:else}
					<span class="text-surface-600-400">—</span>
				{/if}
			{:else if col.key === 'status'}
				{#if row.locked}
					<StatusBadge status="locked" />
				{:else}
					<StatusBadge status={row.isActive ? 'active' : 'disabled'} />
				{/if}
			{:else if col.key === 'actions'}
				<div class="flex justify-end gap-1">
					<IconButton label="Edit user" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
					<IconButton label="Delete user" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
				</div>
			{/if}
		{/snippet}
	</DataTable>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit user' : 'New user'}>
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}
		<!-- Said before the account exists, not after. The backend creates the user and
		     reports that the mail did not go out, but an admin who learns that from a
		     toast has already handed out a password they now have to chase. -->
		{#if !editing && !canEmailCredentials}
			<Alert tone="warning" title="Credentials will not be emailed">
				This deployment does not run Communications, so the temporary password has to
				reach the user another way. Note it down before saving.
			</Alert>
		{/if}
		<Input label="Username" bind:value={fUsername} disabled={!!editing} required={!editing} />
		<Input label="Email" bind:value={fEmail} type="email" required />
		<div class="grid gap-3 sm:grid-cols-2">
			<Select label="Role" bind:value={fRole} options={roleOptions} />
			{#if editing}
				<div class="flex items-end pb-2"><Checkbox bind:checked={fActive} label="Active" /></div>
			{/if}
		</div>
		<div>
			<Input
				label={editing ? 'New password' : 'Password'}
				bind:value={fPassword}
				type="password"
				required={!editing}
				hint={editing
					? 'Leave blank to keep the current password'
					: `At least ${PASSWORD_MIN_LENGTH} characters, with upper and lower case, a digit and a symbol`}
			/>

			<!-- The rules are shown as they are met rather than only on rejection: the
			     server enforces the same policy, but its answer arrives after the form
			     is submitted. -->
			{#if fPassword}
				<ul class="mt-2 grid gap-x-4 gap-y-1 sm:grid-cols-2">
					{#each checks as c (c.label)}
						<li
							class="flex items-center gap-1 text-xs {c.ok
								? 'text-success-700-300'
								: 'text-surface-600-400'}"
						>
							{#if c.ok}<Check size={13} />{:else}<Dot size={13} />{/if}
							<span>{c.label}</span>
						</li>
					{/each}
				</ul>
			{/if}
		</div>
		{#if editing}
			<div class="space-y-3 border-t border-surface-200-800 pt-3">
				<div>
					<div class="text-sm font-medium">Assistant profile</div>
					<p class="text-xs text-surface-600-400">
						Sets the tone and depth of the assistant's answers to this user. The text is
						what the user wrote about themselves; edit it only if they asked you to.
					</p>
				</div>
				<Select label="Profile" bind:value={fProfileId} options={profileOptions} />
				<Textarea
					label="About the user"
					rows={3}
					bind:value={fCustomText}
					maxlength={MAX_CUSTOM_PROFILE_TEXT}
					hint={`${fCustomText.length}/${MAX_CUSTOM_PROFILE_TEXT}`}
				/>
			</div>
		{/if}
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>
