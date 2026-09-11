# Nashira — Quick start

From a clean checkout to a workflow that ran, on one page. Project context and
policies live in [`README.md`](README.md); this is the shortest path to a
running instance.

Nashira is a **conversational operations platform**. You ask the agent in plain
language; it interprets the request and orchestrates the applicable tools, while
platform services and deterministic, code-based workflows execute under
configured permissions, policies and confirmations. Network operations are its
most extensively validated domain today.

Once a workflow has been created, its normal execution does not depend on an
LLM and does not consume model tokens.

---

## 1. What you need

| Path | Requirements |
|---|---|
| **Docker** (recommended) | Docker Engine + Compose v2. Nothing else. |
| **Native** | .NET 10 SDK, Node 20+, PostgreSQL 17 reachable and writable. |

You also need a JWT signing key. Generate one with `openssl rand -base64 48` —
the backend **refuses to boot** in Production with a short or known-default key.

---

## 2. First run with Docker

```bash
cd deploy
cp .env.example .env
#   -> set JWT_KEY (required)
#   -> set POSTGRES_PASSWORD for anything that is not your laptop
docker compose up -d --build
```

| | URL |
|---|---|
| App | <http://localhost:3006> |
| API | <http://localhost:8080> |
| Health | <http://localhost:8080/health/ready> |

Postgres is deliberately **not** published to the host — the backend reaches it
as `db:5432` on the compose network. For an occasional session:

```bash
docker compose exec db psql -U nashira -d nashira
```

EF migrations are applied on boot with retry, then the seeders populate the
built-in API specs, prompt skills, vendor commands, the baseline snippet
catalogue, the Python import allowlist and the curated knowledge base. A first
boot therefore takes noticeably longer than the ones after it.

---

## 3. Getting an account

This is the one step that differs by environment, so read it before you set
`ASPNETCORE_ENVIRONMENT`.

**Development** — `DevSeedService` creates `admin` / `admin` on boot, and
`POST /api/auth/bootstrap` is available. This is what `nashira_e2e` logs in with.

```bash
# in deploy/.env
ASPNETCORE_ENVIRONMENT=Development
```

**Production** — nothing is seeded, and `POST /api/auth/bootstrap` returns 404:
`AuthController.Bootstrap` is gated on `IsDevelopment()`, with no
`AnyAsync(Users)` fresh-install escape hatch. There is currently **no supported
first-admin path on a Production boot against an empty database**.

> `deploy/.env.example` and `nashira_e2e/README.md` must not promise Production
> bootstrap until the implementation and documentation agree. Use Development
> only for local evaluation. Do not treat switching a populated Development
> instance to Production as a supported production bootstrap procedure.

Either way, change the password immediately: `/account`, or
`POST /api/auth/change-password`.

---

## 4. Make the agent answer

Chat is the app's home — `/` redirects straight to `/chat` — but it needs a model
before it can say anything.

1. Sign in and open **`/admin/providers`** (admin).
2. Register a provider — OpenAI, Anthropic, Gemini, DeepSeek, Kimi, Ollama, or a
   custom endpoint — and mark it active.
3. Ask it something in `/chat`. Execution depends on your role, assigned
   permissions, action risk and configured policies. Low-risk reads generally
   run directly; interactive mutations can require confirmation, while promoted
   workflows operate as pre-authorised artifacts within their policy boundary.

If chat returns no useful answer, verify the active provider and model first,
then check provider credentials, quota, network access and the request logs.

---

## 5. Your first workflow

Follow the in-app guide — **`/docs/quick-start`** — which is written against the
running UI. The short version:

1. **Credential** → `/admin/credentials`. How devices are reached. The value
   never leaves the server; responses carry `has_password`, never the password.
2. **Device** → `/devices`. `device_name` and `ip_address` are required; set
   `platform` too, or vendor-command resolution has nothing to key on. For a
   local lab only, the three `allow_*` flags can remain `true`; restrict them
   deliberately before using shared or production environments.
3. **Workflow** → there is **no graph editor**. A workflow arrives either by
   asking the agent in `/chat`, or via `/workflows` → *Import* (a YAML artifact
   from this instance, or a portable **bundle** — including one authored in Flow
   Weaver). Both land it in `draft`; the `id` and `environment` inside an
   imported file are ignored on purpose.
4. **Read the plan** → open the workflow, **Plan** tab. It shows the materialised
   execution order and whether the whole thing is reversible. That banner is the
   thing to read before clicking anything.
5. **Run workflow** → the dialog collects the runtime inputs (from
   `input_schema`, or inferred from the `{{ input.* }}` references when the
   workflow declares none) and the target devices.
6. **Read the run** → `/runs/{id}`. Step headers first; a step's resolved input,
   output and logs load when you open its card.

The UI blocks direct selection of a device that does not allow the workflow's
environment and flags incompatible inherited selections. At execution time the
engine drops incompatible targets and refuses the run only when no valid target
remains. Promotion is `draft → qa → production`, with four-eyes on the last hop
plus any `gate` policies.

A safe first prompt is:

> Create a draft, read-only workflow named `device-version-check` that collects
> software-version information from the devices I select and returns a concise
> per-device summary. Do not schedule or promote it.

Review the generated plan before running it. A useful result is either a
completed run with a per-device summary or a clearly attributed connection or
command error for each unsuccessful device; an empty success is not sufficient.

---

## 6. Local development (no Docker)

Two terminals. Postgres must be running and reachable with the credentials in
`nashira_backend/appsettings.json`
(`Host=localhost;Database=nashira;Username=nashira;Password=nashira`).

```bash
# backend — http://localhost:5280, Development, seeds admin/admin
dotnet run --project nashira_backend
```

```bash
# frontend
cd frontend
npm ci
npm run dev        # http://localhost:5173
```

The Vite dev server proxies `/api`, `/health`, `/openapi` and `/scalar` to
`http://localhost:5280`, so the browser talks same-origin and CORS never enters
the picture in dev. Point it elsewhere with `VITE_API_TARGET`.

`/openapi/v1.json` and `/scalar` are Development-only.

---

## 7. Tests

```bash
dotnet test nashira.slnx                 # backend unit tests (xUnit)

cd frontend
npm run check                            # svelte-check (types + a11y)
npm run check:theme                      # colour-ramp guard
npm run check:nav                        # navigation registry guard
npm run test:e2e                         # Playwright
```

`nashira_e2e` is deliberately **not** a `dotnet test` project — it is a console
runner that drives the real HTTP API and prints the wire traffic, plus a coverage
diff against the published OpenAPI document. It creates and deletes real data; do
not point it at production.

```bash
dotnet run --project nashira_e2e                                    # vs :5280
dotnet run --project nashira_e2e -- --base-url http://localhost:8080  # vs Docker
```

`workflow-v1-conformance/` holds the cross-implementation suite for the bundle
format shared with Flow Weaver.

---

## 8. When it does not come up

| Symptom | Cause |
|---|---|
| Backend exits at boot | `JWT_KEY` missing, too short, or a known default. |
| Cannot log in on a fresh Production instance | Nothing is seeded and bootstrap 404s — see §3. |
| Chat returns nothing | Verify the active provider and model, credentials, quota, network access and request logs. Start at `/admin/providers`. |
| POSTs rejected by SvelteKit | `FRONTEND_ORIGIN` does not match the URL the browser uses. |
| MCP OAuth redirects to `localhost:5173` | `Cors__AllowedOrigins__0` / `FRONTEND_ORIGIN` unset, so the backend fell back to the dev origin in `appsettings.json`. |
| Every request shows one IP in the audit trail | `TRUSTED_PROXIES` does not match the compose subnet. |
| Backend cannot connect after a rename | Postgres only creates its database and role on a first run against an empty volume. See *Renaming an existing deployment* in [`README.md`](README.md). |

Useful Docker diagnostics:

```bash
docker compose ps
docker compose logs --tail=200 backend frontend db
docker compose down
```

Do not add `-v` to `docker compose down` unless you intentionally want to remove
the database volume and understand the data-loss impact.

---

## 9. Where to go next

| | |
|---|---|
| [`README.md`](README.md) | Project overview, operating principles and repository policies. |
| `deploy/.env.example` | Every setting, with the reasoning inline. |
| `docs/mcp-oauth.md` | Connecting MCP servers that need OAuth; verify this file is included when the documentation package is merged into the code repository. |
| `docs/design-system-convergence.md` | Frontend design system; verify this file is included when the documentation package is merged into the code repository. |
| `integrations/<name>/` | External-system bundles (spec + skill + register script), attached to an Integration rather than seeded. |
| `/docs` in the running app | The full manual — what every part is for, how it works, and the parameters it takes. |
