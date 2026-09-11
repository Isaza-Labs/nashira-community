import adapter from '@sveltejs/adapter-node';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

/** @type {import("@sveltejs/kit").Config} */
const config = {
	preprocess: vitePreprocess(),
	kit: {
		// Node server in its own container (`node build`). Routes stay client-rendered
		// (SSR disabled in src/routes/+layout.ts); src/hooks.server.ts proxies /api,
		// /health, /openapi and /scalar to the backend so the browser stays same-origin.
		adapter: adapter({ out: 'build' })
	}
};

export default config;
