// Clipboard writes that survive insecure origins.
//
// navigator.clipboard only exists in secure contexts (HTTPS or localhost), so
// on a LAN deployment reached as http://<ip>:3006 it is undefined and every
// "copy" button dies in its catch block. The legacy execCommand('copy') path
// still works there — deprecated, but deprecated-and-working beats a toast
// apologising that copy failed. Callers get a boolean and decide what showing
// the value anyway looks like for their surface.
export async function copyText(text: string): Promise<boolean> {
	if (typeof navigator !== 'undefined' && navigator.clipboard) {
		try {
			await navigator.clipboard.writeText(text);
			return true;
		} catch {
			// Permission denied — fall through to the legacy path rather than
			// giving up: the two mechanisms fail for independent reasons.
		}
	}

	if (typeof document === 'undefined') return false;

	// Off-screen (not display:none — some browsers refuse to copy from an
	// invisible element) and read-only so the keyboard never opens on touch
	// devices.
	const ta = document.createElement('textarea');
	ta.value = text;
	ta.setAttribute('readonly', '');
	ta.style.position = 'fixed';
	ta.style.top = '-1000px';
	ta.style.opacity = '0';
	document.body.appendChild(ta);

	// Preserve the user's selection: execCommand copies the textarea's
	// selection, which requires stealing focus for a moment.
	const active = document.activeElement as HTMLElement | null;
	ta.select();
	ta.setSelectionRange(0, text.length);

	let ok = false;
	try {
		ok = document.execCommand('copy');
	} catch {
		ok = false;
	}

	ta.remove();
	active?.focus?.();
	return ok;
}
