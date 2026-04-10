using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Plugin.MySQL;

/// <summary>
/// IWdcPlugin entry point — registered via AssemblyLoadContext by the daemon's PluginLoader.
/// </summary>
public sealed class MySqlPlugin : IWdcPlugin
{
    public string Id => "nks.wdc.mysql";
    public string DisplayName => "MySQL";
    public string Version => "1.0.0";

    private MySqlModule? _module;

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
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_module is not null)
            await _module.StopAsync(ct);
    }
}
