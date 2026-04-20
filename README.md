# NKS WebDev Console — Plugins

Official plugin collection for [NKS WebDev Console](https://github.com/nks-hub/webdev-console).

Each plugin ships as an independently built `.zip` artifact published via
GitHub Releases and indexed through [`wdc-catalog-api`](https://github.com/nks-hub/wdc-catalog-api).
The WDC daemon downloads plugins on demand from the catalog rather than
bundling them into the installer.

## Plugins

| Plugin | Purpose |
|---|---|
| `NKS.WebDevConsole.Plugin.Apache` | Apache 2.4 httpd lifecycle + vhost generator |
| `NKS.WebDevConsole.Plugin.Caddy` | Caddy 2 web server |
| `NKS.WebDevConsole.Plugin.Cloudflare` | Cloudflare Tunnel integration |
| `NKS.WebDevConsole.Plugin.Composer` | PHP Composer manager + per-site package UI |
| `NKS.WebDevConsole.Plugin.Hosts` | `/etc/hosts` editor with WDC-marked section |
| `NKS.WebDevConsole.Plugin.Mailpit` | Local SMTP → web inbox |
| `NKS.WebDevConsole.Plugin.MariaDB` | MariaDB server lifecycle |
| `NKS.WebDevConsole.Plugin.MySQL` | MySQL server lifecycle |
| `NKS.WebDevConsole.Plugin.Nginx` | Nginx web server |
| `NKS.WebDevConsole.Plugin.Node` | Node.js process manager |
| `NKS.WebDevConsole.Plugin.PHP` | PHP-FPM per-version manager |
| `NKS.WebDevConsole.Plugin.Redis` | Redis server lifecycle |
| `NKS.WebDevConsole.Plugin.SSL` | mkcert-based local SSL certs |

## Development

- Each plugin is a .NET 9 class library implementing `IWdcPlugin` from
  `NKS.WebDevConsole.Plugin.SDK` (shipped with the daemon at runtime).
- Shared build props live in `Directory.Build.props`.
- CI builds every plugin on every push (`.github/workflows/ci.yml`).
- Tag push `v*` triggers `release.yml` which publishes `<plugin-id>-<version>.zip`
  per plugin to GitHub Releases and registers the release with `wdc-catalog-api`.

## History

This repository was extracted from the `nks-ws` monorepo on 2026-04-20 using
`git filter-repo --subdirectory-filter src/plugins`, preserving the original
90-commit plugin history.

## License

MIT — see [LICENSE](./LICENSE).
