using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.PHP;

/// <summary>
/// IWdcPlugin entry point for the PHP multi-version module.
/// </summary>
public sealed class PhpPlugin : IWdcPlugin
{
    public string Id => "nks.wdc.php";
    public string DisplayName => "PHP (Multi-version)";
    public string Version => "1.0.0";

    private PhpModule? _module;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<PhpVersionManager>();
        services.AddSingleton<PhpIniManager>();
        services.AddSingleton<PhpExtensionManager>();
        services.AddSingleton<PhpCliAliasManager>();

        // PhpModuleConfig resolved from daemon AppConfig in production
        services.AddSingleton(sp =>
        {
            var appDir = AppContext.BaseDirectory;
            return new PhpModuleConfig
            {
                AppDirectory = appDir,
                ConfigBaseDirectory = Path.Combine(appDir, "config", "php"),
                ShimDirectory = Path.Combine(appDir, "bin"),
                LogDirectory = Path.Combine(appDir, "logs"),
                RunDirectory = Path.GetTempPath()
            };
        });

        services.AddSingleton<ComposerIntegration>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ComposerIntegration>>();
            return new ComposerIntegration(logger, AppContext.BaseDirectory);
        });

        services.AddSingleton<PhpModule>();
        services.AddSingleton<IServiceModule>(sp => sp.GetRequiredService<PhpModule>());
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<PhpPlugin>();
        logger.LogInformation("PHP plugin v{Version} loaded", Version);

        _module = context.ServiceProvider.GetRequiredService<PhpModule>();
        await _module.InitializeAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_module is not null)
            await _module.StopAsync(ct);
    }
}
