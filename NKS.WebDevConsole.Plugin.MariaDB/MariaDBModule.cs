using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.MariaDB;

/// <summary>
/// Configuration shape for the MariaDB service module. Mirrors <c>MySqlConfig</c>
/// field shape (BinariesRoot, DataDir, ExecutablePath, ConfigFile, LogDirectory,
/// Port, GracefulTimeoutSecs) so both plugins stay drop-in compatible.
/// </summary>
public sealed class MariaDBConfig
{
    /// <summary>Root for NKS WDC managed MariaDB installs.</summary>
    public string BinariesRoot { get; set; } = Path.Combine(WdcPaths.BinariesRoot, "mariadb");

    /// <summary>Where this instance stores its datafiles. Default ~/.wdc/data/mariadb.</summary>
    public string DataDir { get; set; } = Path.Combine(WdcPaths.DataRoot, "mariadb");

    public string? ExecutablePath { get; set; }
    public string? MariadbAdminPath { get; set; }
    public string? ConfigFile { get; set; }
    public string LogDirectory { get; set; } = Path.Combine(WdcPaths.LogsRoot, "mariadb");
    public int Port { get; set; } = 3306;
    public int GracefulTimeoutSecs { get; set; } = 30;

    /// <summary>
    /// Resolves the highest installed MariaDB version under BinariesRoot and points
    /// ExecutablePath / MariadbAdminPath at it. Does NOT touch MAMP or any third-party install.
    /// </summary>
    public bool ApplyOwnBinaryDefaults()
    {
        if (!Directory.Exists(BinariesRoot)) return false;

        var versionDirs = Directory.GetDirectories(BinariesRoot)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderByDescending(d => Path.GetFileName(d), SemverVersionComparer.Instance)
            .ToList();

        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var vdir in versionDirs)
        {
            // MariaDB 10.5+ ships `mariadbd` as the canonical daemon name. Older
            // releases also keep a `mysqld` symlink — prefer the native name.
            var mariadbd = Path.Combine(vdir, "bin", "mariadbd" + ext);
            var mysqldCompat = Path.Combine(vdir, "bin", "mysqld" + ext);

            string? daemon = File.Exists(mariadbd) ? mariadbd
                           : File.Exists(mysqldCompat) ? mysqldCompat
                           : null;
            if (daemon is null) continue;

            ExecutablePath = daemon;

            var mariaAdmin = Path.Combine(vdir, "bin", "mariadb-admin" + ext);
            var mysqlAdminCompat = Path.Combine(vdir, "bin", "mysqladmin" + ext);
            MariadbAdminPath = File.Exists(mariaAdmin) ? mariaAdmin
                             : File.Exists(mysqlAdminCompat) ? mysqlAdminCompat
                             : null;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Scaffold IServiceModule for MariaDB. Lifecycle methods (Start/Stop/Reload/Validate)
/// are stubbed and throw NotImplementedException — full implementation is tracked
/// as the next iteration of the mariadb plugin. Pattern mirrors NginxModule.
/// </summary>
public sealed class MariaDBModule : IServiceModule
{
    public string ServiceId => "mariadb";
    public string DisplayName => "MariaDB";
    public ServiceType Type => ServiceType.Database;

    private readonly ILogger<MariaDBModule> _logger;
    private readonly MariaDBConfig _config;

    public MariaDBModule(ILogger<MariaDBModule> logger, MariaDBConfig? config = null)
    {
        _logger = logger;
        _config = config ?? new MariaDBConfig();
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "MariaDB scaffold initialized. Binaries root: {Root}. TODO: implement datadir init via mariadb-install-db, my.cnf generation, DPAPI root password.",
            _config.BinariesRoot);

        // Ensure our managed dirs exist so future writes don't fail
        try
        {
            Directory.CreateDirectory(Path.Combine(WdcPaths.GeneratedRoot, "mariadb"));
            Directory.CreateDirectory(_config.LogDirectory);
            Directory.CreateDirectory(_config.DataDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create mariadb managed dirs: {Message}", ex.Message);
        }

        // Best-effort binary discovery — surfaces in logs so the user can tell
        // whether the marketplace install succeeded without starting the service.
        if (_config.ApplyOwnBinaryDefaults())
            _logger.LogInformation("MariaDB binary detected: {Path}", _config.ExecutablePath);
        else
            _logger.LogInformation(
                "No MariaDB installed under {Root}. Install via POST /api/binaries/install {{ \"app\": \"mariadb\", \"version\": \"12.3.1\" }}",
                _config.BinariesRoot);

        return Task.CompletedTask;
    }

    // ── IServiceModule — stubbed lifecycle ───────────────────────────────────
    // TODO(next-iteration): implement process management mirroring MySqlModule,
    // datadir init via `mariadb-install-db`, graceful shutdown via
    // `mariadb-admin shutdown`, log tailing, metrics sampling, and DPAPI-protected
    // root password bootstrap.

    public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        _logger.LogWarning("MariaDBModule.ValidateConfigAsync — not yet implemented (scaffold)");
        throw new NotImplementedException("MariaDB config validation is not yet implemented. TODO: shell out to `mariadbd --verbose --help --defaults-file=<path>` and parse exit code.");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _logger.LogWarning("MariaDBModule.StartAsync — not yet implemented (scaffold)");
        throw new NotImplementedException("MariaDB start is not yet implemented. TODO: launch `mariadbd --defaults-file=<path> --datadir=<dir>`, wait for port bind, register with DaemonJobObject, apply DPAPI root password on first run.");
    }

    public Task StopAsync(CancellationToken ct)
    {
        _logger.LogWarning("MariaDBModule.StopAsync — not yet implemented (scaffold)");
        throw new NotImplementedException("MariaDB stop is not yet implemented. TODO: graceful `mariadb-admin shutdown -u root -p<dpapi>`, fall back to SIGTERM / Kill after GracefulTimeoutSecs.");
    }

    public Task ReloadAsync(CancellationToken ct)
    {
        _logger.LogWarning("MariaDBModule.ReloadAsync — not yet implemented (scaffold)");
        throw new NotImplementedException("MariaDB reload is not yet implemented. TODO: `mariadb-admin reload` (flush privileges + reopen log files), same envelope as MySqlModule.ReloadAsync.");
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        // Non-throwing stub: reporting Stopped is safer than throwing here because
        // the daemon's status loop polls every few seconds and a throw would spam
        // the logs.
        var status = new ServiceStatus("mariadb", "MariaDB", ServiceState.Stopped, null, 0, 0, TimeSpan.Zero);
        return Task.FromResult(status);
    }

    public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        // Non-throwing stub: empty log list until log tailing is implemented.
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
