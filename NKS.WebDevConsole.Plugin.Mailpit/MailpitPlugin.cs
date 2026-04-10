using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Plugin.Mailpit;

/// <summary>
/// IWdcPlugin entry point — registered via AssemblyLoadContext by the daemon's PluginLoader.
/// </summary>
public sealed class MailpitPlugin : IWdcPlugin
{
    public string Id => "nks.wdc.mailpit";
    public string DisplayName => "Mailpit";
    public string Version => "1.0.0";

    private MailpitModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<MailpitModule>();
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<MailpitModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<MailpitPlugin>();
        logger.LogInformation("Mailpit plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<MailpitModule>();
        await _module.InitializeAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_module is not null)
            await _module.StopAsync(ct);
    }
}
