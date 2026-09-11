# Built-in API specs

One OpenAPI 3.1 document per functional area of Nashira's own HTTP API. They are the
catalog the agent browses with `discover_operations` / `operation_detail`, and the
source `execute_operation` resolves a path and method from.

## How they get into the database

`ai_api_specs` is the single source of truth at runtime — `YamlSpecIndex` only ever
reads the table. These files are the **seed**: on boot, `BuiltinSpecSeeder` imports
every `*.yaml` here whose `api` name is not already present, so a fresh deployment
starts with a populated catalog and an existing one is never overwritten. Editing a
spec through `/api/ai/specs` wins permanently; the file is not re-read for that api
again.

Rename the file to seed a new spec — the `api` identifier is the filename without its
extension, and it is what the agent passes as `discover_operations(api="na_workflows")`.

## Executing against them

Discovery works with nothing configured. **Execution** additionally needs a base URL:
set `Ai:SelfBaseUrl` (e.g. `https://nashira.internal`) and the seeder stamps it onto
every built-in spec, together with `auth_type: bearer` and an
`${secret:nashira:self_api_token}` reference the operator fills in via
`/api/secrets`. Leave it unset and the specs are documentation only — `execute_operation`
refuses with a clear "spec has no base_url" rather than guessing a host.

## Conventions

- `operationId` is `<area>_<verb><Subject>`, globally unique — the agent addresses
  operations by that id, and `YamlSpecIndex` indexes on it.
- Paths are written exactly as ASP.NET routes them, including the singular
  `/api/device` and `/api/credential` (they come from `[Route("api/[controller]")]`).
- Request bodies are described down to the fields an agent has to fill in, not
  exhaustively; the controller is still the authority and rejects the rest.
