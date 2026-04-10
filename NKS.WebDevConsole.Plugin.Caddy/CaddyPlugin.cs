using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.Caddy;

/// <summary>
/// Caddy plugin entry point — registered via AssemblyLoadContext by the daemon's PluginLoader.
/// Provides an alternative to Apache with native HTTP/2 and automatic HTTPS support.
/// </summary>
public sealed class CaddyPlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.caddy";
    public string DisplayName => "Caddy";
    public string Version => "1.0.0";

    private CaddyModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<CaddyConfig>();
        services.AddSingleton<CaddyModule>();
        // Expose IServiceModule so the daemon's ProcessManager can discover it
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<CaddyModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<CaddyPlugin>();
        logger.LogInformation("Caddy plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<CaddyModule>();
        await _module.InitializeAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_module is not null)
            await _module.StopAsync(ct);
    }

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Web Servers")
            .Icon("el-icon-connection")
            .AddServiceCard("caddy")
            .AddConfigEditor("caddy")
            .AddLogViewer("caddy")
            .AddMetricsChart("caddy")
            .Build();
}
