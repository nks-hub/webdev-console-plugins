using Microsoft.Extensions.DependencyInjection;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Plugin.SDK;
using NKS.WebDevConsole.Plugin.SDK.Deploy;

namespace NKS.WebDevConsole.Plugin.LocalRsync;

/// <summary>
/// Plugin entry-point for the local-rsync deploy backend. Registers
/// <see cref="LocalRsyncBackend"/> as a SECOND IDeployBackend implementation
/// alongside <c>NksDeployBackend</c>. The daemon picks among multiple
/// registered backends by calling <c>CanDeploy(domain)</c> and selecting
/// the first that returns true — local-rsync only matches when the site's
/// deploy.neon explicitly opts in via a <c>backend: localrsync</c> key, so
/// it will not steal deploys away from NksDeploy.
/// </summary>
public sealed class LocalRsyncPlugin : PluginBase
{
    public override string Id => "nks.wdc.deploy.localrsync";
    public override string DisplayName => "Local rsync deploy";
    public override string Version => "0.1.0";

    public string Description =>
        "IDeployBackend implementation that wraps `rsync -av {site_root} {target_path}`. " +
        "Lives alongside NksDeploy as proof that IDeployBackend isn't tied to nksdeploy.";

    public override void Initialize(IServiceCollection services, IPluginContext context)
    {
        // Register as IDeployBackend AS WELL as NksDeployBackend. The host's
        // DI container will resolve IEnumerable<IDeployBackend> for the
        // selector; resolving as a single IDeployBackend would only return
        // the LAST registration. The daemon's deploy router enumerates and
        // delegates per-domain via CanDeploy.
        services.AddSingleton<IDeployBackend, LocalRsyncBackend>();
    }
}
