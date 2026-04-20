# `nks.wdc.node` — Node.js process manager

Runs Node (+ package manager) based sites as long-running processes
alongside PHP sites.

## Manifest

| Field | Value |
|---|---|
| `id` | `nks.wdc.node` |
| `category` | `language` |
| `minWdcVersion` | 0.1.60 |
| `permissions` | `process`, `filesystem`, `network`, `gui` |

## What it does

- Spawns `node.exe` per site using the site's declared `nodeVersion`.
- Supports `package.json` script entry points + raw `node index.js`.
- `npm install` / `pnpm install` / `yarn install` passthrough under
  per-site working dir.
- Watches package.json for script additions and exposes them in the UI.

## Paths

- Binaries: `~/.wdc/binaries/node/<semver>/`
- Per-site stdout/stderr: streamed via event bus + tailed to
  `~/.wdc/node/<site>/<date>.log`.

## Security

- Every argv that crosses the endpoint boundary goes through
  `NodeModule` command-injection validator (20+ test cases). Never
  constructs shell strings.
