// See https://svelte.dev/docs/kit/types#app.d.ts for the app namespace shape.
declare global {
	/** Injected by vite.config.ts: the build the running console was made from. */
	const __NASHIRA_BUILD__: { version: string; commit: string; date: string };

	namespace App {
		// interface Error {}
		// interface Locals {}
		// interface PageData {}
		// interface PageState {}
		// interface Platform {}
	}
}

export {};
