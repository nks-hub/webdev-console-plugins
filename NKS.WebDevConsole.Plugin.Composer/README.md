# `nks.wdc.composer` — PHP Composer plugin

Stateless plugin (no long-running service) that exposes Composer CLI +
Packagist metadata to the daemon so WDC can manage PHP dependencies per
site from the Composer tab on /sites/:domain/edit.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.composer` |
| `category` | `language` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `network`, `filesystem`, `gui` |

## What it does

- Discovers `composer.phar` under `~/.wdc/binaries/composer/<ver>/` with
  semver-sorted version selection (`ComposerConfig`).
- Auto-discovers a WDC-managed PHP binary for the interpreter
  (falls back to system `php`).
- `InstallAsync` / `RequireAsync` / `RunAsync` with argv dispatch
  split on `.phar` suffix:
  - `.phar` files → invoked via `php.exe composer.phar <argv>`
  - Native binaries → invoked directly
- Uses `CliWrap` argument arrays (no shell interpolation → safe from
  command injection).

## REST endpoints

Mounted by the host daemon at `/api/sites/{domain}/composer/*`:

- `GET /composer/status` — reads `composer.json` directly, returns
  required + installed + latest versions per package.
- `POST /composer/install` — runs `composer install`.
- `POST /composer/require` — body `{ "package": "…" }`, validated
  against a strict package-name regex + path-traversal guard.
- `POST /composer/remove` — body `{ "package": "…" }`.
- `POST /composer/diagnose` — runs `composer diagnose` and parses
  WARNING / ERROR lines.
- `GET /composer/outdated` — `composer outdated --format=json --no-ansi`
  wrapped and JSON-parsed.

## UI

- `SiteComposer.vue` panel in the Composer tab on /sites/:domain/edit.
- Packagist hover popover with package metadata + repo link.

## Known work in progress

- **Per-site PHP version**: `ComposerConfig.PhpPath` is currently
  global (first-found wins). Tracked as F77 — the invoker should use
  the site's `phpVersion` when running `composer.phar`.

## Tests

- `ComposerInvokerTests` — argv / dispatch (7 + 6 edge cases)
- `ComposerConfigTests` — binary discovery (5)
- `ComposerEndpointTests` — regex + parsing (16)
- `ComposerEndpointEdgeCaseTests` — guards + malformed input (12)
