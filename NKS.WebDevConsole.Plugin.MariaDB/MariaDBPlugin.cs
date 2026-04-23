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
    public string Version => "1.0.1";

    private MariaDBModule? _module;
    private IDisposable? _binaryInstalledSub;

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

        // BinaryInstalled subscription: a post-boot `POST /api/binaries/install
        // {"app":"mariadb"}` re-runs detection so the next Start finds the
        // freshly extracted mariadbd/mysqld. Replaces the Start-time
        // lazy-init snippet (task #9).
        var bus = context.ServiceProvider.GetService(typeof(IBinaryInstalledEventBus))
            as IBinaryInstalledEventBus;
        _binaryInstalledSub = bus?.Subscribe(async evt =>
        {
            if (!string.Equals(evt.App, "mariadb", StringComparison.OrdinalIgnoreCase)) return;
            logger.LogInformation(
                "BinaryInstalled mariadb {Version} → re-initializing MariaDB module", evt.Version);
            if (_module is not null)
                await _module.InitializeAsync(CancellationToken.None);
        });
    }

    public Task StopAsync(CancellationToken ct)
    {
        _binaryInstalledSub?.Dispose();
        _binaryInstalledSub = null;
        return Task.CompletedTask;
    }

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Databases")
            .Icon("el-icon-coin")
            .SetServiceCategory("db", "mariadb")
            .AddServiceCard("mariadb")
            .AddConfigEditor("mariadb")
            .AddLogViewer("mariadb")
            .AddMetricsChart("mariadb")
            .Build();
}
