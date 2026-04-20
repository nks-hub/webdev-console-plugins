# `plugin.json` manifest reference

Every plugin ships a `plugin.json` next to its DLL. The daemon reads it
at discovery time without instantiating the plugin assembly, so disabled
plugins don't load into memory.

## Full schema

```json
{
  "id": "nks.wdc.apache",
  "name": "Apache HTTP Server",
  "version": "0.1.62",
  "description": "Apache 2.4 httpd lifecycle + per-site vhost generator",
  "author": "NKS Hub",
  "homepage": "https://github.com/nks-hub/webdev-console-plugins/tree/main/NKS.WebDevConsole.Plugin.Apache",
  "minWdcVersion": "0.1.60",
  "maxWdcVersion": null,

  "entry": "NKS.WebDevConsole.Plugin.Apache.ApachePlugin, NKS.WebDevConsole.Plugin.Apache",

  "permissions": {
    "network": true,
    "process": true,
    "gui": true,
    "filesystem": ["vhosts", "logs", "binaries"]
  },

  "dependencies": {
    "hard": [],
    "anyOf": [
      { "plugin": "nks.wdc.php", "reason": "PHP handler for vhosts" }
    ]
  },

  "ui": {
    "category": "webserver",
    "icon": "mdi:apache",
    "panels": [
      { "type": "service-card", "serviceId": "apache" },
      { "type": "log-viewer", "serviceId": "apache" },
      { "type": "config-editor", "serviceId": "apache" }
    ]
  }
}
```

## Field reference

| Field | Type | Required | Purpose |
|---|---|---|---|
| `id` | string | yes | Reverse-domain plugin id, stable across versions |
| `name` | string | yes | Display name shown in /plugins UI |
| `version` | semver | yes | Plugin version, bumped on behavior changes |
| `description` | string | no | Short one-liner shown in marketplace |
| `author` | string | no | Author / org name |
| `homepage` | URL | no | Plugin homepage / source |
| `minWdcVersion` | semver | recommended | Minimum WDC daemon version this plugin supports |
| `maxWdcVersion` | semver | no | Upper bound (rarely needed) |
| `entry` | string | yes | `FullTypeName, AssemblyName` for the `IWdcPlugin` impl |
| `permissions.network` | bool | no | Makes outbound HTTP calls |
| `permissions.process` | bool | no | Spawns child processes |
| `permissions.gui` | bool | no | Contributes UI |
| `permissions.filesystem` | string[] | no | Named filesystem areas the plugin touches |
| `dependencies.hard` | array | no | Plugins that MUST be enabled first |
| `dependencies.anyOf` | array | no | At least one of these MUST be enabled |
| `ui.category` | string | no | Sidebar category (webserver / database / language / cache / mail / tools / networking / other) |
| `ui.icon` | string | no | Icon identifier (Material Design or SVG path) |
| `ui.panels` | array | no | Panel contributions rendered by the frontend |

## Panel types

| `panels[].type` | Purpose | Required props |
|---|---|---|
| `service-card` | Dashboard tile for a long-running service | `serviceId` |
| `log-viewer` | Tail + filter service logs | `serviceId` |
| `config-editor` | Monaco editor for config files | `serviceId` |
| `version-switcher` | Version picker (multi-version runtimes) | `serviceId` |
| `metrics-chart` | CPU/RAM/request-rate chart | `serviceId` |
| `site-edit-tab` | Tab contribution on /sites/:domain/edit | `id`, `label`, `icon` |
| `sidebar-nav` | Explicit sidebar link | `route`, `label`, `icon` |
| `settings-section` | Section in /settings | `id`, `label` |
| `custom` | Host-rendered custom panel | `component`, `props` |

## Validation

The daemon validates `plugin.json` on load and emits a clear error to
`~/.wdc/logs/plugin-loader.log` when a field is missing or of the wrong
type. Invalid plugins are skipped — the rest of the app keeps running.
