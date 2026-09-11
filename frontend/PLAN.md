# Nashira Frontend (Phase 7) — Work Plan

Status: planning. Target: a Svelte Single-Page Application that consumes the
existing `nashira_backend` REST/SSE API. This document is the canonical plan for
the frontend track; per-slice detail lands in `nashira/CHANGELOG.md` as work ships.

## 0. Goal and guiding principles

- **Conversational-first**: chat is the primary surface. Everything else is
  secondary. (Requirement §1.5.9 — "la UX es el producto"; target user is a
  non-technical operator.)
- **Reuse flow-weaver's chat technology by copy-and-adapt**, with NO live
  dependency — same rule as the canonical engine. flow-weaver's frontend is
  already SvelteKit + Svelte 5, so this is a direct lift, not a reimplementation.
- **Nashira is a strict UI subset of flow-weaver**: NO visual flow builder, NO
  editable DAG canvas, NO draft/qa/prod promotion UI. Do NOT copy flow-weaver's
  flow-centric layout.
- **Perfectly modularized, reusable components**: a small in-house design system
  (buttons, cards/boxes, modals, alert modals, tables, toasts) consumed by thin,
  feature-scoped pages.
- **Air-gapped ready (tier T3)**: offline build, no runtime external calls (no
  CDN, no external fonts/telemetry). The SPA must be servable entirely inside the
  perimeter.
- Discard the old nashira frontend entirely; rebuild nashira's "face" on FW's base.

## 1. Stack decision

**Chosen: SvelteKit 2 + Svelte 5 (runes) in SPA mode.**

- SvelteKit with `adapter-static` + `export const ssr = false` + a `fallback`
  (`index.html`) produces a pure client-side SPA (static assets only) — satisfies
  "SPA", and the static output is trivially served offline (air-gap) from .NET
  `wwwroot` or any static host.
- This maximizes reuse of flow-weaver's mature code (the auth rotating-refresh
  store and the SSE streaming core are hard to get right from scratch) because
  they are already SvelteKit/Svelte 5.

Toolchain (aligned with flow-weaver so lifts are clean):

| Concern | Choice |
|---|---|
| Framework | SvelteKit 2, Svelte 5 runes (`$state`/`$derived`/`$props`/`$effect`) |
| Language | TypeScript |
| Build | Vite 7, `@sveltejs/adapter-static` (SPA output) |
| Styling | Tailwind CSS 4 (`@tailwindcss/vite`, CSS-first) + Skeleton Labs tokens |
| Icons | `lucide-svelte` (bundled, no CDN) |
| Markdown | `marked` + hardened renderer (`markdown.ts`) |
| Code/diagrams | `highlight.js`; `mermaid` only where needed (plan preview) |
| Tests | Playwright (e2e smoke), Vitest (unit for stores/adapters) |
| Lint/format | ESLint + Prettier (svelte plugins) |

Alternative considered and rejected: plain Vite + Svelte + `svelte-spa-router`.
It is a "purer" SPA but forces converting FW's file-based routes and swapping the
`$app/*` couplings, for no real benefit. SvelteKit-in-SPA-mode is the standard
pattern and keeps the reuse.

## 2. Architecture and modularization

Layered, with per-feature modules. Pages are thin: they compose UI components,
call a typed API module, and read/write stores. No business logic in `.svelte`
pages beyond wiring.

```
nashira/frontend/
  package.json  svelte.config.js  vite.config.ts  tsconfig.json  playwright.config.ts  .npmrc
  static/                         # bundled fonts/icons (no external CDN — air-gap)
  src/
    app.html  app.css             # Tailwind 4 + Skeleton tokens + nashira brand tokens
    hooks.client.ts               # boot: hydrate auth session, global error trap
    routes/                       # file-based routing (thin pages)
      +layout.svelte  +layout.ts  # app shell (nav, Toaster, ConfirmHost); ssr=false
      +page.svelte                # redirect -> /chat
      login/
      chat/  chat/[conversationId]/
      devices/  credentials/  git/  workflows/  knowledge/  inventory/  exports/  audit/
      admin/ { users, permissions, secrets, settings, profiles, providers, skills, specs, learnings, loader }
    lib/
      api/
        client.ts                 # fetch wrapper: auth header, 401->refresh, snake_case map, ProblemDetails
        ai-stream.ts              # SSE POST -> async generator of typed chat events
        *.api.ts                  # one typed module per domain (devices, workflows, git, ...)
      stores/                     # Svelte 5 runes singletons
        auth.svelte.ts            # JWT session + rotating refresh + cross-tab sync
        me.svelte.ts              # current user / role / tenant
        toast.svelte.ts  confirm.svelte.ts  connection.svelte.ts  prefs.svelte.ts
      components/
        ui/                       # the reusable design system (barrel index.ts) — see §3
        chat/                     # ChatMessage, ChatMarkdown, ChatToolCalls, ChatThinkingDots,
                                  # ChatComposer, ConversationList, WorkflowConfirmCard
        layout/                   # AppNav, AppSidebar, UserMenu, RoleGate
        <feature>/                # composite feature components (DeviceForm, WorkflowPlan, ...)
      types/                      # api.ts (DTOs mirroring backend), events.ts (SSE union)
      guards/                     # roles.ts (Viewer/Operator/Admin helpers)
      utils/                      # format.ts, status.ts, icon.ts, markdown.ts
```

**Feature-module rule**: each domain owns `*.api.ts` + its `types` + its feature
components + its route folder. Adding a new backend area = add one api module + one
route + reuse the shared UI kit. That is the "perfectamente modularizada" property.

## 3. Reusable UI component library (design system)

Lifted and rebranded from flow-weaver's `components/ui/` (~30 mature components),
exposed via a single barrel `lib/components/ui/index.ts`. Mapping to the explicit
asks (botones, cards/box, modales, modales de alertas):

| Ask | Components |
|---|---|
| Botones | `Button` (variants/sizes/loading), `IconButton` |
| Cards / box | `Card`, `StatCard`, `EmptyState`, `ErrorState` |
| Modales | `Dialog` (generic modal: header/body/footer, focus-trap, esc/overlay close) |
| Modales de alertas | `AlertDialog` (destructive confirm) + `confirm()` promise API; `Alert` (inline banner); `Toaster` + `toast` store (transient) |
| Tables/lists | `DataTable`, `Pagination`, `SortableTh`, `Toolbar` |
| Forms | `Input`, `Textarea`, `Select`, `Checkbox`, `SearchInput` |
| Status/labels | `Badge`, `StatusBadge`, `Kbd` |
| Structure/feedback | `PageHeader`, `Tabs`, `Spinner`, `Skeleton` |

Two app-wide singletons (also lifted): `toast` (tones, auto-dismiss, undo/retry)
and `confirm()` (promise-based confirmation resolved by a single `ConfirmHost` in
the layout). These give every screen consistent alert/confirm UX for free.

## 4. Reuse-from-flow-weaver map

Source: `C:/Users/rober/Documents/Trabajo/flow-weaver/frontend/`.

**Lift near-verbatim** (rebrand only): `lib/components/ui/*`, `toast.svelte.ts`,
`confirm.svelte.ts`, `lib/stores/auth.svelte.ts` (JWT rotating-refresh + Web-Locks
single-flight + cross-tab sync), `lib/markdown.ts`, `lib/api/client.ts` fetch
wrapper + snake_case anti-corruption layer.

**Adapt** (nashira contract differs — see §13): `lib/api/ai-stream.ts` and the
`Chat*` components + `routes/ai/chat/+page.svelte`.

**Do NOT bring**: FW's flow-centric layout/nav, the editable DAG canvas
(`@xyflow/svelte` editor), and the promotion (draft/qa/prod) UI. Ignore the
separate legacy React app at `nashira_frontend/frontend-react` entirely.

## 5. API integration layer

- **Base**: all endpoints under `/api`, snake_case JSON. List endpoints return
  `{ items, total, limit, offset }` (paged via `?limit=&offset=`). Errors are
  RFC7807 ProblemDetails.
- **`client.ts`**: single `apiFetch` — injects `Authorization: Bearer <access>`,
  maps snake_case<->camelCase at the boundary (types stay idiomatic), surfaces
  ProblemDetails as typed errors routed to `toast`/`ErrorState`.
- **Auth**: access token ~15 min, refresh ~7 days (rotating, replay-detected
  server-side). Preemptive refresh when expiring soon; single-flight refresh on
  401 then retry once; cross-tab sync. Session persisted in localStorage under a
  nashira key.
- **SSE**: `ai-stream.ts` uses `fetch` POST (not `EventSource`, which can't POST a
  body or set auth) -> `response.body.getReader()` -> manual `\n\n` frame split ->
  async generator of typed events. `AbortController` for cancel.

## 6. Routing and role-based access

Backend enforces `RequireAuthenticatedUser` globally + policies Viewer (any
authed), Operator (admin|operator), Admin (admin). The SPA mirrors this with route
guards driven by the `role` from login/`/api/auth/me` (defense-in-depth; the API
remains the source of truth).

- **Operations zone** (Viewer/Operator): Chat, Devices, Git, Workflows, Knowledge,
  Inventory, Exports.
- **Admin zone** (Admin only): Users, Permissions, Credentials, Secrets, Settings,
  Profiles, AI Providers, Prompt Skills, API Specs, Learnings, Audit, Loader.

A `RoleGate` component hides/disables actions the role can't perform (e.g. write
buttons for a Viewer), matching the per-verb policy of each controller.

## 7. Chat (primary surface)

- Composer -> `POST /api/ai/chat` `{ conversation_id?, message }` -> consume the
  SSE async generator; a pure reducer rebuilds the assistant message per event
  (required for Svelte 5 `$state` reactivity).
- Event handling: `conversation` (adopt new id) · `token` (append delta) ·
  `tool_start`/`tool_result` (collapsible tool-call cards, nashira tool labels) ·
  `done` (token/iteration counts) · `error`.
- Conversation history/CRUD via `/api/ai/conversations` (list, detail, delete);
  auto-scroll "follow the stream / jump to latest"; markdown hardened against
  LLM-output XSS.
- File upload in-chat is a §1.5.9 requirement — depends on a backend upload
  endpoint (see §13, gap to confirm).

## 8. Governance-C confirmation surface

Model C: reads/low-risk idempotent ops run inline; bulk or irreversible mutations
follow `canonical workflow -> confirmation -> execution`. The UI surfaces a
**plan preview + rollback safety**, NOT an editable builder:

- Read the materialized workflow via `GET /api/workflows/{id}/plan` (topological
  order + rollback analysis: reversible vs `NonReversible` nodes) and `/yaml`.
- `WorkflowConfirmCard` renders nodes/edges (read-only; `mermaid` or a simple list
  view), highlights non-reversible steps, and offers Confirm -> `POST
  /api/workflows/{id}/run`; then poll `GET /api/workflows/runs/{runId}` for status.
- Coordination item (§13): how a chat turn signals "confirmation required" is not
  in today's SSE event set — resolve before building the in-chat variant.

## 9. Serving and deployment (+ backend enablers)

- **Dev**: Vite dev server on `:5173` with `server.proxy` forwarding `/api`
  (incl. the SSE `/api/ai/chat`) to the backend -> same-origin from the browser,
  so no CORS needed in dev.
- **Prod (recommended, air-gap friendly)**: build static output and serve it from
  the .NET app (`wwwroot` + `UseStaticFiles` + SPA fallback to `index.html`) ->
  single image, same origin, no CORS, no external egress.
- **Offline build (T3)**: pin the toolchain, vendor npm deps (lockfile + optional
  local registry/cache), bundle fonts/icons in `static/` (no CDN), no runtime
  telemetry.

Backend enablers (small, one-time):
1. **CORS** — bind the existing (currently unused) `Cors:AllowedOrigins` config
   (`http://localhost:5173`) via `AddCors`/`UseCors`. Only strictly needed if the
   SPA is ever served cross-origin; the Vite proxy + static-serving paths avoid it,
   but wire it as a safety net.
2. **Static serving** — add `UseStaticFiles` + SPA fallback so .NET can serve the
   built SPA (nothing serves `frontend/` today).
3. **Docker** — add a frontend build stage to `deploy/Dockerfile` that builds the
   SPA and copies it into the backend image `wwwroot` (today it builds only the
   .NET backend + Python SSH runner).

## 10. Language, theming, branding, accessibility

- **Language**: English-only UI (user decision). No i18n layer — plain, consistent
  English copy across the app.
- **Theming**: placeholder theme for now (Skeleton Labs `cerberus` preset), pending
  a later nashira rebrand. Structured as design tokens so the rebrand is a
  token/theme swap, not a component rewrite. Dark mode inherited from Skeleton.
- **Branding**: neutral placeholders now (no FW marks, no invented logo); nashira
  keeps its own identity for the later rebrand. Reinforce the "auditability"
  promise — the Audit viewer + `/verify` tamper-evidence are first-class.
- **UI/UX best practices (required)**: consistent spacing/typography scale,
  accessible components (focus-visible rings, focus-trap in modals, keyboard nav,
  aria labels), responsive layout, and explicit empty/loading/error states on every
  data surface. No external runtime assets (fonts/icons bundled — air-gap).

## 11. Slice roadmap

Each slice is independently shippable and verified (build clean + smoke test +
CHANGELOG entry), mirroring how the backend was sliced.

- **Slice 0 — Foundations**: scaffold SvelteKit SPA (adapter-static, ssr off),
  Tailwind 4 + Skeleton, TS/ESLint/Prettier/Playwright, offline-build config, app
  shell (layout, nav, brand, theme). Backend enablers: CORS + static serving +
  Docker frontend stage. Acceptance: SPA boots, hits `/health`, dev proxy works.
- **Slice 1 — Auth & session**: login page, `auth` store (JWT rotating refresh),
  `me` store, role-based route guards, logout, change-password, global 401
  handling. Acceptance: login -> guarded route -> refresh survives token expiry.
- **Slice 2 — Design system**: lift + rebrand `components/ui/*` + `toast` +
  `confirm` + `DataTable`/`Pagination`. A "/kitchen-sink" dev page exercises every
  component (buttons, cards, modals, alert modals, tables, toasts).
- **Slice 3 — Chat (primary)**: `ai-stream.ts` adapter (nashira endpoint/events),
  chat page, message rendering, tool-call cards, thinking dots, composer,
  conversations history/CRUD. Acceptance: streamed turn with tool calls renders
  end to end. (File upload pending §13.)
- **Slice 4 — Governance confirmation**: `WorkflowConfirmCard` (plan/yaml preview
  + rollback safety), confirm -> run -> run-status. Acceptance: a materialized
  workflow can be reviewed, confirmed, executed, and its run status shown.
- **Slice 5 — Operations CRUD**: Devices, Git (browser/diff/commit), Knowledge,
  Inventory/NetBox sync, Exports (list/download). Built on a generic CRUD pattern
  (DataTable + Dialog forms) reused across areas.
- **Slice 6 — Admin CRUD**: Users, Permissions, Credentials, Secrets, Settings,
  Profiles, AI Providers, Skills, API Specs, Learnings, Loader/validation.
- **Slice 7 — Audit surface**: audit log viewer (filter by entity/action) +
  `/verify` tamper-evidence — the auditability brand surface.
- **Slice 8 — Hardening**: English copy pass, consistent empty/error/loading
  states, a11y pass, offline-build verification, Docker/compose single-image
  wiring, e2e smoke suite.

## 12. Decisions (resolved)

1. **SPA framework packaging** — SvelteKit + `adapter-static` (SPA mode). RESOLVED.
2. **Serving model** — SPA served static from .NET `wwwroot` (single image,
   air-gap friendly); CORS also wired for cross-origin dev. RESOLVED.
3. **Language** — English-only UI, no i18n layer. RESOLVED.
4. **Theme/branding** — neutral placeholder theme (Skeleton `cerberus`) now; nashira
   rebrand later via a token/theme swap. Maintain UI/UX best practices throughout.
   RESOLVED.

## 13. Backend integration notes / gaps to coordinate

1. **SSE endpoint/event naming differs from FW**: nashira is `POST /api/ai/chat`
   with a `token` delta event (FW uses `/api/ai/chat/stream` and `text`). The
   `ai-stream.ts` adapter must target nashira's contract:
   `conversation | token | tool_start | tool_result | done | error`.
2. **In-chat file upload**: §1.5.9 requires it, but `ChatRequest` is
   `{ conversation_id?, message }` and no file-upload controller surfaced in the
   current API inventory. Confirm whether an upload endpoint exists or is a backend
   gap before building Slice 3's upload UI.
3. **Governance confirmation signal**: today's chat SSE set has no
   "confirmation_required"/plan frame. Decide: handle confirmation via the
   standalone Workflows screens (Slice 4), or add a small backend SSE event so the
   confirm card can appear inline in chat.
4. **CORS/static serving/Docker frontend stage**: not wired today (Slice 0
   enablers above).
```
