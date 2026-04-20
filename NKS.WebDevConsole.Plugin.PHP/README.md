# `nks.wdc.php` — PHP runtime plugin

Per-version PHP-FPM manager. Sites declare `phpVersion` and WDC routes
them to the matching `php-cgi.exe` / `php-fpm` pool.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.php` |
| `category` | `language` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem`, `gui` |

## What it does

- Enumerates installed PHP versions from `~/.wdc/binaries/php/*/php.exe`.
- Spawns one FastCGI listener per version on unique ports.
- Manages `php.ini` per version with the `PhpIniManager` helper:
  enable/disable extensions, tune `memory_limit` etc.
- Detects site `phpVersion` from `composer.json` / `.nvmrc`-style hints.
- Exposes `/api/php/versions`, `/api/php/extensions`, and `php.ini`
  editing via the daemon's top-level routes.

## Paths

- Binaries: `~/.wdc/binaries/php/<semver>/`
- Runtime pools: `~/.wdc/php/<semver>/pool.d/*.conf`
- `php.ini`: `~/.wdc/binaries/php/<semver>/php.ini`

## Events

- `php.version.added`, `php.version.removed`, `php.extension.toggled`.

## UI

- `/php` page with version cards + extension toggles.
- Version switcher widget per site.
- Config editor for `php.ini`.

## Tests

- `PhpIniManagerTests` (16 cases), `DetectPhpVersionTests` (6 cases)
  in the WDC daemon test suite.
