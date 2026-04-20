# `nks.wdc.caddy` — Caddy 2 web server plugin

Caddy 2 as an HTTP(S) alternative to Apache / Nginx with automatic
HTTPS.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.caddy` |
| `category` | `webserver` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem`, `network`, `gui` |

## What it does

- Runs `caddy run --config ~/.wdc/caddy/Caddyfile` from
  `~/.wdc/binaries/caddy/<ver>/caddy.exe`.
- Generates per-site blocks in `Caddyfile` from Scriban templates.
- Graceful reload via `caddy reload`.
- Automatic HTTPS optional (off by default — WDC has a separate SSL
  plugin via mkcert for local trust).

## Paths

- Binary: `binaries/caddy/<ver>/caddy.exe`
- Config: `~/.wdc/caddy/Caddyfile`
- Logs: captured to `~/.wdc/caddy/access.log` / `error.log`

## UI

- Service card, log viewer, config editor.

## Notes

Caddy's native HTTPS-by-default is suppressed in the WDC-generated
Caddyfile because mkcert already provides locally trusted certs.
