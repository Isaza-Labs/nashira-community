import type { Handle } from '@sveltejs/kit';

// The frontend runs as its own Node service (deploy/Dockerfile.frontend). Behind
// the Node adapter the Vite dev proxy is gone, but the browser still fetches
// /api/... same-origin from the frontend container. This hook forwards those
// paths to the backend (BACKEND_URL, set by docker-compose to http://backend:8080)
// and streams the upstream response back unchanged — including the SSE chat
// stream. In `npm run dev` the Vite proxy handles these before SvelteKit sees them.

const BACKEND_URL = process.env.BACKEND_URL ?? 'http://localhost:5280';
const PROXY_PREFIXES = ['/api/', '/health/', '/openapi/', '/scalar'];

export const handle: Handle = async ({ event, resolve }) => {
	const { pathname, search } = event.url;

	const proxied = PROXY_PREFIXES.some(
		(p) => pathname === p.replace(/\/$/, '') || pathname.startsWith(p)
	);
	if (!proxied) return resolve(event);

	const target = `${BACKEND_URL}${pathname}${search}`;

	// Forward method, headers and body; drop hop-by-hop headers Node rejects.
	// Also drop the browser Origin/Referer: this is a server-to-server proxy, so
	// the backend's CORS check would otherwise flag the frontend origin (noise).
	const headers = new Headers(event.request.headers);
	headers.delete('host');
	headers.delete('connection');
	headers.delete('content-length');
	headers.delete('origin');
	headers.delete('referer');

	// The backend builds the MCP OAuth redirect_uri from the request host; through
	// this proxy that would be the internal Docker hostname (backend:8080), which
	// the browser cannot reach. Forward the browser-facing origin instead.
	headers.set('x-forwarded-proto', event.url.protocol.replace(/:$/, ''));
	headers.set('x-forwarded-host', event.url.host);

	// Forward the real client address so the backend's per-IP login limiter and
	// the audit/auth-event trails see the browser, not this container. Appending
	// (rather than setting) preserves any chain from a proxy in front of us —
	// cloudflared, nginx, an ALB. The backend only honours these headers from
	// proxies the operator listed in Network__TrustedProxies, so a forged inbound
	// value can't survive: it is either dropped, or the operator has trusted this
	// hop and we are the ones appending the truth.
	const clientAddress = event.getClientAddress();
	const forwardedFor = event.request.headers.get('x-forwarded-for');
	headers.set('x-forwarded-for', forwardedFor ? `${forwardedFor}, ${clientAddress}` : clientAddress);

	const init: RequestInit = { method: event.request.method, headers, redirect: 'manual' };
	if (event.request.method !== 'GET' && event.request.method !== 'HEAD') {
		init.body = await event.request.arrayBuffer();
	}

	try {
		const upstream = await fetch(target, init);
		// Copy the upstream response 1:1 (streams the SSE body; keeps Set-Cookie etc.).
		return new Response(upstream.body, {
			status: upstream.status,
			statusText: upstream.statusText,
			headers: new Headers(upstream.headers)
		});
	} catch (err) {
		const message = err instanceof Error ? err.message : String(err);
		return new Response(JSON.stringify({ error: `backend_unreachable: ${message}` }), {
			status: 502,
			headers: { 'content-type': 'application/json' }
		});
	}
};
