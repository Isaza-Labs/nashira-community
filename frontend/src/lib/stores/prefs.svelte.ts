// UI preferences: the colour mode (light / dark), persisted to localStorage and
// reflected onto <html data-mode> plus the theme-color meta. The pre-paint script
// in app.html applies the stored mode before hydration; this store keeps it in
// sync afterwards and drives the mode toggle.

import { browser } from '$app/environment';

export type UiMode = 'light' | 'dark';

const KEY = 'nashira:prefs';

// Shell background per mode, mirroring --app-bg in app.css. Drives the
// theme-color meta so the browser chrome matches the app.
const SHELL_BG: Record<UiMode, string> = {
	light: '#eaecf6',
	dark: '#161719'
};

function read(): UiMode {
	if (!browser) return 'dark';
	try {
		const p = JSON.parse(localStorage.getItem(KEY) || '{}');
		return p.mode === 'light' ? 'light' : 'dark';
	} catch {
		return 'dark';
	}
}

class Prefs {
	mode = $state<UiMode>('dark');

	constructor() {
		this.mode = read();
		this.apply();
	}

	apply() {
		if (!browser) return;
		document.documentElement.setAttribute('data-mode', this.mode);
		document
			.querySelector('meta[name="theme-color"]')
			?.setAttribute('content', SHELL_BG[this.mode]);
		try {
			localStorage.setItem(KEY, JSON.stringify({ mode: this.mode }));
		} catch {
			/* storage unavailable — the in-memory prefs still apply */
		}
	}

	setMode(m: UiMode) {
		this.mode = m;
		this.apply();
	}

	toggleMode() {
		this.setMode(this.mode === 'dark' ? 'light' : 'dark');
	}
}

export const prefs = new Prefs();
