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
    public string Version => "1.0.5";

    private RedisModule? _module;
    private IDisposable? _binaryInstalledSub;

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

        // BinaryInstalled subscription: a post-boot `POST /api/binaries/install
        // {"app":"redis"}` re-runs detection so the next Start sees the
        // freshly extracted redis-server. Replaces the Start-time lazy
        // DetectRedisExecutable() call (task #9).
        var bus = context.ServiceProvider.GetService(typeof(IBinaryInstalledEventBus))
            as IBinaryInstalledEventBus;
        _binaryInstalledSub = bus?.Subscribe(async evt =>
        {
            if (!string.Equals(evt.App, "redis", StringComparison.OrdinalIgnoreCase)) return;
            logger.LogInformation(
                "BinaryInstalled redis {Version} → re-initializing Redis module", evt.Version);
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
            .Category("Caches")
            .Icon("el-icon-coin")
            .SetServiceCategory("cache", "redis")
            .AddServiceCard("redis")
            .AddConfigEditor("redis")
            .AddLogViewer("redis")
            .AddMetricsChart("redis")
            .Build();
}
