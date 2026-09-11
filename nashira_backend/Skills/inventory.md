# Skill: inventory — devices, pools, sources, and reaching them

Inventory manages three families: **devices**, **device pools** and **inventory sources**.
Devices and sources may reference credentials already configured elsewhere.

Tools: `query_devices`, `create_device`, `update_device`, `delete_device`,
`bulk_delete_devices`, `device_ping`, `device_connect`, `list_inventory_sources`,
`create_inventory_source`, `update_inventory_source`, `delete_inventory_source`,
`sync_netbox_inventory`. Spec: `na_inventory` (note the **singular** `/api/device`;
that is the controller name, not a typo).

## Devices

`create_device` needs an `ip_address`; `device_name` defaults to the IP. Optional:
`platform`, `vendor`, `os_version`, `site`, `role`. `update_device` and `delete_device`
resolve by the **current** `device_name`, so rename and update in that order, not both at
once.

`query_devices` searches by keyword (name / ip / vendor / platform), `site`, `role` or
`missing_ip` (devices with no IP address). It never returns credentials. Use it before any
device operation — acting on a name the user typed, without confirming it resolves, is how
you ping the wrong box.

For deleting **more than a handful** of devices, use `bulk_delete_devices` — one call, one
confirmation, one mutation-budget slot — never a loop of `delete_device` (that burns the
turn's budget 20 rows at a time). Select by a `device_names` list, by filters
(`missing_ip`, `site`, `role`, `vendor`, `platform`), or both — filters restrict the list.
Always preview first with `query_devices`, show the user the exact names and the count, and
pass that count as `expected_count` so the call refuses if the inventory changed in
between.

### The environment trio

Every device carries `allow_draft` (default true), `allow_qa` (default **false** — qa
targeting is opt-in) and `allow_production` (default true). A workflow run refuses **by
name** on a device that does not allow the run's environment; it never silently skips it.

When a run reports such a refusal, the fix is a deliberate one: either the device opts
into that environment or the run targets a different set. Say which you are proposing.

### Host keys

`expected_ssh_host_key_fingerprint` is `SHA256:<base64>`. Once set, a mismatch aborts the
connection. Left null, the first successful connect pins whatever it saw. A sudden
mismatch after a device replacement is expected; a mismatch nobody can explain is not
something to work around by clearing the field.

## Reaching a device

- `device_ping` — TCP reachability on port 22. Accepts a device name (resolved through
  inventory) or a bare IP, so you can probe something not yet registered. Returns
  `reachable` + `latency_ms`.
- `device_connect` — runs CLI commands over SSH via netmiko. The device must exist in
  inventory **with a credential assigned**. Prefer `show` / `display`; commands are
  classified read / mutation / destructive and destructive ones are blocked unless an
  admin opted in (see the snippets skill for the full classification).

Ping first when a connect fails. "Unreachable" and "authenticated but rejected the
command" are different problems and the user cannot tell them apart from your summary
unless you separate them.

## Pools

A **device pool** is a named group: `static_members` (device ids), `filter_rules` (a
dynamic query), or both. Pools carry their own `allow_draft` / `allow_qa` /
`allow_production`, and a `deny` policy can be written against `device_pool`.

There is no dedicated tool — the pools live in the `na_inventory` spec, reached through
`execute_operation`:

| Operation | What it does |
|---|---|
| `inventory_listDevicePools` | The catalogue, each with its resolved `member_count`. |
| `inventory_getDevicePool` | One pool. |
| `inventory_devicePoolMembers` | **What it actually contains right now.** |
| `inventory_createDevicePool` / `inventory_updateDevicePool` / `inventory_deleteDevicePool` | Operator-level authoring. |

`filter_rules` is a JSON object and its keys are limited to `site`, `role`, `vendor`,
`platform`, `status`. An unknown key is rejected rather than ignored — a rule nobody
implements would match everything and read exactly like one that works.

**Resolve the members before telling anyone what a run will touch.** A rule-defined
pool's contents are a query result, not a list someone wrote down. Pass
`environment=draft|qa|production` to `inventory_devicePoolMembers` and you get the
answer that matters: `pool_allows_environment` (false means the run is refused before a
single device is considered) plus `excluded`, the devices dropped by their own
environment trio. Without the parameter you get the raw membership, which is not what a
run would touch.

`static_members` is replaced wholesale on update, like a workflow's node array — read
the pool first and send the full list, or the members you did not mention disappear.

## Importing from NetBox

1. `create_inventory_source` — `base_url` must be an absolute http(s) URL, and
   `token_secret_ref` names a **stored secret** (or is a full `${secret:...}` reference),
   never a raw token — a raw token is rejected.
   Optional `site_filter` and `allow_private_network`.
2. `sync_netbox_inventory` — pulls and upserts, matched by name. Run it with `dry_run`
   first and show the counts before doing it for real.

Imported devices keep their `external_id`, so a re-sync updates instead of duplicating.
A source that has never synced has a null `last_synced_at` — that is the first thing to
check when someone says "the devices aren't there".
