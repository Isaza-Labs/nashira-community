# Nashira — Quick start

From a clean checkout to a workflow that ran, on one page. Architecture and
conventions live in [`README.md`](README.md); this is only the path to a running
instance.

Nashira is a **conversational operations platform backed by deterministic,
code-based workflows**. Network automation is currently its most extensively
validated operational domain.

The agent interprets and orchestrates. Platform services and the deterministic
workflow engine perform the operations, under configured permissions, policies
and confirmations.

> Commands below assume **bash** (`cp`, `openssl`). On Windows use Git Bash or
> WSL, or substitute `Copy-Item` and any 32+ character random string generator.

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

Everyday diagnostics:

```bash
docker compose ps                        # what is up, and healthy
docker compose logs -f backend           # follow the backend
docker compose logs backend | grep -i migration
docker compose exec db psql -U nashira -d nashira
docker compose down                      # stop; add -v to also drop the volume
```

Postgres is deliberately **not** published to the host — the backend reaches it
as `db:5432` on the compose network.

EF migrations are applied on boot with retry, then the seeders populate the
built-in API specs, prompt skills, vendor commands, the baseline snippet
catalogue, the Python import allowlist and the curated knowledge base. A first
boot therefore takes noticeably longer than the ones after it.

### Select deployment modules

If `NASHIRA_MODULES` is absent from `deploy/.env`, Nashira keeps every capability
enabled for compatibility with existing deployments. To use an explicit
allowlist, add for example:

```env
NASHIRA_MODULES=chat,ai-studio,integrations,secrets
```

`core` is implicit and always available. Dependencies are strict and must also
appear in the list:

- `ai-studio` requires `integrations`;
- `chat` requires `ai-studio`;
- `communications` requires `chat` and, transitively, `ai-studio` and
  `integrations`;
- the other configurable modules only require implicit `core`.

The configurable IDs are `chat`, `ai-studio`, `automation`, `fleet`,
`integrations`, `communications`, `secrets`, `git`, `knowledge`, `artifacts`,
`governance` and `observability`. Do not declare `NASHIRA_MODULES=` with no
value: an explicit empty allowlist is invalid. Unknown modules and missing
dependencies also stop the backend before it accepts traffic, with every
configuration error included in the log.

The selection is immutable for the lifetime of the backend process. After
changing it in `deploy/.env`, recreate the backend container:

```bash
docker compose up -d --force-recreate backend
docker compose logs backend
```

#### Checking what a deployment actually runs

The backend reports its own selection, so nothing has to be inferred from the
`.env` file — which may not be the one the running container was started with:

```bash
curl -s -H "Authorization: Bearer $TOKEN" http://localhost:8080/api/modules
```

The boot log says the same thing:

```text
deployment.modules.resolved mode=Explicit enabled=core, chat, ai-studio, integrations, secrets
```

The following `deployment.modules.snippets`, `deployment.modules.tools`,
`deployment.modules.hosted_services` and `deployment.modules.seeders` lines
report the concrete active and total counts for that selection.

The console reads the same manifest after signing in: pages of a disabled
capability are absent from the sidebar, the command palette and the admin hub,
and opening one by URL shows *Module unavailable for this deployment*.

#### What a disabled capability does

- Its API endpoints stay routed and answer `503` with
  `application/problem+json`, `code: module_disabled` and the module name. The
  action never runs, so a refused call changes nothing.
- Its background workers do not start, its agent tools are not registered, its
  snippet types are not runnable, and its seeders do not write.
- **Its data is left alone.** Nothing is deleted, deactivated or migrated, and
  retention does not prune the tables of a capability that is off.

#### Rolling back

Because nothing is deleted, going back is the same operation as going forward:
restore the previous value (or remove the variable entirely for all-enabled) and
recreate the container.

```bash
# in deploy/.env: restore the previous NASHIRA_MODULES, or comment it out
docker compose up -d --force-recreate backend
```

The capability returns with the rows it had. Its seeders run again over them —
each one is idempotent by key, so the baseline is restored without duplicating
what was kept or resurrecting what an operator deleted.

#### Validating a change before applying it

`docker compose config` resolves the file with your `.env` and prints what the
container would receive, which is the cheapest way to catch a typo before a
restart:

```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml config | grep -A2 NASHIRA_MODULES
```

An invalid selection — an empty value, an unknown module, a missing dependency —
stops the backend at boot with every error listed at once, before it accepts any
traffic. Read them with `docker compose logs backend`.

---

## 3. Getting an account

Read this before you set `ASPNETCORE_ENVIRONMENT` — it is the one step that
differs by environment.

### Development

`DevSeedService` creates `admin` / `admin` on boot, and
`POST /api/auth/bootstrap` is available. This is what `nashira_e2e` logs in with.

```bash
# in deploy/.env
ASPNETCORE_ENVIRONMENT=Development
```

Change the password immediately from `/account`, or
`POST /api/auth/change-password`.

### Production — known limitation

**There is currently no supported mechanism for provisioning the first
administrator on a Production boot against an empty database.** Nothing is
seeded, and `POST /api/auth/bootstrap` returns 404: `AuthController.Bootstrap`
is gated on `IsDevelopment()`.

This is a gap to close before the guide is published as a production install
procedure, not a step to work around. Two documents currently describe a
behaviour the code does not implement, and should be reconciled at the same
time:

- `deploy/.env.example` — "On a server use Production and create the first admin
  via `POST /api/auth/bootstrap`".
- [`nashira_e2e/README.md`](nashira_e2e/README.md) — same instruction for
  pointing the runner at a Production deployment.

Until a first-admin provisioning path exists, treat Production deployment as
blocked on it. Development remains fine for evaluation and local work.

---

## 4. Make the agent answer

Chat is the app's home — `/` redirects straight to `/chat` — but it needs a model
before it can say anything.

1. Sign in and open **`/admin/providers`** (admin).
2. Register a provider — OpenAI, Anthropic, Gemini, DeepSeek, Kimi, Ollama, or a
   compatible custom endpoint — and mark it active.
3. Ask it something in `/chat`.

### What decides whether a request runs

Every agent tool carries three things, enforced by `ToolDispatcher`: a **domain**
(`device`, `git`, `workflow`, …), a **role gate** (`read` / `write` / `execute` /
`dangerous`) and an **autonomy tier**:

| Tier | Behaviour |
|---|---|
| `autonomous` | Runs without asking — reads and diagnostics. |
| `single_confirm` | Asks you once. Most mutations, and bulk deletes (which always confirm and spend one mutation-budget slot). |
| `elevated_confirm` | Asks with a heightened confirmation. |
| `human_only` | Never run by the agent. |

On top of that sit your role, your **per-user tool-domain permissions**
(`/admin/permissions` — an operator can still be denied the `ssh` domain in
chat), the mutation budget for the turn, and any matching **policies**. A tool
that is not in the matrix falls back to the most restrictive classification and
is refused. Promoted workflows are pre-authorised artifacts: the review happened
at promotion, so running one is not re-litigated tool by tool.

---

## 5. Your first workflow

Follow the in-app guide — **`/docs/quick-start`** — which is written against the
running UI. A concrete read-only first run:

1. **Credential** → `/admin/credentials`. How devices are reached. The value
   never leaves the server; responses carry `has_password`, never the password.
2. **Device** → `/devices`. `device_name` and `ip_address` are required; set
   `platform` too, or vendor-command resolution has nothing to key on.
3. **Workflow** → there is **no graph editor**. A workflow arrives either by
   asking the agent in `/chat`, or via `/workflows` → *Import* (a YAML artifact
   from this instance, or a portable **bundle** — including one authored in Flow
   Weaver). Both land it in `draft`; the `id` and `environment` inside an
   imported file are ignored on purpose.

   For a first run, ask the agent for the smallest possible graph over a seeded,
   read-only snippet:

   > Build a draft workflow with a single node using the snippet
   > `baseline-tcp-reachability-per-device`, and nothing else.

   That snippet ships with every install: a TCP probe, `target_mode`
   `per_device`, idempotency tier `idempotent`, 15 s timeout, defaulting to
   `{{ device.ip_address }}` on port 22. It reads and changes nothing.

4. **Read the plan** → open the workflow, **Plan** tab. It shows the materialised
   execution order and whether the whole thing is reversible. That banner is what
   to read before clicking anything. For this workflow it should report the run
   as fully reversible, since nothing is mutated.
5. **Run workflow** → the dialog collects the runtime inputs (from
   `input_schema`, or inferred from the `{{ input.* }}` references when the
   workflow declares none) and the target devices.
6. **Read the run** → `/runs/{id}`. Expect one step per targeted device, each
   returning `reachable` and `latency_ms`; a device that is down gives you a
   failed step with an error code rather than a failed run you cannot explain.
   Step headers load first, and a step's resolved input, output and logs load
   when you open its card.

### Environments and targeting

New workflows start in `draft`. Promotion is `draft → qa → production`, with
four-eyes on the last hop plus any `gate` policies.

A run **never narrows its own target list silently**:

- The Run dialog only offers devices the workflow's environment can reach, and
  keeps any already-selected blocked device visible with a warning rather than
  dropping it from under you.
- If a blocked device reaches the API anyway — from a trigger, a schedule, or a
  stored target list — `WorkflowRunService.ResolveTargetsAsync` throws and names
  the device instead of executing on the remainder. The same applies to a target
  that was deleted or deactivated in the meantime.
- The one place membership is filtered rather than refused is the pool
  **preview**, `GET /api/device-pools/{id}/members?environment=…`, which reports
  allowed and excluded members separately. It is a preview endpoint, not the run
  path.

> `allow_draft` / `allow_qa` / `allow_production` all default to `true` on a new
> device. That is a **lab and development** default. Any device representing real
> infrastructure should have `allow_draft` (and usually `allow_qa`) turned off,
> which is what fences production hardware off from unfinished work.

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
npm run check:password                   # password-policy parity with the API
npm run test:e2e                         # Playwright
```

`nashira_e2e` is deliberately **not** a `dotnet test` project — it is a console
runner that drives the real HTTP API and prints the wire traffic, plus a coverage
diff against the published OpenAPI document.

> It creates, updates and deletes **real data** in whatever environment you point
> it at. Do not run it against production.

```bash
dotnet run --project nashira_e2e                                      # vs :5280
dotnet run --project nashira_e2e -- --base-url http://localhost:8080  # vs Docker
```

`workflow-v1-conformance/` holds the cross-implementation suite for the bundle
format shared with Flow Weaver.

---

## 8. When it does not come up

| Symptom | Cause |
|---|---|
| Backend exits at boot | `JWT_KEY` missing, too short, or a known default. |
| Cannot log in on a fresh Production instance | Known limitation — see §3. |
| POSTs rejected by SvelteKit | `FRONTEND_ORIGIN` does not match the URL the browser uses. |
| MCP OAuth redirects to `localhost:5173` | `Cors__AllowedOrigins__0` / `FRONTEND_ORIGIN` unset, so the backend fell back to the dev origin in `appsettings.json`. |
| Every request shows one IP in the audit trail | `TRUSTED_PROXIES` does not match the compose subnet. |
| Backend cannot connect after a rename | Postgres only creates its database and role on a first run against an empty volume. See *Renaming an existing deployment* in [`README.md`](README.md). |

**Chat returns nothing.** Work down this list rather than retrying:

1. No provider registered, or none marked active — `/admin/providers`.
2. Provider credentials rejected: check the connectivity test on the provider row.
3. No model selected, or a model name the provider no longer serves.
4. The backend cannot reach the provider — egress blocked, proxy, or for Ollama a
   host the container cannot resolve.
5. Quota or rate limit exhausted upstream.
6. Your profile grants no active model, or the tool domain you asked about is
   denied for your user — `/admin/profiles`, `/admin/permissions`.
7. Read the actual error: `docker compose logs -f backend`, or set
   `AI_LOG_PAYLOADS=true` temporarily for the raw stream.

---

## 9. Where to go next

| | |
|---|---|
| [`README.md`](README.md) | Architecture, monorepo layout, conventions, design record. |
| [`deploy/.env.example`](deploy/.env.example) | Every setting, with the reasoning inline. |
| [`docs/mcp-oauth.md`](docs/mcp-oauth.md) | Connecting MCP servers that need OAuth. |
| [`docs/design-system-convergence.md`](docs/design-system-convergence.md) | Frontend design system. |
| [`integrations/`](integrations/) | External-system bundles (spec + skill + register script), attached to an Integration rather than seeded. |
| [`nashira_e2e/README.md`](nashira_e2e/README.md) | Driving the real API and reading the wire traffic. |
| `/docs` in the running app | The full manual — what every part is for, how it works, and the parameters it takes. |
