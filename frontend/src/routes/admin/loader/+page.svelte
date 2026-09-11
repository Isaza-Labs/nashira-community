<script lang="ts">
	import {
		validateTemplate,
		listValidations,
		type ValidationResult,
		type ValidationRecord,
		type TemplateIssue
	} from '$lib/api/loader.api';
	import {
		PageHeader,
		Card,
		Select,
		Input,
		CodeEditor,
		Button,
		Alert,
		Badge,
		Spinner,
		ErrorState,
		toast
	} from '$lib/components/ui';
	import { CheckCircle2 } from 'lucide-svelte';
	import { timeAgo } from '$lib/utils/time';
	import SectionNav from '$lib/components/layout/SectionNav.svelte';

	const kindOptions = [
		{ value: 'skill', label: 'Prompt skill' },
		{ value: 'spec', label: 'API spec' }
	];

	let fKind = $state('skill');
	let fName = $state('');
	let fContent = $state('');

	// Skills are markdown; specs are YAML unless the content opens as JSON.
	const contentLanguage = $derived(
		fKind === 'spec' ? (fContent.trimStart().startsWith('{') ? 'json' : 'yaml') : 'markdown'
	);

	function onContentFile(filename: string) {
		if (!fName.trim()) fName = filename.replace(/\.(md|markdown|txt|ya?ml|json)$/i, '');
	}
	let validating = $state(false);
	let result = $state<ValidationResult | null>(null);

	let history = $state<ValidationRecord[]>([]);
	let historyLoading = $state(true);
	let historyError = $state<unknown>(null);
	let expanded = $state<string | null>(null);

	async function loadHistory() {
		historyLoading = true;
		historyError = null;
		try {
			history = await listValidations();
		} catch (e) {
			historyError = e;
		} finally {
			historyLoading = false;
		}
	}

	$effect(() => {
		loadHistory();
	});

	async function validate() {
		if (!fContent.trim()) {
			toast.warning('Provide template content to validate.');
			return;
		}
		validating = true;
		result = null;
		try {
			result = await validateTemplate(fKind, fName.trim(), fContent);
			await loadHistory();
		} catch (e) {
			toast.fromError(e, 'Validation failed');
		} finally {
			validating = false;
		}
	}

	function tone(severity: string): 'error' | 'warning' | 'neutral' {
		const s = severity.toLowerCase();
		if (s === 'error' || s === 'critical') return 'error';
		if (s === 'warning' || s === 'warn') return 'warning';
		return 'neutral';
	}

	function fmt(iso: string): string {
		return timeAgo(iso);
	}
</script>

<svelte:head><title>Loader · Nashira</title></svelte:head>

<SectionNav id="ai-studio" />

<PageHeader title="Loader" description="Dry-run security validation for skills and specs." />

<div class="grid gap-4 lg:grid-cols-2">
	<Card title="Validate">
		<div class="space-y-3">
			<div class="grid gap-3 sm:grid-cols-2">
				<Select label="Kind" bind:value={fKind} options={kindOptions} />
				<Input label="Name" bind:value={fName} hint="Target template name" />
			</div>
			<CodeEditor
				label="Content"
				bind:value={fContent}
				language={contentLanguage}
				rows={12}
				accept={fKind === 'spec' ? '.yaml,.yml,.json' : '.md,.markdown,.txt'}
				onfile={onContentFile}
				hint="Template content to check — paste it or load the file"
			/>
			<div class="flex justify-end">
				<Button variant="primary" loading={validating} onclick={validate}>Validate</Button>
			</div>

			{#if result}
				{#if result.ok && result.issues.length === 0}
					<Alert tone="success" title="Passed">No security issues found.</Alert>
				{:else}
					{#if result.ok}
						<Alert tone="warning" title="Passed with warnings">
							{result.issues.length} issue{result.issues.length === 1 ? '' : 's'} found.
						</Alert>
					{:else}
						<Alert tone="error" title="Rejected">
							{result.issues.length} issue{result.issues.length === 1 ? '' : 's'} found.
						</Alert>
					{/if}
					<ul class="space-y-1.5">
						{#each result.issues as issue, i (i)}
							<li class="flex items-start gap-2 text-sm">
								<Badge tone={tone(issue.severity)}>{issue.severity}</Badge>
								<span class="text-surface-700-300">{issue.message}</span>
							</li>
						{/each}
					</ul>
				{/if}
			{/if}
		</div>
	</Card>

	<Card title="Recent validations">
		{#if historyLoading}
			<div class="flex justify-center py-10"><Spinner /></div>
		{:else if historyError}
			<ErrorState error={historyError} onRetry={loadHistory} compact />
		{:else if history.length === 0}
			<p class="py-6 text-center text-sm text-surface-600-400">No validations yet.</p>
		{:else}
			<div class="divide-y divide-surface-100-900">
				{#each history as r (r.id)}
					<button
						type="button"
						onclick={() => (expanded = expanded === r.id ? null : r.id)}
						class="w-full py-2.5 text-left first:pt-0"
					>
						<div class="flex items-center gap-2">
							{#if r.ok}
								<CheckCircle2 size={14} class="shrink-0 text-success-600-400" />
							{:else}
								<Badge tone="error">rejected</Badge>
							{/if}
							<span class="flex-1 truncate text-sm font-medium">{r.targetName || '(unnamed)'}</span>
							<Badge>{r.kind}</Badge>
							<span class="text-xs tabular-nums text-surface-600-400">{fmt(r.at)}</span>
						</div>
						{#if expanded === r.id && r.issues.length > 0}
							<ul class="mt-2 space-y-1 pl-6">
								{#each r.issues as issue, i (i)}
									<li class="flex items-start gap-2 text-xs">
										<Badge tone={tone(issue.severity)}>{issue.severity}</Badge>
										<span class="text-surface-600-400">{issue.message}</span>
									</li>
								{/each}
							</ul>
						{/if}
					</button>
				{/each}
			</div>
		{/if}
	</Card>
</div>
