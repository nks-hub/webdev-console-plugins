using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.PHP;

/// <summary>
/// IWdcPlugin entry point for the PHP multi-version module.
/// Scans only NKS WDC managed binaries under <c>~/.wdc/binaries/php/&lt;version&gt;/</c>
/// — never MAMP/XAMPP/WAMP/system installs — and exposes a REST-friendly list of
/// detected versions via <see cref="GetInstalledVersions"/>.
/// </summary>
public sealed class PhpPlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.php";
    public string DisplayName => "PHP (Multi-version)";
    public string Version => "1.0.0";

    private PhpModule? _module;
    private PhpVersionManager? _versionManager;
    private IReadOnlyList<PhpInstallation> _installations = [];

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<PhpVersionManager>();
        services.AddSingleton<PhpIniManager>();
        services.AddSingleton<PhpExtensionManager>();
        services.AddSingleton<PhpCliAliasManager>();

        // PhpModuleConfig resolved from daemon AppConfig in production. Logs and
        // run-state live in the user's WDC data dir (~/.wdc/logs and ~/.wdc/data/run)
        // so the daemon doesn't need write access into its own install directory —
        // a hard requirement for /Applications-installed Mac apps where the bundle
        // is read-only — and so the path survives reinstalls/upgrades.
        services.AddSingleton(sp =>
        {
            var appDir = AppContext.BaseDirectory;
            var logDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.LogsRoot, "php");
            var runDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "run");
            var configDir = Path.Combine(NKS.WebDevConsole.Core.Services.WdcPaths.DataRoot, "php");
            Directory.CreateDirectory(logDir);
            Directory.CreateDirectory(runDir);
            Directory.CreateDirectory(configDir);
            return new PhpModuleConfig
            {
                AppDirectory = appDir,
                ConfigBaseDirectory = configDir,
                ShimDirectory = Path.Combine(appDir, "bin"),
                LogDirectory = logDir,
                RunDirectory = runDir
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

        _versionManager = context.ServiceProvider.GetRequiredService<PhpVersionManager>();
        _module = context.ServiceProvider.GetRequiredService<PhpModule>();
        await _module.InitializeAsync(ct);

        // Cache detected installations for REST queries
        _installations = _module.Installations;

        logger.LogInformation("PHP plugin detected {Count} version(s), active={Active}",
            _installations.Count, _versionManager.ActiveVersion ?? "none");

        foreach (var php in _installations)
        {
            logger.LogInformation("  PHP {Version} | exe={Exe} | cgi={Cgi} | ext={ExtCount} | port={Port}",
                php.Version, php.ExecutablePath,
                php.FpmExecutable ?? "(none)",
                php.Extensions.Length,
                php.FcgiPort);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_module is not null)
            await _module.StopAsync(ct);
    }

    /// <summary>
    /// Returns a REST-friendly summary of all detected PHP installations.
    /// </summary>
    public IReadOnlyList<PhpVersionInfo> GetInstalledVersions()
    {
        return _installations.Select(php => new PhpVersionInfo(
            php.Version,
            php.MajorMinor,
            php.ExecutablePath,
            php.FpmExecutable,
            php.Directory,
            php.Extensions,
            php.FcgiPort,
            IsActive: php.MajorMinor == _versionManager?.ActiveVersion
        )).ToList();
    }

    /// <summary>Sets the active PHP version for new sites.</summary>
    public void SetActiveVersion(string majorMinor)
    {
        _versionManager?.SetActiveVersion(majorMinor);
    }

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Runtimes")
            .Icon("el-icon-cpu")
            .AddServiceCard("php")
            .AddVersionSwitcher("php")
            .AddConfigEditor("php")
            .AddLogViewer("php")
            .Build();

    /// <summary>Returns extensions for a specific PHP version (major.minor).</summary>
    public async Task<IReadOnlyList<PhpExtension>> GetExtensionsForVersion(string version)
    {
        var php = _installations.FirstOrDefault(p =>
            p.MajorMinor == version || p.Version == version || p.Version.StartsWith(version));
        if (php == null) return Array.Empty<PhpExtension>();
        var extManager = _module?.GetType()
            .GetField("_extensionManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_module) as PhpExtensionManager;
        if (extManager == null) return Array.Empty<PhpExtension>();
        return await extManager.GetExtensionsAsync(php);
    }
}

/// <summary>REST-friendly DTO for a detected PHP version.</summary>
public record PhpVersionInfo(
    string Version,
    string MajorMinor,
    string PhpExePath,
    string? PhpCgiPath,
    string Directory,
    string[] Extensions,
    int FcgiPort,
    bool IsActive
);
