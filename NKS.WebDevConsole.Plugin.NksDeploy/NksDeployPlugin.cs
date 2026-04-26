using Microsoft.Extensions.DependencyInjection;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK;
using NKS.WebDevConsole.Plugin.SDK.Deploy;

namespace NKS.WebDevConsole.Plugin.NksDeploy;

/// <summary>
/// IWdcPlugin entry point for the NksDeploy backend.
///
/// Registers <see cref="NksDeployBackend"/> as the host's <see cref="IDeployBackend"/>
/// implementation so the daemon's deploy routes resolve to it across the
/// AssemblyLoadContext boundary. The plugin id is the literal string
/// <c>"nks.wdc.deploy"</c> (NOT inferred from class name) — the
/// PluginLoader.WireEndpoints helper uses it as the URL prefix for any
/// endpoints the plugin registers (currently none — REST routes are wired
/// from the nks-ws daemon side under /api/nks.wdc.deploy/*).
/// </summary>
public sealed class NksDeployPlugin : PluginBase
{
    public override string Id => "nks.wdc.deploy";
    public override string DisplayName => "NksDeploy";
    public override string Version => "0.1.0";

    // Description is a default interface method on IWdcPlugin (not virtual on
    // PluginBase). Implement it as a regular property — the explicit interface
    // dispatch picks it up.
    public string Description =>
        "Zero-downtime deployment for Nette apps via the bundled nksdeploy " +
        "PHP CLI. CliWrap subprocess, NDJSON progress stream into the wdc " +
        "deploy drawer, SQLite-backed run journal.";

    public override void Initialize(IServiceCollection services, IPluginContext context)
    {
        // Register the backend as IDeployBackend. The shared assembly list in
        // PluginLoadContext (Plugin.SDK is shared) means the daemon and the
        // plugin agree on type identity, so the host can resolve
        // IDeployBackend from this plugin's container or from the host's via
        // a forwarding registration the daemon adds.
        services.AddSingleton<IDeployBackend, NksDeployBackend>();
    }

    public override Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        // No long-running work to spin up — the backend is stateless on
        // start. Per-deploy state lives in deploy_runs; lock state lives
        // on the remote host. We don't yet probe nksdeploy.phar presence
        // here (that's deferred to first StartDeployAsync invocation so
        // the daemon can boot on machines without PHP installed).
        return Task.CompletedTask;
    }

    public override void RegisterEndpoints(EndpointRegistration registration)
    {
        // 5 REST endpoints under /api/nks.wdc.deploy/. Auth covered by the
        // daemon's existing Bearer middleware (it matches /api/*). DI on
        // each handler resolves IDeployBackend (this plugin's instance) plus
        // any cross-cutting services like IDeployEventBroadcaster.
        NksDeployRoutes.Register(registration);
    }
}
