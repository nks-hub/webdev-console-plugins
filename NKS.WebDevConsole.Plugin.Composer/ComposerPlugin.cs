using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Plugin.Composer;

/// <summary>
/// IWdcPlugin entry point for the Composer PHP dependency manager.
///
/// Composer is a stateless CLI tool — it has no daemon process to start or stop.
/// This plugin does NOT implement IServiceModule. Instead, it registers
/// <see cref="ComposerConfig"/> and <see cref="ComposerInvoker"/> in the DI
/// container so that other components (REST endpoints, site orchestration) can
/// resolve them via the plugin's ServiceProvider.
///
/// Binary discovery is performed on <see cref="StartAsync"/>: the plugin walks
/// <c>~/.wdc/binaries/composer/</c> for versioned <c>composer.phar</c> files
/// (newest version wins by semver), then falls back to the system <c>composer</c>
/// shim on PATH if no managed phar is present.
/// </summary>
public sealed class ComposerPlugin : IWdcPlugin
{
    public string Id => "nks.wdc.composer";
    public string DisplayName => "Composer";
    public string Version => "1.0.0";

    public string Description =>
        "PHP dependency manager — per-site composer.json/lock management, " +
        "framework auto-install, runs under the active PHP version. " +
        "Manages versioned composer.phar files under ~/.wdc/binaries/composer/ " +
        "with automatic semver-ordered discovery and PATH fallback.";

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<ComposerConfig>(_ =>
        {
            var cfg = new ComposerConfig();
            cfg.ApplyOwnBinaryDefaults();
            return cfg;
        });

        services.AddSingleton<ComposerInvoker>(sp =>
            new ComposerInvoker(sp.GetRequiredService<ComposerConfig>()));
    }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var logger = context.GetLogger<ComposerPlugin>();
        var config = context.ServiceProvider.GetRequiredService<ComposerConfig>();

        var managed = config.ApplyOwnBinaryDefaults();
        if (managed)
            logger.LogInformation("Composer plugin v{Version} loaded — phar: {Path}", Version, config.ExecutablePath);
        else
            logger.LogInformation(
                "Composer plugin v{Version} loaded — no managed phar found under {Root}; using system fallback: {Path}. "
                + "Install with POST /api/binaries/install {{ \"app\": \"composer\", \"version\": \"2.8.0\" }}",
                Version, config.BinariesRoot, config.ExecutablePath);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
