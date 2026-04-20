# WDC Plugin API — reference

> Audience: plugin authors writing new `NKS.WebDevConsole.Plugin.*` projects.
> For the concrete WRITING walkthrough see [`WRITING-A-PLUGIN.md`](./WRITING-A-PLUGIN.md).
> For the plugin.json manifest schema see [`MANIFEST.md`](./MANIFEST.md).

---

## 1 · Plugin lifecycle

```
┌──────────────┐  Assembly.LoadFrom()  ┌──────────┐
│ PluginLoader │──────────────────────▶│  Plugin  │
└──────────────┘                        │   ALC    │
        │                               └────┬─────┘
        │  Activator.CreateInstance(IWdcPlugin)
        ▼
┌──────────────┐   Initialize(services, ctx)
│ IWdcPlugin   │◀──── DI registration + endpoint mapping
│              │
│              │   StartAsync(ctx, ct)
│              │◀──── long-running services start
│              │
│              │   StopAsync(ct)
│              │◀──── graceful shutdown
└──────────────┘
```

Every plugin is loaded inside its own `AssemblyLoadContext` with a narrow
set of **shared types** (SDK + Core interfaces + Microsoft.Extensions.*).
Anything else the plugin depends on lives INSIDE the plugin ALC and is
unloaded when the daemon stops.

---

## 2 · The `IWdcPlugin` interface

```csharp
namespace NKS.WebDevConsole.Core.Interfaces;

public interface IWdcPlugin
{
    string Id { get; }            // e.g. "nks.wdc.apache" — unique, stable
    string DisplayName { get; }   // e.g. "Apache HTTP Server"
    string Version { get; }       // semver, e.g. "0.1.62"

    void Initialize(IServiceCollection services, IPluginContext context);
    Task StartAsync(IPluginContext context, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

Derive from `PluginBase` (in `NKS.WebDevConsole.Plugin.SDK`) to get default
no-op implementations:

```csharp
using NKS.WebDevConsole.Plugin.SDK;

public sealed class MyPlugin : PluginBase
{
    public override string Id => "nks.wdc.myplugin";
    public override string DisplayName => "My Plugin";
    public override string Version => "0.1.0";

    public override void Initialize(IServiceCollection services, IPluginContext ctx)
    {
        services.AddSingleton<MyConfig>();
        services.AddScoped<MyService>();
    }

    public override Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        ctx.Logger.LogInformation("MyPlugin starting…");
        return Task.CompletedTask;
    }
}
```

### Id conventions

- Reverse-domain style, lowercase. Official plugins live under
  `nks.wdc.<name>`; community plugins should use their own namespace.
- MUST be stable across versions — the id is the key for plugin enable /
  disable state and for catalog lookups.

---

## 3 · `IPluginContext`

Passed to `Initialize` and `StartAsync`. Read-only projection of host
capabilities the plugin is allowed to use.

| Member | Type | Purpose |
|---|---|---|
| `Logger` | `ILogger` | Pre-scoped logger (`source: <plugin-id>`) |
| `Endpoints` | `EndpointRegistration` | Register REST endpoints (see §4) |
| `Events` | `IPluginEventBus` | Publish progress / metrics / log events |
| `Services` | `IServiceProvider` | Shared daemon services (read-only) |
| `DataDir` | `string` | Filesystem dir under `~/.wdc/plugins/<id>/` |
| `BinariesRoot` | `string` | Where WDC-managed binaries live |

---

## 4 · Registering REST endpoints

```csharp
public override void Initialize(IServiceCollection services, IPluginContext ctx)
{
    ctx.Endpoints
      .MapGet("/status", (HttpContext http, MyService svc) => svc.GetStatus())
      .MapPost("/restart", async (MyService svc) => { await svc.Restart(); return Results.Ok(); });
}
```

Endpoints are mounted by the daemon under `/api/plugin/<plugin-id>/*`.
Auth, rate-limit, and access-log middleware run automatically before the
plugin handler.

Conventions:

- Keep handler delegates small and DI-driven; inject services, not
  plugin fields.
- Use `Results.BadRequest(...)` / `Results.NotFound(...)` — the daemon
  will surface these to the frontend with their proper status codes.
- Streaming responses (SSE, large log tails) are fine; the daemon does
  NOT buffer them.

---

## 5 · UI contributions (`PluginUiDefinition`)

A plugin describes the UI surface it wants via the `GetUiDefinition()`
hook (called by the daemon lazily on first enable):

```csharp
public override PluginUiDefinition GetUiDefinition() =>
    new UiSchemaBuilder(Id)
        .Category("Services")             // sidebar grouping label
        .Icon("Link")                     // Element Plus component name
        .AddNavEntry("cloudflare", "Cloudflare", "/cloudflare", "Link", order: 60)
        .AddServiceCard("cloudflare")
        .AddLogViewer("cloudflare")
        .AddPanel("cloudflare-tunnel-panel", new() { ["serviceId"] = "cloudflare" })
        .Build();
```

The Electron frontend consumes this at `/api/plugins/ui` (aggregator:
returns nav entries from every enabled plugin) and `/api/plugins/{id}/ui`
(per-plugin panels) and dynamically renders:

- **Sidebar nav entries** contributed via `AddNavEntry(id, label, route, icon, order)` —
  rendered inside the `Tools` section of `AppSidebar.vue`. Each plugin
  chooses its own route (e.g. `/composer`, `/ssl`). Disabling a plugin
  removes its entries on the next catalog refresh.
- Dashboard cards per service (`AddServiceCard`)
- Per-site edit-tab panels (`AddPanel("site-edit", { tab: … })`)
- Built-in widgets: log viewer, config editor, version switcher, metrics chart

### `NavContribution` schema

```csharp
public record NavContribution(
    string Id,        // stable, e.g. "composer" — used as router route name
    string Label,     // human-readable, e.g. "Composer"
    string Icon,      // Element Plus v2 component name (Box, Files, Lock, Link, …)
    string Route,     // router path, must start with "/" — e.g. "/composer"
    int Order = 100); // sort key inside the category; lower = first
```

Icons resolve via a frontend registry — currently supports `Link`, `Download`,
`Box`, `Setting`, `Coin`, `Lock`, `Cpu`, `House`, `Connection`, `Document`,
`Files`, `QuestionFilled`, `User`, `UserFilled`. Unknown icon names fall back
to `Box`.

Disabling the plugin removes ALL of those UI contributions at once.
See [`MANIFEST.md`](./MANIFEST.md) for the JSON-serializable shape.

---

## 6 · Event bus (`IPluginEventBus`)

Publish structured events the daemon forwards over SSE:

```csharp
ctx.Events.EmitValidationStarted(serviceId: "apache");
ctx.Events.EmitLogLine(serviceId: "apache", line: "…");
ctx.Events.EmitMetrics(serviceId: "apache", cpu: 0.12, memMb: 48.3);
ctx.Events.EmitServiceStateChanged(serviceId: "apache", state: ServiceState.Running);
```

Do NOT try to write to the daemon's event log directly — go through the
event bus.

---

## 7 · `plugin.json` manifest

Every plugin MUST ship a `plugin.json` next to its DLL (embedded
resource or copy-to-output). Minimum fields:

```json
{
  "id": "nks.wdc.myplugin",
  "name": "My Plugin",
  "version": "0.1.0",
  "minWdcVersion": "0.1.60",
  "entry": "MyNamespace.MyPlugin, MyAssembly",
  "permissions": { "network": true, "process": true, "gui": true }
}
```

Full schema in [`MANIFEST.md`](./MANIFEST.md). The daemon reads it to
enumerate available plugins without instantiating them (so disabled
plugins don't load into memory at all).

---

## 8 · Cross-ALC pattern

Because each plugin lives in its own ALC, naively grabbing a type from a
plugin from the host daemon using `plugin.GetType("...")` will not match
the SAME type loaded in the default ALC. Use reflection + the plugin's
own assembly for the type, then resolve the instance from the SHARED
service provider:

```csharp
var pluginAssembly = pluginLoader.Get("nks.wdc.composer").Assembly;
var invokerType = pluginAssembly.GetType("NKS.WebDevConsole.Plugin.Composer.ComposerInvoker");
if (invokerType is null) return null;
var invoker = sp.GetService(invokerType);
```

Methods on the reflected instance are called via `MethodInfo.Invoke`.
The Composer plugin ships the canonical example in
`ResolveComposerInvoker()` on the host daemon side.

---

## 9 · Testing

- Unit tests live next to each plugin csproj (`NKS.WebDevConsole.Plugin.X.Tests/`).
- Prefer testing the pure logic (config generators, command builders)
  with simple in-memory mocks for `IProcessRunner` / `IPluginContext`.
- Lifecycle tests can use `TestHost` from the daemon test-suite to spin
  up a real `WebApplication` with just your plugin loaded.

---

## 10 · Shipping

- Bump `plugin.json.version` and the csproj `<Version>` if you changed
  behaviour.
- Commit, push, open a PR.
- On merge to main, the next `v*` tag on THIS repo ships your plugin
  automatically — the Release workflow packages
  `NKS.WebDevConsole.Plugin.<Name>.zip` and attaches it to the GitHub
  release. `wdc-catalog-api` picks it up on its next refresh and every
  WDC install can offer the upgrade.

---

## See also

- [`WRITING-A-PLUGIN.md`](./WRITING-A-PLUGIN.md) — step-by-step walkthrough
- [`MANIFEST.md`](./MANIFEST.md) — `plugin.json` full schema reference
- [`../README.md`](../README.md) — repo overview
- [WDC daemon source](https://github.com/nks-hub/webdev-console/tree/main/src/daemon)
