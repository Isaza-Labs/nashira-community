# nashira frontend

Svelte SPA (SvelteKit in SPA mode) that consumes the nashira backend REST/SSE API.
See [`PLAN.md`](./PLAN.md) for the full Phase 7 architecture and slice roadmap.

## Stack

- SvelteKit 2 + Svelte 5 (runes), TypeScript
- Vite 7, `@sveltejs/adapter-static` (pure client-side SPA output)
- Tailwind CSS 4 + Skeleton Labs. The theme is Nashira's own — every token lives in
  `src/app.css`, mapped from the brand identity (`static/Nashira_resumen_identidad_interfaz.pdf`):
  azul `#1D4E89` acts in light, turquesa `#00B2CA` acts/informs in dark, coral `#EE3D23`
  is brand-only, carbón `#222222` / lavanda `#EAECF6` are the two canvases.

## Develop

The backend must be running (dev URL `http://localhost:5280`). Then:

```
npm install
npm run dev
```

The dev server (http://localhost:5173) proxies `/api`, `/health`, `/openapi`, and
`/scalar` to the backend, so the browser talks same-origin (no CORS needed in dev).
Override the target with `VITE_API_TARGET`.

## Build

```
npm run build     # emits the static SPA to build/
npm run preview   # serve the build locally
```

In production the backend serves `build/` from its `wwwroot` (same origin, no CORS).
The Docker image builds this automatically — see `deploy/Dockerfile`.

## Air-gapped (tier T3)

No external runtime assets: fonts use the system stack and icons are bundled. For
offline builds, vendor the npm dependency tree and point `.npmrc` at an internal
registry.
