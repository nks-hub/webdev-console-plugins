# NKS WebDev Console — Plugins

[![CI](https://github.com/nks-hub/webdev-console-plugins/actions/workflows/ci.yml/badge.svg)](https://github.com/nks-hub/webdev-console-plugins/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/nks-hub/webdev-console-plugins?label=release)](https://github.com/nks-hub/webdev-console-plugins/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)

Official plugin collection for [NKS WebDev Console](https://github.com/nks-hub/webdev-console).
Each plugin ships as an independently versioned `.zip` artifact published to
GitHub Releases and indexed through
[`wdc-catalog-api`](https://github.com/nks-hub/wdc-catalog-api)
so the WDC daemon can install / upgrade plugins at runtime without
re-installing the whole app.

> **Writing a new plugin?** Start with **[`docs/PLUGIN-API.md`](./docs/PLUGIN-API.md)**
> — the full `IWdcPlugin` interface reference, SDK helpers, DI + cross-ALC
> pattern, and a `Hello, plugin!` walkthrough.

---

## What is a WDC plugin?

A WDC plugin is a .NET 9 class library that implements `IWdcPlugin`
(defined in `NKS.WebDevConsole.Plugin.SDK`). The daemon discovers plugin
assemblies at start-up, loads each in its own `AssemblyLoadContext`, and
gives the plugin a chance to register services, lifecycle handlers,
REST endpoints, and UI contributions.

What plugins can do:

- **Manage a long-running service process** (Apache, MySQL, …) with
  validate / start / stop / reload lifecycle hooks + health telemetry.
- **Generate configuration** per site via Scriban templates (vhost files,
  php.ini snippets, cloudflared config, etc.).
- **Expose REST endpoints** under `/api/plugin/<id>/*` mounted by the
  daemon's reverse-proxy. Auth + logging come for free.
- **Contribute UI** — sidebar nav entries, dashboard cards, per-site
  tabs, config editors — via `PluginUiDefinition`.
- **Emit events** (metrics, logs, validation progress) over SSE that the
  Electron frontend subscribes to.

What plugins intentionally CANNOT do:

- Touch the daemon's SQLite directly (must go through `IPluginContext`).
- Share state with other plugins outside the well-defined shared types
  declared in `PluginLoadContext.SharedAssemblies`.
- Run arbitrary code at assembly load — `Initialize` is the earliest
  lifecycle hook and it runs inside the plugin ALC.

---

## Plugins in this repo

| Plugin | Purpose | Status |
|---|---|---|
| `NKS.WebDevConsole.Plugin.Apache`     | Apache 2.4 httpd lifecycle + vhost generator | Stable |
| `NKS.WebDevConsole.Plugin.Caddy`      | Caddy 2 web server                           | Stable |
| `NKS.WebDevConsole.Plugin.Cloudflare` | Cloudflare Tunnel integration                | Stable |
| `NKS.WebDevConsole.Plugin.Composer`   | PHP Composer manager + per-site package UI  | Stable |
| `NKS.WebDevConsole.Plugin.Hosts`      | `/etc/hosts` editor with WDC-marked section | Stable |
| `NKS.WebDevConsole.Plugin.Mailpit`    | Local SMTP → web inbox                       | Stable |
| `NKS.WebDevConsole.Plugin.MariaDB`    | MariaDB server lifecycle                    | Stable |
| `NKS.WebDevConsole.Plugin.MySQL`      | MySQL server lifecycle                      | Stable |
| `NKS.WebDevConsole.Plugin.Nginx`      | Nginx web server                            | Stable |
| `NKS.WebDevConsole.Plugin.Node`       | Node.js process manager                     | Stable |
| `NKS.WebDevConsole.Plugin.PHP`        | PHP-FPM per-version manager                 | Stable |
| `NKS.WebDevConsole.Plugin.Redis`      | Redis server lifecycle                      | Stable |
| `NKS.WebDevConsole.Plugin.SSL`        | mkcert-based local SSL certs                | Stable |

Each plugin directory has its own `README.md` with plugin-specific
manifest metadata, configuration paths, emitted events, and any REST
endpoints it exposes.

---

## Repository layout

```
.
├── Directory.Build.props        # shared .NET build props (nullable, warnings)
├── NKS.WebDevConsole.Plugin.*/  # one directory per plugin (13×)
│   ├── *.csproj                 # .NET 9 class library
│   ├── plugin.json              # manifest (id, name, version, ui, permissions)
│   └── README.md                # plugin-specific docs
├── docs/
│   ├── PLUGIN-API.md            # core IWdcPlugin + SDK reference
│   ├── WRITING-A-PLUGIN.md      # step-by-step walkthrough
│   └── MANIFEST.md              # plugin.json schema
├── .github/
│   ├── workflows/
│   │   ├── ci.yml               # build-on-push
│   │   └── release.yml          # tag v* → per-plugin .zip → GitHub Release
│   └── dependabot.yml           # weekly nuget + github-actions bumps
└── LICENSE                      # MIT
```

---

## Building locally

```cmd
git clone https://github.com/nks-hub/webdev-console-plugins.git
cd webdev-console-plugins
dotnet restore
dotnet build -c Release
```

Each `NKS.WebDevConsole.Plugin.<Name>/bin/Release/net9.0/` then contains a
buildable plugin. The daemon can load plugins directly from those output
folders via `NKS_WDC_PLUGINS_DEV_DIR=…` (developer loopback).

Shipping a new release:

```cmd
git tag v0.2.0
git push origin v0.2.0
```

The `Release` workflow packages every plugin into its own zip and attaches
them as release assets.  `wdc-catalog-api` then picks up the new release
on its next GitHub-releases refresh and exposes the new version at
`/api/v1/plugins/catalog` so every running WDC instance can offer the
update in-app.

---

## Versioning

- Every plugin shares the repo-level tag — e.g. tag `v0.2.0` ships
  `Apache@0.2.0`, `MySQL@0.2.0`, … even when an individual plugin had no
  code changes since the previous tag. This keeps the downloader logic
  trivial and makes rollbacks atomic across the fleet.
- Individual plugins MAY bump their internal `plugin.json` version ahead
  of the next tag to signal breaking changes to the manifest schema
  consumer; the tag still defines the artifact identity.

---

## Contributing

- See [`docs/PLUGIN-API.md`](./docs/PLUGIN-API.md) for the interface,
  [`docs/WRITING-A-PLUGIN.md`](./docs/WRITING-A-PLUGIN.md) for a
  walkthrough.
- Issues + PRs: please use the templates under `.github/`.
- No AI attribution in commit messages (see repo convention).

## Security

- Plugin DLLs ship unsigned today — verify via SHA-256 on the catalog
  entry until codesigning lands.
- Report vulnerabilities privately per [`SECURITY.md`](./SECURITY.md).

## License

MIT — see [LICENSE](./LICENSE).

---

## History

Extracted from the `nks-ws` monorepo on **2026-04-20** via
`git filter-repo --subdirectory-filter src/plugins`, preserving the full
**90-commit** plugin history before scaffold was added.
