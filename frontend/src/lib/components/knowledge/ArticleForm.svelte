<script lang="ts">
	import { untrack } from 'svelte';
	import { Input, Textarea, Alert } from '$lib/components/ui';
	import type { Article, ArticlePayload } from '$lib/api/knowledge.api';

	// Create/edit form for a knowledge article. Submitted from the modal footer via
	// form="article-form". Remounts per open, so `initial` seeds once.
	let {
		initial = null,
		onsave
	}: { initial?: Article | null; onsave: (payload: ArticlePayload) => void } = $props();

	const seed = untrack(() => initial);
	let title = $state(seed?.title ?? '');
	let content = $state(seed?.content ?? '');
	let tags = $state((seed?.tags ?? []).join(', '));
	let error = $state('');

	function submit(e: SubmitEvent) {
		e.preventDefault();
		if (!title.trim()) {
			error = 'Title is required.';
			return;
		}
		error = '';
		const tagList = tags
			.split(',')
			.map((t) => t.trim().toLowerCase())
			.filter(Boolean);
		onsave({ title: title.trim(), content, tags: tagList });
	}
</script>

<form id="article-form" onsubmit={submit} class="space-y-3">
	{#if error}<Alert tone="error">{error}</Alert>{/if}
	<Input label="Title" bind:value={title} required />
	<Input label="Tags" bind:value={tags} hint="Comma-separated" />
	<Textarea label="Content" bind:value={content} rows={10} hint="Markdown supported" />
</form>
