// Saving a fetched blob to disk. Shared by every authenticated download in the
// app: the endpoints need the bearer token, so the bytes are fetched through the
// API client and handed to the browser from memory rather than linked to.

// The object URL is released on a timer, not right after click(): revoking it
// synchronously cancels the download in Firefox and Safari, which is how a
// "working" link ends up producing no file.
export function saveBlob(blob: Blob, fileName: string): void {
	const url = URL.createObjectURL(blob);
	const a = document.createElement('a');
	a.href = url;
	a.download = fileName;
	a.rel = 'noopener';
	document.body.appendChild(a);
	a.click();
	a.remove();
	setTimeout(() => URL.revokeObjectURL(url), 60_000);
}
