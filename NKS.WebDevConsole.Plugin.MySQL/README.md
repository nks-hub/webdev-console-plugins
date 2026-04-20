# `nks.wdc.mysql` — MySQL server plugin

Local MySQL server lifecycle. Ships a bundled `mysqld.exe` and manages
its data dir + `my.ini` + service lifecycle.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.mysql` |
| `category` | `database` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem`, `network`, `gui` |

## What it does

- Spawns `mysqld.exe` from `~/.wdc/binaries/mysql/<ver>/bin/`.
- Generates `my.ini` on first run (datadir, port, collation).
- `mysqladmin ping` probes on WDC-managed port for health.
- Bootstraps an empty `~/.wdc/mysql/data/` via `mysqld --initialize-insecure`
  on first boot.
- Port resolution order: `settings.ports.mysql` > mysqladmin prober > 3306.
  (F49b/F49c plumbing.)

## Paths

- Binary: `binaries/mysql/<ver>/bin/mysqld.exe`
- Data: `~/.wdc/mysql/data/`
- Config: `~/.wdc/mysql/data/my.ini` (auto-generated)

## Events

- `service.state.changed`, `service.metrics`, `service.log.line`.

## Security

- Default root has no password (dev only). The daemon stores a
  WDC-specific password in settings and uses it via `MYSQL_PWD` env var
  (never `-p` on command line → no process-list leak).

## UI

- Service card, log viewer, config editor.
- Powers the /databases page (list databases, import/export, drop).
