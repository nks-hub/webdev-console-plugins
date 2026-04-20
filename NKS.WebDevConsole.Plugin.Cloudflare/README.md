# `nks.wdc.cloudflare` — Cloudflare Tunnel plugin

Publishes WDC sites to a public `*.<zone>` URL via Cloudflare Tunnel so
external collaborators can preview local development without exposing
the dev machine's IP.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.cloudflare` |
| `category` | `networking` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `network`, `filesystem`, `gui` |

## What it does

- Spawns `cloudflared.exe` from `~/.wdc/binaries/cloudflared/<ver>/`.
- Generates `~/.wdc/cloudflared/config.yml` from the per-site
  `cloudflare: { tunnelId, zoneId, subdomain }` TOML fields.
- Creates the DNS `CNAME <subdomain>.<zone> -> <tunnelId>.cfargotunnel.com`
  via Cloudflare API when the user enables a tunnel on a site.
- Starts / stops the `cloudflared` process on demand.
- Single process routes ALL tunneled sites — restart affects the whole
  fleet; UI warns before restart.

## Configuration surface

- Tunnel + zone credentials stored in `~/.wdc/cloudflare/config.json`
  (never committed anywhere, read/write guarded).
- Per-site overrides in the site TOML `cloudflare` block.

## REST endpoints

Top-level daemon routes (not mounted under `/api/plugin/`):

- `GET /api/cloudflare/config`
- `POST /api/cloudflare/config`
- `GET /api/cloudflare/dns`, `POST /api/cloudflare/dns`
- `DELETE /api/cloudflare/dns/:id`
- `GET /api/cloudflare/tunnels`, `GET /api/cloudflare/zones`
- `POST /api/cloudflare/verify-token`

## UI

- `/cloudflare` page with three tabs: Setup / Sites / DNS.
- Per-site Cloudflare toggle in SiteEdit → Cloudflare tab.
- Dashboard quick-status: badge showing how many sites are tunneled.

## Simple Mode integration

`SimpleModeCloudflareHelper` provisions defaults from
`cloudflareTunnel: true` hint on site creation so users don't have to
know about zones / tunnels / subdomains at all (F33 wiring).
