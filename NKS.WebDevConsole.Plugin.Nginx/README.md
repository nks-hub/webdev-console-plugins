# `nks.wdc.nginx` — Nginx plugin

Manages Nginx as an alternative to Apache for per-site HTTP(S) serving.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.nginx` |
| `category` | `webserver` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem`, `gui` |

## What it does

- Spawns `nginx.exe` from `~/.wdc/binaries/nginx/<ver>/`.
- Generates `conf/sites-enabled/<domain>.conf` from Scriban templates.
- Graceful reload via `nginx -s reload`.
- Validates config (`nginx -t`) before apply.
- Mutually exclusive with Apache at runtime on the same port — the
  daemon warns if both are enabled.

## Paths

- Binary: `binaries/nginx/<ver>/nginx.exe`
- Config root: `~/.wdc/nginx/conf/nginx.conf`
- Sites: `~/.wdc/nginx/conf/sites-enabled/*.conf`
- Logs: `~/.wdc/nginx/logs/`

## Events

- `service.state.changed`, `service.metrics`, `service.log.line`.

## UI

- Service card, log viewer, config editor, version switcher.
