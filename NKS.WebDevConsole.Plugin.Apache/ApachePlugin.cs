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
    public string Version => "1.0.10";

    private ApacheModule? _module;
    private IDisposable? _binaryInstalledSub;

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

        // Subscribe to BinaryInstalled so a post-boot `POST /api/binaries/install
        // {"app":"apache",...}` triggers re-detection without the old
        // StartAsync lazy-init hack (task #9). Filter on the app id — the
        // bus fans out every install to every subscriber.
        var bus = context.ServiceProvider.GetService(typeof(IBinaryInstalledEventBus))
            as IBinaryInstalledEventBus;
        _binaryInstalledSub = bus?.Subscribe(async evt =>
        {
            if (!string.Equals(evt.App, "apache", StringComparison.OrdinalIgnoreCase)) return;
            logger.LogInformation(
                "BinaryInstalled apache {Version} → re-initializing Apache module", evt.Version);
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
            .Category("Web Servers")
            .Icon("el-icon-connection")
            .SetServiceCategory("web", "apache")
            .AddServiceCard("apache")
            .AddConfigEditor("apache")
            .AddLogViewer("apache")
            .AddMetricsChart("apache")
            .Build();
}
