# `nks.wdc.mailpit` — Mailpit SMTP sink plugin

Captures outbound mail from dev apps (via SMTP on 1025) and serves a
web inbox on 8025 so you can inspect signup / reset / notification
emails without shipping to a real SMTP provider.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.mailpit` |
| `category` | `mail` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `network`, `gui` |

## What it does

- Spawns `mailpit.exe` from `~/.wdc/binaries/mailpit/<ver>/`.
- Binds SMTP listener on `127.0.0.1:1025`.
- Binds web UI on `127.0.0.1:8025`.
- Quick-action on Dashboard: "Open Mailpit" — opens 8025 in the system
  browser.

## UI

- Service card + "Open web UI" quick action.
