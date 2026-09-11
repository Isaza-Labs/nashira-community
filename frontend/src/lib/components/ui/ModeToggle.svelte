<script lang="ts">
	// Dedicated light/dark toggle (sun/moon). One click flips the colour mode.
	import { prefs } from '$lib/stores/prefs.svelte';
	import { Sun, Moon } from 'lucide-svelte';
</script>

<button
	type="button"
	onclick={() => prefs.toggleMode()}
	aria-label={prefs.mode === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
	title={prefs.mode === 'dark' ? 'Light mode' : 'Dark mode'}
	class="inline-flex h-9 w-9 items-center justify-center rounded-lg text-surface-600-400 transition hover:bg-surface-100-900 hover:text-surface-950-50"
>
	{#if prefs.mode === 'dark'}<Sun size={17} />{:else}<Moon size={17} />{/if}
</button>
