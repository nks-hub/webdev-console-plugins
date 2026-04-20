# Contributing to WDC Plugins

Thanks for helping! A few ground rules so reviews go fast.

## Before you start

- Read [`docs/PLUGIN-API.md`](./docs/PLUGIN-API.md) for the interface.
- Read [`docs/MANIFEST.md`](./docs/MANIFEST.md) for the `plugin.json`
  schema.
- For a brand-new plugin, walk through
  [`docs/WRITING-A-PLUGIN.md`](./docs/WRITING-A-PLUGIN.md).

## Local setup

```cmd
git clone https://github.com/nks-hub/webdev-console-plugins.git
cd webdev-console-plugins
dotnet restore
dotnet build -c Release
```

To load a plugin from its build output into a running daemon without
publishing, point the daemon at the bin dir via:

```cmd
set NKS_WDC_PLUGINS_DEV_DIR=C:\work\sources\webdev-console-plugins\NKS.WebDevConsole.Plugin.MyPlugin\bin\Release\net9.0
```

## Commit conventions

- **Conventional Commits**: `feat(apache): …`, `fix(mysql): …`,
  `chore(ci): …`. One-line subjects under ~100 chars; use the body for
  context.
- **No AI / bot attribution** in commit messages (`Co-Authored-By:`
  trailers, `Generated with …`, etc.) — repo convention.
- Prefer one change per commit. Atomic history makes `git bisect`
  actually useful.

## Pull requests

- Open against `main`.
- CI must be green. `dotnet build -c Release` is the gate.
- Include or update tests for any plugin logic you add.
- Update the plugin's `README.md` + bump `plugin.json.version` +
  csproj `<Version>` when you change behaviour.
- If you add a new plugin, also add it to the table in the root
  [`README.md`](./README.md).

## Review

- Aim for < 48h first review.
- Squash + merge default.
- Docs-only PRs can be self-merged after one approving review.

## Releasing

Tags are cut by maintainers. Tag `v0.x.0` on `main` runs the Release
workflow, which builds every plugin and attaches one zip per plugin to
the GitHub release. `wdc-catalog-api` picks it up automatically.
