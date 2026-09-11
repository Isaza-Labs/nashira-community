import { getModuleManifest } from '$lib/api/modules.api';
import { ModuleAvailability, type ModuleId } from '$lib/modules/manifest';

export type ModuleStoreStatus = 'idle' | 'loading' | 'ready' | 'error';

// The deployment's capabilities, loaded once after authentication and read by every
// surface that decides what to offer: sidebar, palette, sections, admin hub, route
// guard. The manifest is authenticated, so it cannot be fetched before login.
//
// Three states rather than a boolean, because they lead to three different screens:
// `loading` must render nothing decisive (a menu drawn from an unanswered manifest
// flashes pages that then vanish), `ready` renders the real deployment, and `error`
// renders core with a way to retry. There is no fourth state where everything is
// assumed on.
class ModuleStore {
	status = $state<ModuleStoreStatus>('idle');
	// Never null once anything has been attempted: on failure this holds the
	// fail-closed answer, so every reader can ask the same question the same way
	// without each one inventing its own behaviour for "not loaded".
	availability = $state<ModuleAvailability>(ModuleAvailability.failClosed());
	loadedUserId = $state<string | null>(null);
	error = $state<string | null>(null);

	// Guards against an out-of-order answer: a slow response for a user who has since
	// signed out must not install itself over the next one's.
	private request = 0;

	// Deliberately a plain field, not $state, and read before anything reactive is
	// touched. `load` is called from an effect, and an effect subscribes to whatever
	// state it reads while running: guarding on `status` would make this store's own
	// writes re-trigger the effect that called it, which loops forever on the failure
	// path — where the guard never becomes true — and never settles on an answer.
	private attemptedFor: string | null = null;

	get ready(): boolean {
		return this.status === 'ready';
	}

	isReadyFor(userId: string): boolean {
		return this.status === 'ready' && this.loadedUserId === userId;
	}

	isEnabled(id: ModuleId): boolean {
		return this.availability.isEnabled(id);
	}

	areEnabled(ids: readonly ModuleId[]): boolean {
		return this.availability.areEnabled(ids);
	}

	async load(userId: string, force = false): Promise<void> {
		if (!force && this.attemptedFor === userId) return;
		this.attemptedFor = userId;

		const token = ++this.request;
		this.status = 'loading';
		this.error = null;
		if (this.loadedUserId !== userId) {
			this.availability = ModuleAvailability.failClosed();
			this.loadedUserId = null;
		}

		try {
			const manifest = await getModuleManifest();
			if (token !== this.request) return;
			this.availability = ModuleAvailability.from(manifest);
			this.loadedUserId = userId;
			this.status = 'ready';
		} catch (err) {
			if (token !== this.request) return;
			// Fail closed. Assuming the modules are all on would fill the menu with
			// pages that answer 503 and fire requests at capabilities this deployment
			// may never have had; showing core is smaller, honest, and recoverable.
			this.availability = ModuleAvailability.failClosed();
			this.loadedUserId = userId;
			this.error = err instanceof Error ? err.message : 'The module manifest could not be loaded';
			this.status = 'error';
		}
	}

	retry(userId: string): Promise<void> {
		return this.load(userId, true);
	}

	clear(): void {
		this.request += 1;
		this.attemptedFor = null;
		this.availability = ModuleAvailability.failClosed();
		this.loadedUserId = null;
		this.error = null;
		this.status = 'idle';
	}
}

export const moduleStore = new ModuleStore();
