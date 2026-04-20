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

### Resolving `NKS.WebDevConsole.Plugin.SDK`

Plugin csprojs reference the SDK via `PackageReference` inherited from
`Directory.Build.props`. `dotnet restore` needs a source — two options:

1. **Local release asset** (recommended, no auth): download the latest
   `NKS.WebDevConsole.Plugin.SDK.<ver>.nupkg` from
   <https://github.com/nks-hub/webdev-console/releases>, drop into
   `./.local-nuget/`, add the folder to `nuget.config`:
   ```xml
   <add key="local-sdk" value=".local-nuget" />
   ```
   The CI workflow does the same via `gh release download` — see
   `.github/workflows/ci.yml`.

2. **GitHub Packages feed** (already in `nuget.config`). Needs a classic
   PAT with `read:packages` scope. Export before `dotnet restore`:
   ```cmd
   set NUGET_GH_USERNAME=your-github-login
   set NUGET_GH_TOKEN=ghp_...
   ```
   Cross-repo access may require the repo owner to link the package at
   <https://github.com/orgs/nks-hub/packages/>.

### Loading a local plugin into a running daemon

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
