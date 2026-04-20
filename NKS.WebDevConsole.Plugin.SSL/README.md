# `nks.wdc.ssl` — Local SSL (mkcert) plugin

Issues locally-trusted TLS certs for WDC sites via `mkcert`.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.ssl` |
| `category` | `tools` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem`, `gui` |

## What it does

- Invokes `mkcert.exe` from `~/.wdc/binaries/mkcert/<ver>/`.
- Installs the mkcert root CA into the system trust store on first run
  (`mkcert -install`).
- Generates `<domain>.pem` + `<domain>-key.pem` per site on demand.
- Re-issues on domain rename or alias change.
- Tracks expiry per cert and surfaces a warning badge in the SSL tab
  ≤ 14 days before expiry (upcoming F81).

## Paths

- Binary: `binaries/mkcert/<ver>/mkcert.exe`
- Certs: `~/.wdc/ssl/certs/<domain>.pem` (+ `-key.pem`)

## UI

- `/ssl` global page with per-cert detail (F81 enrichment planned:
  expiry countdown, CA trust status, orphan detection for certs whose
  site no longer exists).
