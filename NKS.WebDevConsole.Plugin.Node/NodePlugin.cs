using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.Node;

/// <summary>
/// IWdcPlugin entry point for the Node.js process manager.
/// Manages per-site Node.js apps as supervised services — any site with
/// <see cref="SiteConfig.NodeUpstreamPort"/> > 0 can have its Node process
/// spawned and monitored by this plugin.
/// </summary>
public sealed class NodePlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.node";
    public string DisplayName => "Node.js";
    public string Version => "1.0.0";

    private NodeModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<NodeModule>();
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<NodeModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<NodePlugin>();
        logger.LogInformation("Node.js plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<NodeModule>();
        await _module.InitializeAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_module is not null)
            await _module.StopAllAsync(ct);
    }

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Tools")
            .Icon("el-icon-monitor")
            .SetServiceCategory("lang", "node")
            .AddServiceCard("node")
            .AddLogViewer("node")
            .AddMetricsChart("node")
            .Build();
}
