# Vendor command seed catalogue

Each `<platform>.yaml` here seeds the `vendor_commands` table at boot, via
`VendorCommandSeeder`. That table is what
`GET /api/vendor-commands/resolve` (`snippets_resolveVendorCommand` in the
`na_snippets` spec) reads, so an empty directory means every resolve call
404s and the agent falls back to hardcoding one vendor's syntax into a
workflow — which is the exact failure the catalogue exists to prevent.

## What a row means

The key is `(intent, platform)`:

- **`intent`** is a stable, vendor-neutral name — `show_interfaces`,
  `show_bgp_summary`. It is what a workflow node asks for.
- **`platform`** is the Netmiko `device_type` already stored on
  `Device.Platform`, so resolving is a direct join rather than a mapping
  table nobody maintains. Canonical values live in
  `deploy/python/nashira_ssh_parsers.py` (`VENDOR_FAMILY`).

One workflow that asks for `show_interfaces` therefore runs
`show ip interface brief` on a Cisco box, `show interfaces terse` on a
Juniper and `show interface brief` on an SR Linux, without a branch per
vendor.

## Schema

```yaml
platform: cisco_ios          # Netmiko device_type; matches Device.Platform
description: Free text describing the catalogue.
commands:
  - intent: show_version
    command: show version
    description: Software version, model, uptime and hardware summary.
    read_only: true          # default true; false marks a command that changes state
    parser_template: null    # optional TextFSM template name, only when the
                             # runner's built-in templates do not cover it
```

`read_only: false` is not a substitute for the SSH policy. `SshCommandPolicy`
classifies every command it is handed independently of this catalogue, and a
`save_config` entry is still a mutation there. The flag exists so a catalogue
entry that mutates cannot be treated as a `show` by the idempotency tier.

## Adding a platform

1. Drop a `<device_type>.yaml` here using the schema above.
2. Confirm the `device_type` is in `VENDOR_FAMILY` in
   `deploy/python/nashira_ssh_parsers.py`, or the SSH runner will connect
   as `generic_ssh` and parse nothing. That file is also where the vendor
   family lives — the catalogue deliberately does not repeat it, because a
   second copy would only drift from the one the runner reads.
3. If a command needs structured output the runner cannot parse, add the
   TextFSM template under `deploy/python/ntc_templates_extra/` and name it
   in `parser_template`.

Seeding is idempotent per `(intent, platform)` and includes soft-deleted
rows: an operator who removed an entry decided something, and a redeploy
does not quietly bring it back.

## Platforms not shipped here

`aruba_aoscx`, `aruba_osswitch`, `hp_comware`, `mikrotik_routeros`,
`risecom_ros` and `generic` are supported by the SSH runner but ship no
seed file — their command sets have not been verified against real
hardware. Resolving an intent on those platforms 404s by design rather
than returning a guess; add a file here once the syntax is confirmed.
