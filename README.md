[![CI](https://github.com/nks-hub/webdev-console-plugins/actions/workflows/ci.yml/badge.svg)](https://github.com/nks-hub/webdev-console-plugins/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/nks-hub/webdev-console-plugins?label=release)](https://github.com/nks-hub/webdev-console-plugins/releases)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
[![Plugins](https://img.shields.io/badge/plugins-13-blue)](#plugins)

# NKS WebDev Console — Plugins

Official plugin collection for [NKS WebDev Console](https://github.com/nks-hub/webdev-console). Each plugin ships as an independently versioned `.zip` artifact published to GitHub Releases and indexed through [`wdc-catalog-api`](https://github.com/nks-hub/wdc-catalog-api) so the WDC daemon can install and upgrade plugins at runtime without re-installing the whole app.

> **Writing a new plugin?** Start with **[`docs/PLUGIN-API.md`](./docs/PLUGIN-API.md)** — the full `IWdcPlugin` interface reference, SDK helpers, DI + cross-ALC pattern, and a `Hello, plugin!` walkthrough.

## Features

- ✅ **13 production plugins** — Apache, Nginx, Caddy, PHP, MySQL, MariaDB, Redis, Mailpit, SSL (mkcert), Composer, Cloudflare, Node, Hosts
- ✅ **Isolated load contexts** — each plugin runs in its own `AssemblyLoadContext`, preventing DLL version conflicts across plugins
- ✅ **REST endpoints per plugin** — every plugin can expose `/api/plugin/<id>/*` with auth + logging provided by the daemon
- ✅ **UI contributions** — plugins register sidebar nav entries, dashboard cards, per-site tabs, and config editors via `PluginUiDefinition`
- ✅ **Lifecycle hooks** — validate / start / stop / reload for long-running services with built-in health telemetry
- ✅ **Scriban config generation** — type-safe vhost / php.ini / fpm.conf templating per site
- ✅ **SSE event stream** — plugins emit metrics, logs and validation progress consumed by the Electron UI
- ✅ **Dynamic discovery** — the daemon loads plugin DLLs from its runtime directory at startup
- ✅ **Per-plugin versioning** — shared repo tag (`v0.2.0`) ships atomic plugin bundle; individual `plugin.json` versions allow manifest-schema bumps

## Requirements

- .NET 9 SDK (for building)
- NKS WebDev Console daemon ≥ 0.2.0
- GitHub Packages access for `NKS.WebDevConsole.Plugin.SDK` NuGet (published by the parent repo on every `v*` tag)

## Installation

Plugins are installed through the NKS WebDev Console daemon at runtime. No manual installation is required.

### Via the WDC Electron UI

**Settings → Plugins → Install from Catalog** — pick the plugin and version.

### Via the WDC CLI

```bash
wdc plugins list                          # available + installed
wdc plugins install nks.wdc.apache        # install latest
wdc plugins install nks.wdc.apache@0.2.0  # pin version
wdc plugins enable nks.wdc.apache
```

### Via the REST API

```bash
curl -H "Authorization: Bearer $TOKEN" \
     -X POST http://localhost:$PORT/api/plugins/install \
     -H 'Content-Type: application/json' \
     -d '{"id": "nks.wdc.apache", "version": "0.2.0"}'
```

## Plugins

| Plugin | Purpose | Status |
|---|---|---|
| `NKS.WebDevConsole.Plugin.Apache`     | Apache 2.4 httpd lifecycle + vhost generator | Stable |
| `NKS.WebDevConsole.Plugin.Caddy`      | Caddy 2 web server                           | Stable |
| `NKS.WebDevConsole.Plugin.Cloudflare` | Cloudflare Tunnel integration                | Stable |
| `NKS.WebDevConsole.Plugin.Composer`   | PHP Composer manager + per-site package UI   | Stable |
| `NKS.WebDevConsole.Plugin.Hosts`      | `/etc/hosts` editor with WDC-marked section  | Stable |
| `NKS.WebDevConsole.Plugin.Mailpit`    | Local SMTP → web inbox                       | Stable |
| `NKS.WebDevConsole.Plugin.MariaDB`    | MariaDB server lifecycle                     | Stable |
| `NKS.WebDevConsole.Plugin.MySQL`      | MySQL server lifecycle                       | Stable |
| `NKS.WebDevConsole.Plugin.Nginx`      | Nginx web server                             | Stable |
| `NKS.WebDevConsole.Plugin.Node`       | Node.js process manager                      | Stable |
| `NKS.WebDevConsole.Plugin.PHP`        | PHP-FPM per-version manager                  | Stable |
| `NKS.WebDevConsole.Plugin.Redis`      | Redis server lifecycle                       | Stable |
| `NKS.WebDevConsole.Plugin.SSL`        | mkcert-based local SSL certs                 | Stable |

Each plugin directory has its own `README.md` with plugin-specific manifest metadata, configuration paths, emitted events, and REST endpoints.

## Plugin Capabilities

A WDC plugin is a .NET 9 class library that implements `IWdcPlugin` (defined in `NKS.WebDevConsole.Plugin.SDK`). The daemon discovers plugin assemblies at start-up, loads each in its own `AssemblyLoadContext`, and gives the plugin a chance to register services, lifecycle handlers, REST endpoints, and UI contributions.

**What plugins can do:**

- **Manage a long-running service process** (Apache, MySQL, …) with validate / start / stop / reload lifecycle hooks + health telemetry
- **Generate configuration** per site via Scriban templates (vhost files, php.ini snippets, cloudflared config, etc.)
- **Expose REST endpoints** under `/api/plugin/<id>/*` mounted by the daemon's reverse-proxy. Auth + logging come for free.
- **Contribute UI** — sidebar nav entries, dashboard cards, per-site tabs, config editors — via `PluginUiDefinition`
- **Emit events** (metrics, logs, validation progress) over SSE that the Electron frontend subscribes to

**What plugins intentionally CANNOT do:**

- Touch the daemon's SQLite directly (must go through `IPluginContext`)
- Share state with other plugins outside the well-defined shared types declared in `PluginLoadContext.SharedAssemblies`
- Run arbitrary code at assembly load — `Initialize` is the earliest lifecycle hook and it runs inside the plugin ALC

## Development

### Building locally

```bash
git clone https://github.com/nks-hub/webdev-console-plugins.git
cd webdev-console-plugins
dotnet restore
dotnet build -c Release
```

Each `NKS.WebDevConsole.Plugin.<Name>/bin/Release/net9.0/` directory then contains a buildable plugin. Point the daemon at the build output with `NKS_WDC_PLUGINS_DEV_DIR=/path/to/webdev-console-plugins` for developer loopback.

### Repository layout

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
└── .github/
    ├── workflows/
    │   ├── ci.yml               # build-on-push
    │   └── release.yml          # tag v* → per-plugin .zip → GitHub Release
    └── dependabot.yml           # weekly nuget + github-actions bumps
```

### Shipping a release

```bash
git tag v0.2.0
git push origin v0.2.0
```

The `Release` workflow packages every plugin into its own `.zip` and attaches them as release assets. `wdc-catalog-api` picks up the new release on its next GitHub-releases refresh and exposes the new version at `/api/v1/plugins/catalog` so running WDC instances can offer the update in-app.

### Versioning

- Every plugin shares the repo-level tag — e.g. tag `v0.2.0` ships `Apache@0.2.0`, `MySQL@0.2.0`, … even when an individual plugin had no code changes. This keeps the downloader logic trivial and makes rollbacks atomic across the fleet.
- Individual plugins MAY bump their internal `plugin.json` version ahead of the next tag to signal breaking changes to the manifest schema; the tag still defines the artifact identity.

## Contributing

Contributions are welcome! For major changes please open an issue first.

1. Fork the repository
2. Create your feature branch (`git checkout -b feat/amazing-feature`)
3. Keep `dotnet build` green and add/update plugin `README.md` if behavior changes
4. Commit your changes — one-line conventional commit messages, no AI attribution
5. Open a Pull Request

See [`docs/PLUGIN-API.md`](./docs/PLUGIN-API.md) for the plugin interface and [`docs/WRITING-A-PLUGIN.md`](./docs/WRITING-A-PLUGIN.md) for a step-by-step walkthrough.

## Security

Plugin DLLs ship unsigned today — verify via SHA-256 on the catalog entry until codesigning lands. Report vulnerabilities privately per [`SECURITY.md`](./SECURITY.md).

## Support

- 📧 **Email:** dev@nks-hub.cz
- 🐛 **Bug reports:** [GitHub Issues](https://github.com/nks-hub/webdev-console-plugins/issues)
- 🔗 **Main project:** [nks-hub/webdev-console](https://github.com/nks-hub/webdev-console)

## License

MIT License — see [LICENSE](LICENSE) for details.

---

<p align="center">
  Made with ❤️ by <a href="https://github.com/nks-hub">NKS Hub</a>
</p>
