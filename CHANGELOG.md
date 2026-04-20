# Changelog

All notable changes to this repo since it was extracted from the
nks-ws monorepo.

The format is loosely [Keep a Changelog](https://keepachangelog.com/),
versioned per repo-level Git tag (plugin artifacts share that version).

## [Unreleased]

### Added
- Initial public extraction from `nks-hub/webdev-console` monorepo via
  `git filter-repo --subdirectory-filter src/plugins`, preserving
  90 commits of per-plugin history.
- Comprehensive README per plugin (manifest, config paths, endpoints,
  events, UI contributions).
- `docs/PLUGIN-API.md` — IWdcPlugin reference.
- `docs/MANIFEST.md` — `plugin.json` full schema.
- `docs/WRITING-A-PLUGIN.md` — walkthrough building `HelloPlugin`.
- `SECURITY.md`, `CONTRIBUTING.md`.
- GitHub Actions CI: structural smoke check (csproj XML valid,
  plugin.json present & parses, required docs present).
- GitHub Actions Release workflow: tag `v*` → per-plugin `.zip`
  attached to a GitHub release via `softprops/action-gh-release`.
- Dependabot (weekly nuget + github-actions bumps).
- Pull request + issue templates, CODEOWNERS.

### Known issues
- Plugin `.csproj` files still `ProjectReference` the Plugin.SDK via
  a relative path inherited from the monorepo layout — standalone
  `dotnet build` fails until SDK is published to NuGet (tracked as
  F100 in the nks-ws backlog).
- Tests currently live inside `nks-ws/tests/NKS.WebDevConsole.Daemon.Tests`;
  migration into this repo is a future milestone.
