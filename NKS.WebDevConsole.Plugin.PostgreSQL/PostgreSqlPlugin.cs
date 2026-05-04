using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.PostgreSQL;

public sealed class PostgreSqlPlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.postgresql";
    public string DisplayName => "PostgreSQL";
    public string Version => "1.0.0";

    private PostgreSqlModule? _module;
    private IDisposable? _binaryInstalledSub;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<PostgreSqlModule>();
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<PostgreSqlModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<PostgreSqlPlugin>();
        logger.LogInformation("PostgreSQL plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<PostgreSqlModule>();
        await _module.InitializeAsync(ct);

        var bus = context.ServiceProvider.GetService(typeof(IBinaryInstalledEventBus))
            as IBinaryInstalledEventBus;
        _binaryInstalledSub = bus?.Subscribe(async evt =>
        {
            if (!string.Equals(evt.App, "postgresql", StringComparison.OrdinalIgnoreCase)) return;
            logger.LogInformation(
                "BinaryInstalled postgresql {Version} -> re-initializing PostgreSQL module", evt.Version);
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
            .SetServiceCategory("db", "postgresql")
            .AddServiceCard("postgresql")
            .AddConfigEditor("postgresql")
            .AddLogViewer("postgresql")
            .AddMetricsChart("postgresql")
            .Build();
}
