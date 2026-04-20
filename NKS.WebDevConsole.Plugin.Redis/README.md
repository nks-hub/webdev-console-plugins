# `nks.wdc.redis` — Redis cache plugin

Local Redis server for session storage, cache, and queue workloads in
dev environments.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.redis` |
| `category` | `cache` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem`, `network`, `gui` |

## What it does

- Spawns `redis-server.exe` from `~/.wdc/binaries/redis/<ver>/`.
- Ships a stock `redis.conf` + `sentinel.conf` in the binaries bundle
  (fix applied upstream in binaries-repo PR #32).
- Exposes the default port 6379 on localhost.

## Paths

- Binary: `binaries/redis/<ver>/redis-server.exe`
- Config: `binaries/redis/<ver>/redis.conf`
- Data: working dir at `~/.wdc/redis/data/`

## UI

- Service card, log viewer, config editor.
