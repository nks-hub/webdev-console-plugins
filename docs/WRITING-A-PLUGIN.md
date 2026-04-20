# Writing a new WDC plugin — walkthrough

This is a hands-on guide. For the reference spec see
[`PLUGIN-API.md`](./PLUGIN-API.md) and [`MANIFEST.md`](./MANIFEST.md).

We'll build a trivial `HelloPlugin` that:

- Exposes `GET /api/plugin/nks.wdc.hello/greet` returning `{ "message": "…" }`
- Contributes a dashboard service card

---

## 1 · Create the project

```cmd
cd webdev-console-plugins
dotnet new classlib -n NKS.WebDevConsole.Plugin.Hello -f net9.0 --no-restore
```

Edit `NKS.WebDevConsole.Plugin.Hello/NKS.WebDevConsole.Plugin.Hello.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Version>0.1.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="NKS.WebDevConsole.Core">
      <HintPath>..\..\nks-ws\src\daemon\NKS.WebDevConsole.Core\bin\Release\net9.0\NKS.WebDevConsole.Core.dll</HintPath>
    </Reference>
    <Reference Include="NKS.WebDevConsole.Plugin.SDK">
      <HintPath>..\..\nks-ws\src\daemon\NKS.WebDevConsole.Plugin.SDK\bin\Release\net9.0\NKS.WebDevConsole.Plugin.SDK.dll</HintPath>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <None Update="plugin.json" CopyToOutputDirectory="Always" />
  </ItemGroup>
</Project>
```

(Once the SDK is published to NuGet, the `<Reference>` block becomes a
`<PackageReference Include="NKS.WebDevConsole.Plugin.SDK">`.)

## 2 · Write the manifest

`NKS.WebDevConsole.Plugin.Hello/plugin.json`:

```json
{
  "id": "nks.wdc.hello",
  "name": "Hello Plugin",
  "version": "0.1.0",
  "description": "Trivial plugin demo",
  "minWdcVersion": "0.1.60",
  "entry": "NKS.WebDevConsole.Plugin.Hello.HelloPlugin, NKS.WebDevConsole.Plugin.Hello",
  "permissions": { "network": false, "process": false, "gui": true },
  "ui": {
    "category": "tools",
    "icon": "mdi:hand-wave",
    "panels": [
      { "type": "service-card", "serviceId": "hello" }
    ]
  }
}
```

## 3 · Implement `IWdcPlugin`

`NKS.WebDevConsole.Plugin.Hello/HelloPlugin.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.Hello;

public sealed class HelloPlugin : PluginBase
{
    public override string Id => "nks.wdc.hello";
    public override string DisplayName => "Hello Plugin";
    public override string Version => "0.1.0";

    public override void Initialize(IServiceCollection services, IPluginContext ctx)
    {
        services.AddSingleton<HelloService>();

        ctx.Endpoints.MapGet("/greet", (HelloService svc) =>
            Results.Ok(new { message = svc.Greet() })
        );
    }

    public override Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        ctx.Logger.LogInformation("[hello] ready");
        return Task.CompletedTask;
    }

    public override PluginUiDefinition? GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("tools")
            .Icon("mdi:hand-wave")
            .AddServiceCard("hello")
            .Build();
}

public sealed class HelloService
{
    public string Greet() => $"Hello from WDC at {DateTime.UtcNow:O}";
}
```

## 4 · Build + smoke test locally

```cmd
dotnet build -c Release NKS.WebDevConsole.Plugin.Hello
set NKS_WDC_PLUGINS_DEV_DIR=%CD%\NKS.WebDevConsole.Plugin.Hello\bin\Release\net9.0
```

Then start the WDC daemon — it picks up the dev dir and loads your
plugin. Hit the endpoint:

```cmd
curl http://localhost:<port>/api/plugin/nks.wdc.hello/greet
```

You should see `{"message":"Hello from WDC at 2026-…"}`.

## 5 · Add tests

```cmd
dotnet new xunit -n NKS.WebDevConsole.Plugin.Hello.Tests -f net9.0
```

Test `HelloService.Greet()` directly (no DI / no daemon). Add a minimal
end-to-end test using `TestServer` only if you're touching endpoint
routing.

## 6 · Ship it

1. Commit + push + open a PR.
2. After merge, next `v*` tag on the plugins repo picks up your plugin
   in the Release workflow and the per-plugin zip lands on the GitHub
   release.
3. `wdc-catalog-api` indexes it; every running WDC can offer it from
   the /plugins marketplace.

---

## Common pitfalls

- **Package / DLL version mismatch** — if the SDK changes a signature,
  rebuild your plugin before the next daemon release, or pin SDK in
  the csproj.
- **Cross-ALC leaks** — never pass raw `Type` references from your
  plugin ALC to the host expecting reference equality. Use reflection +
  shared service provider per `PLUGIN-API.md` §8.
- **Too much work in `Initialize`** — it runs synchronously on the
  daemon startup thread. Heavy work belongs in `StartAsync` (which
  the daemon runs with a timeout guard).
- **Forgetting `plugin.json`** — the daemon skips assemblies without a
  manifest, even if they implement `IWdcPlugin`. Always include the
  JSON and set `CopyToOutputDirectory="Always"`.
