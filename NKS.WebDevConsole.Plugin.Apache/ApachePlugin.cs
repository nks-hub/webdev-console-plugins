using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.Apache;

/// <summary>
/// IWdcPlugin entry point — registered via AssemblyLoadContext by the daemon's PluginLoader.
/// </summary>
public sealed class ApachePlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.apache";
    public string DisplayName => "Apache HTTP Server";
    public string Version => "1.0.0";

    private ApacheModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<ApacheHealthChecker>();
        services.AddSingleton<ApacheModule>();

        // Expose IServiceModule so the daemon's ProcessManager can discover it
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<ApacheModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<ApachePlugin>();
        logger.LogInformation("Apache plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<ApacheModule>();
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
            .AddServiceCard("apache")
            .AddConfigEditor("apache")
            .AddLogViewer("apache")
            .AddMetricsChart("apache")
            .Build();
}
