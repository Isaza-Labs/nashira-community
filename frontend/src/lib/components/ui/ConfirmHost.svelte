<script lang="ts">
	import Modal from './Modal.svelte';
	import Button from './Button.svelte';
	import { confirmStore } from './confirm.svelte';

	// Single instance in the root layout; renders whatever confirm() last asked.
	// Built on nashira's Modal (native <dialog>: focus-trap, Esc, inert background).
</script>

{#if confirmStore.options}
	{@const opts = confirmStore.options}
	<Modal
		bind:open={confirmStore.open}
		title={opts.title}
		size="sm"
		onclose={() => confirmStore.resolve(false)}
	>
		{#if opts.message}
			<p class="text-sm leading-relaxed text-surface-700-300">{opts.message}</p>
		{/if}
		{#snippet footer()}
			<Button variant="ghost" onclick={() => confirmStore.resolve(false)}>
				{opts.cancelLabel ?? 'Cancel'}
			</Button>
			<Button
				variant={opts.tone === 'danger' ? 'danger' : 'primary'}
				onclick={() => confirmStore.resolve(true)}
			>
				{opts.confirmLabel ?? 'Confirm'}
			</Button>
		{/snippet}
	</Modal>
{/if}
