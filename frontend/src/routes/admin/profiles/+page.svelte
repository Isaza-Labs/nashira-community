<script lang="ts">
	import {
		listProfiles,
		createProfile,
		updateProfile,
		deleteProfile,
		type Profile
	} from '$lib/api/profiles.api';
	import {
		PageHeader,
		Button,
		Modal,
		DataTable,
		IconButton,
		Input,
		Textarea,
		Alert,
		ErrorState,
		confirm,
		toast,
		type Column
	} from '$lib/components/ui';
	import { Plus, Pencil, Trash2 } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	let items = $state<Profile[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);

	let modalOpen = $state(false);
	let editing = $state<Profile | null>(null);
	let saving = $state(false);
	let formError = $state('');

	let fName = $state('');
	let fDisplay = $state('');
	let fDescription = $state('');
	let fSkills = $state('');
	let fStyle = $state('');
	let fOrder = $state('0');

	const columns: Column[] = [
		{ key: 'displayName', header: 'Display name' },
		{ key: 'name', header: 'Name' },
		{ key: 'displayOrder', header: 'Order' },
		{ key: 'actions', header: '', align: 'right' }
	];

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listProfiles()).items;
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
		fName = '';
		fDisplay = '';
		fDescription = '';
		fSkills = '';
		fStyle = '';
		fOrder = '0';
		modalOpen = true;
	}

	function openEdit(p: Profile) {
		editing = p;
		formError = '';
		fName = p.name;
		fDisplay = p.displayName;
		fDescription = p.description ?? '';
		fSkills = p.skills.join(', ');
		fStyle = p.responseStyle ?? '';
		fOrder = String(p.displayOrder);
		modalOpen = true;
	}

	async function save() {
		if (!editing && !fName.trim()) {
			formError = 'Name is required.';
			return;
		}
		if (!fDisplay.trim()) {
			formError = 'Display name is required.';
			return;
		}
		const skills = fSkills.split(',').map((s) => s.trim()).filter(Boolean);
		const order = Number.parseInt(fOrder, 10) || 0;
		saving = true;
		try {
			const common = {
				displayName: fDisplay.trim(),
				description: fDescription,
				skills,
				responseStyle: fStyle.trim(),
				displayOrder: order
			};
			if (editing) await updateProfile(editing.id, common);
			else await createProfile({ name: fName.trim(), ...common });
			modalOpen = false;
			toast.success(editing ? 'Profile updated' : 'Profile created');
			await load();
		} catch (e) {
			toast.fromError(e, 'Failed to save the profile');
		} finally {
			saving = false;
		}
	}

	async function remove(p: Profile) {
		const ok = await confirm({
			title: 'Delete profile?',
			message: `"${p.displayName}" will be removed.`,
			tone: 'danger',
			confirmLabel: 'Delete'
		});
		if (!ok) return;
		try {
			await deleteProfile(p.id);
			items = items.filter((x) => x.id !== p.id);
			toast.success('Profile deleted');
		} catch (e) {
			toast.fromError(e, "Couldn't delete the profile");
		}
	}
</script>

<svelte:head><title>Profiles · Nashira</title></svelte:head>

<SectionNav id="ai-studio" />

<PageHeader title="Profiles" description="Personas that set the tone and depth of the assistant's answers, per user.">
	{#snippet actions()}
		<Button variant="primary" onclick={openCreate}><Plus size={15} />New profile</Button>
	{/snippet}
</PageHeader>

{#if error}
	<ErrorState {error} onRetry={load} />
{:else}
	<DataTable {loading} {columns} rows={items} rowKey={(p) => p.id} empty="No profiles — a profile names a response style and described skills; assigned to a user, it steers the assistant's answers to them.">
		{#snippet cell(row, col)}
			{#if col.key === 'displayName'}
				<span class="font-medium">{row.displayName}</span>
			{:else if col.key === 'name'}
				<code class="text-xs text-surface-600-400">{row.name}</code>
			{:else if col.key === 'displayOrder'}
				<span class="tabular-nums text-surface-600-400">{row.displayOrder}</span>
			{:else if col.key === 'actions'}
				<div class="flex justify-end gap-1">
					<IconButton label="Edit profile" onclick={() => openEdit(row)}><Pencil size={14} /></IconButton>
					<IconButton label="Delete profile" onclick={() => remove(row)}><Trash2 size={14} /></IconButton>
				</div>
			{/if}
		{/snippet}
	</DataTable>
{/if}

<Modal bind:open={modalOpen} title={editing ? 'Edit profile' : 'New profile'} size="lg">
	<div class="space-y-3">
		{#if formError}<Alert tone="error">{formError}</Alert>{/if}
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Name" bind:value={fName} disabled={!!editing} required={!editing} hint="Stable identifier" />
			<Input label="Display name" bind:value={fDisplay} required />
		</div>
		<Textarea label="Description" bind:value={fDescription} rows={2} />
		<Input label="Skills" bind:value={fSkills} hint="Comma-separated skill names" />
		<div class="grid gap-3 sm:grid-cols-2">
			<Input label="Response style" bind:value={fStyle} hint="e.g. concise, detailed" />
			<Input label="Display order" bind:value={fOrder} type="number" />
		</div>
	</div>
	{#snippet footer()}
		<Button variant="ghost" onclick={() => (modalOpen = false)}>Cancel</Button>
		<Button variant="primary" loading={saving} onclick={save}>Save</Button>
	{/snippet}
</Modal>
