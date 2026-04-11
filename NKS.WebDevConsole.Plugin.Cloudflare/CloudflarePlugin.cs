using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.Cloudflare;

/// <summary>
/// Cloudflare Tunnel plugin entry point — registered via AssemblyLoadContext.
///
/// Wraps two responsibilities:
///   1. <see cref="CloudflareModule"/> runs the cloudflared binary as an IServiceModule
///      so it appears in the Dashboard like Apache/MySQL — start/stop/logs/health.
///   2. <see cref="CloudflareApi"/> is a thin wrapper around the Cloudflare REST API
///      used for listing zones, creating DNS records that point at the active tunnel,
///      and enumerating existing tunnels for a given account.
///
/// Settings live under ~/.wdc/cloudflare/config.json. Required fields for basic
/// operation: <c>cloudflaredPath</c>, <c>tunnelToken</c> (JWT). API features
/// additionally need <c>apiToken</c> with at least
///   Account > Cloudflare Tunnel > Edit
///   Account > Account Settings > Read
///   Zone > Zone > Read
///   Zone > DNS > Edit
/// </summary>
public sealed class CloudflarePlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.cloudflare";
    public string DisplayName => "Cloudflare Tunnel";
    public string Version => "1.0.0";

    private CloudflareModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<CloudflareConfig>(_ => CloudflareConfig.LoadOrDefault());
        services.AddSingleton<CloudflareApi>();
        services.AddSingleton<CloudflareModule>();
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<CloudflareModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<CloudflarePlugin>();
        logger.LogInformation("Cloudflare Tunnel plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<CloudflareModule>();
        await _module.InitializeAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_module is not null)
            await _module.StopAsync(ct);
    }

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Networking")
            .Icon("el-icon-link")
            .AddServiceCard("cloudflare")
            .AddLogViewer("cloudflare")
            .AddPanel("cloudflare-tunnel-panel", new()
            {
                ["serviceId"] = "cloudflare",
                ["title"] = "Tunnel & DNS",
            })
            .Build();
}
