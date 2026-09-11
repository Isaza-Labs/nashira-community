// Pure client-side app: SSR and prerender are off for every route. The Node
// adapter serves the client shell and src/hooks.server.ts proxies /api to the
// backend, so browser-only APIs (localStorage, the auth store) never run on the
// server.
export const ssr = false;
export const prerender = false;
