# `nks.wdc.mariadb` — MariaDB server plugin

Drop-in replacement for MySQL when the user prefers MariaDB (or when
MySQL licensing is a concern).

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.mariadb` |
| `category` | `database` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem`, `network`, `gui` |

## What it does

- Mirrors the MySQL plugin's lifecycle via `IMariaDBProcessRunner`.
- Shares the mysqladmin ping helper for port discovery.
- Points at MariaDB bundles under `~/.wdc/binaries/mariadb/<ver>/`.
- MUST NOT be enabled simultaneously with `nks.wdc.mysql` — the daemon
  warns and disables one.

## Events + UI

Same contract as `nks.wdc.mysql` — service card, logs, config editor.

## Tests

5 lifecycle tests in `NKS.WebDevConsole.Daemon.Tests` (commit 6b9d9bf).
