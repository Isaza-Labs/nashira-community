<script lang="ts">
	import { listExports, downloadExport, type ExportArtifact } from '$lib/api/exports.api';
	import { timeAgo } from '$lib/utils/time';
	import { PageHeader, Button, Spinner, ErrorState, toast } from '$lib/components/ui';
	import { Download, FileText } from 'lucide-svelte';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	let items = $state<ExportArtifact[]>([]);
	let loading = $state(true);
	let error = $state<unknown>(null);
	let downloadingId = $state<string | null>(null);

	async function load() {
		loading = true;
		error = null;
		try {
			items = (await listExports(100, 0)).items;
		} catch (e) {
			error = e;
		} finally {
			loading = false;
		}
	}

	$effect(() => {
		load();
	});

	async function download(a: ExportArtifact) {
		downloadingId = a.id;
		try {
			await downloadExport(a.id, a.fileName);
		} catch (e) {
			toast.fromError(e, "Couldn't download the export");
		} finally {
			downloadingId = null;
		}
	}

	function fmtBytes(n: number): string {
		if (n < 1024) return `${n} B`;
		if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
		return `${(n / 1024 / 1024).toFixed(2)} MB`;
	}

	function fmt(iso: string): string {
		return timeAgo(iso);
	}
</script>

<svelte:head><title>Exports · Nashira</title></svelte:head>

<SectionNav id="artifacts" />

<PageHeader title="Exports" description="Download generated export artifacts." />

{#if loading}
	<div class="flex justify-center py-16"><Spinner size="lg" /></div>
{:else if error}
	<ErrorState {error} onRetry={load} />
{:else if items.length === 0}
	<div
		class="rounded-xl border border-surface-200-800 px-4 py-16 text-center text-sm text-surface-600-400"
	>
		No exports yet. They are produced by the export tool during a chat.
	</div>
{:else}
	<div class="overflow-hidden rounded-xl border border-surface-200-800">
		{#each items as a (a.id)}
			<div
				class="flex items-center gap-3 border-b border-surface-100-900 px-4 py-3 last:border-0"
			>
				<div
					class="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-surface-100-900 text-surface-600-400"
				>
					<FileText size={16} />
				</div>
				<div class="min-w-0 flex-1">
					<div class="truncate font-medium">{a.fileName}</div>
					<div class="text-xs text-surface-600-400">
						{fmtBytes(a.sizeBytes)}, {a.contentType || 'unknown type'}, {fmt(a.createdAt)}
					</div>
				</div>
				<Button
					size="sm"
					variant="secondary"
					loading={downloadingId === a.id}
					onclick={() => download(a)}
				>
					<Download size={14} />Download
				</Button>
			</div>
		{/each}
	</div>
{/if}
