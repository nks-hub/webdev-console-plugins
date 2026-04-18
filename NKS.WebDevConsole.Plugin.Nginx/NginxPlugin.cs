using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.Nginx;

/// <summary>
/// IWdcPlugin entry point for the Nginx service module.
/// Registered via AssemblyLoadContext by the daemon's PluginLoader.
/// </summary>
public sealed class NginxPlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.nginx";
    public string DisplayName => "Nginx";
    public string Version => "1.0.0";

    private NginxModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<NginxModule>();

        // Expose IServiceModule so the daemon's ProcessManager can discover it
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<NginxModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<NginxPlugin>();
        logger.LogInformation("Nginx plugin v{Version} loaded (scaffold — lifecycle stubbed)", Version);

        _module = context.ServiceProvider.GetRequiredService<NginxModule>();
        await _module.InitializeAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Web Servers")
            .Icon("el-icon-connection")
            .AddServiceCard("nginx")
            .AddConfigEditor("nginx")
            .AddLogViewer("nginx")
            .AddMetricsChart("nginx")
            .Build();
}
