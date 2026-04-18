using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.MariaDB;

/// <summary>
/// IWdcPlugin entry point for the MariaDB service module.
/// Registered via AssemblyLoadContext by the daemon's PluginLoader.
/// </summary>
public sealed class MariaDBPlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.mariadb";
    public string DisplayName => "MariaDB";
    public string Version => "1.0.0";

    private MariaDBModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<MariaDBModule>();

        // Expose IServiceModule so the daemon's ProcessManager can discover it
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<MariaDBModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<MariaDBPlugin>();
        logger.LogInformation("MariaDB plugin v{Version} loaded (scaffold — lifecycle stubbed)", Version);

        _module = context.ServiceProvider.GetRequiredService<MariaDBModule>();
        await _module.InitializeAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Databases")
            .Icon("el-icon-coin")
            .AddServiceCard("mariadb")
            .AddConfigEditor("mariadb")
            .AddLogViewer("mariadb")
            .AddMetricsChart("mariadb")
            .Build();
}
