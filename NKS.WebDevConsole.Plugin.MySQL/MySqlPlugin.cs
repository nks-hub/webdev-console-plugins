using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.MySQL;

/// <summary>
/// IWdcPlugin entry point — registered via AssemblyLoadContext by the daemon's PluginLoader.
/// </summary>
public sealed class MySqlPlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.mysql";
    public string DisplayName => "MySQL";
    public string Version => "1.0.7";

    private MySqlModule? _module;
    private IDisposable? _binaryInstalledSub;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<MySqlModule>();
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<MySqlModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<MySqlPlugin>();
        logger.LogInformation("MySQL plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<MySqlModule>();
        await _module.InitializeAsync(ct);

        // BinaryInstalled subscription replaces the Start-time lazy-init
        // probe (task #9). Post-wizard installs of mysql trigger a
        // re-detection pass so the next Start sees ExecutablePath.
        var bus = context.ServiceProvider.GetService(typeof(IBinaryInstalledEventBus))
            as IBinaryInstalledEventBus;
        _binaryInstalledSub = bus?.Subscribe(async evt =>
        {
            if (!string.Equals(evt.App, "mysql", StringComparison.OrdinalIgnoreCase)) return;
            logger.LogInformation(
                "BinaryInstalled mysql {Version} → re-initializing MySQL module", evt.Version);
            if (_module is not null)
                await _module.InitializeAsync(CancellationToken.None);
        });
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _binaryInstalledSub?.Dispose();
        _binaryInstalledSub = null;
        if (_module is not null)
            await _module.StopAsync(ct);
    }

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Databases")
            .Icon("el-icon-coin")
            .SetServiceCategory("db", "mysql")
            .AddServiceCard("mysql")
            .AddConfigEditor("mysql")
            .AddLogViewer("mysql")
            .AddMetricsChart("mysql")
            .Build();
}
