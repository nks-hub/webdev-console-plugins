# `nks.wdc.hosts` — `/etc/hosts` editor plugin

Maintains a WDC-marked block inside the system hosts file so WDC-managed
domains resolve to `127.0.0.1`.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.hosts` |
| `category` | `tools` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `filesystem`, `gui` |

## What it does

- Reads `C:\Windows\System32\drivers\etc\hosts` (or `/etc/hosts`).
- Parses entries, preserving non-WDC lines verbatim.
- Rewrites a marker-delimited section
  (`# BEGIN NKS WebDev Console …` / `# END NKS WebDev Console`)
  containing one line per managed site.
- Returns 403 when run without elevated privileges (Windows).
- Backs up the original to timestamped `.bak` in `~/.wdc/backups/hosts/`
  before every write.

## REST endpoints

Mounted by host daemon at `/api/hosts/*`:

- `GET /hosts` — return parsed entries with origin tag (`wdc` | `external`).
- `POST /hosts/apply` — rewrite the WDC block.
- `POST /hosts/backup` — force a backup now.
- `POST /hosts/restore` — upload a `.bak` to overwrite the live file
  (size-clamped to 10 MiB to prevent memory exhaustion — F58 fix).

## UI

- `/tools/hosts` page with editable table and staging area so edits
  don't hit disk until the user confirms.

## Known issues

- MAMP hosts entries with tab separators + multi-hostname lines may
  render as garbled cells — parser hardening tracked as F82.
