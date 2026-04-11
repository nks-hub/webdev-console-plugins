using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.Redis;

/// <summary>
/// IWdcPlugin entry point — registered via AssemblyLoadContext by the daemon's PluginLoader.
/// </summary>
public sealed class RedisPlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.redis";
    public string DisplayName => "Redis";
    public string Version => "1.0.0";

    private RedisModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<RedisModule>();
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<RedisModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<RedisPlugin>();
        logger.LogInformation("Redis plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<RedisModule>();
        await _module.InitializeAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_module is not null)
            await _module.StopAsync(ct);
    }

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Caches")
            .Icon("el-icon-coin")
            .AddServiceCard("redis")
            .AddConfigEditor("redis")
            .AddLogViewer("redis")
            .AddMetricsChart("redis")
            .Build();
}
