# `nks.wdc.apache` — Apache HTTP Server plugin

Manages the Apache 2.4 `httpd` process lifecycle for NKS WebDev Console
and generates per-site vhost files from Scriban templates.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.apache` |
| `category` | `webserver` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem` (vhosts/logs/binaries), `gui` |

## What it does

- Validates `httpd.conf` syntax before starting.
- Spawns the bundled `httpd.exe` from `~/.wdc/binaries/apache/<ver>/`
  with a WDC-managed config root.
- Generates `<domain>.conf` vhost files under
  `~/.wdc/apache/vhosts/` from `Templates/vhost.scriban`.
- Reloads gracefully (`httpd -k restart`) on site changes.
- Publishes CPU / RAM / request-rate metrics over the plugin event bus.

## Configuration surface

- **Binary**: discovered from `binaries/apache/<semver>/bin/httpd.exe`.
- **Config root**: `~/.wdc/apache/conf/httpd.conf` (regenerated on apply).
- **Vhost root**: `~/.wdc/apache/vhosts/*.conf` (one per WDC site).
- **Log dir**: `~/.wdc/apache/logs/` (access + error logs per site).

## Lifecycle events emitted

- `service.state.changed` (`starting` → `running` → `stopped` / `error`)
- `service.metrics` (cpu / mem / req-per-min when access log parses)
- `service.log.line` (tail of `error.log`)
- `site.validate.*` (before and after each vhost regen)

## REST endpoints

None — Apache is a pure service plugin. Site-level operations go
through the daemon's `/api/sites/*` routes which call into
`ApachePlugin` via DI.

## UI contributions

- Service card on Dashboard
- Log viewer
- Config editor for `httpd.conf`
- Version switcher across installed Apache builds

## Testing

Unit tests live in `tests/NKS.WebDevConsole.Daemon.Tests` (in the main
WDC repo for now — will move here when plugin tests migrate).
