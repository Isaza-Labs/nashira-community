import { getMyNavigationVisibility } from '$lib/api/navigation-permissions.api';

class NavigationVisibilityStore {
	visibility = $state<Record<string, boolean>>({});
	loadedUserId = $state<string | null>(null);
	private request = 0;

	get ready(): boolean {
		return this.loadedUserId !== null;
	}

	isReadyFor(userId: string): boolean {
		return this.loadedUserId === userId;
	}

	async load(userId: string, force = false): Promise<void> {
		if (!force && this.loadedUserId === userId) return;
		const token = ++this.request;
		if (this.loadedUserId !== userId) {
			this.visibility = {};
			this.loadedUserId = null;
		}

		try {
			const rows = await getMyNavigationVisibility();
			if (token !== this.request) return;
			this.visibility = Object.fromEntries(rows.map((row) => [row.pageKey, row.visible]));
		} catch {
			if (token !== this.request) return;
			// Visibility is an interface restriction, not the API authorization boundary.
			// If it cannot be loaded, preserve the existing role-based navigation.
			this.visibility = {};
		} finally {
			if (token === this.request) this.loadedUserId = userId;
		}
	}

	clear(): void {
		this.request += 1;
		this.visibility = {};
		this.loadedUserId = null;
	}
}

export const navigationVisibilityStore = new NavigationVisibilityStore();
