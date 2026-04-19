using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
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
/// Lightweight abstraction over mariadb child-process invocations so unit tests
/// can verify argv construction without actually spawning mariadbd. The default
/// <see cref="CliWrapMariaDBProcessRunner"/> uses CliWrap; tests substitute a Moq.
/// </summary>
public interface IMariaDBProcessRunner
{
    /// <summary>
    /// Runs a mariadb binary synchronously (wait for exit) and returns exit
    /// code + captured stdout/stderr. Used for <c>--verbose --help</c>
    /// validation, <c>mariadb-admin shutdown</c>, <c>mariadb-admin reload</c>.
    /// </summary>
    Task<MariaDBCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken ct);

    /// <summary>
    /// Spawns mariadbd in foreground and returns the live <see cref="Process"/>
    /// handle. Caller is responsible for registering with DaemonJobObject,
    /// attaching event handlers, and disposing.
    /// </summary>
    Process Spawn(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory);
}

public sealed record MariaDBCommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class CliWrapMariaDBProcessRunner : IMariaDBProcessRunner
{
    public async Task<MariaDBCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken ct)
    {
        var cmd = Cli.Wrap(executable)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None);

        if (!string.IsNullOrEmpty(workingDirectory))
            cmd = cmd.WithWorkingDirectory(workingDirectory);

        var result = await cmd.ExecuteBufferedAsync(ct);
        return new MariaDBCommandResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    public Process Spawn(string executable, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Start();
        return proc;
    }
}

/// <summary>
/// IServiceModule implementation for MariaDB. Manages process lifecycle, config
/// validation, graceful reload, and (scaffolded) log / metrics surfaces. Mirrors
/// <see cref="NKS.WebDevConsole.Plugin.Nginx.NginxModule"/> — both plugins use
/// the same IProcessRunner + Moq pattern for deterministic unit tests.
/// </summary>
public sealed class MariaDBModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "mariadb";
    public string DisplayName => "MariaDB";
    public ServiceType Type => ServiceType.Database;

    private readonly ILogger<MariaDBModule> _logger;
    private readonly MariaDBConfig _config;
    private readonly IMariaDBProcessRunner _runner;

    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime? _startTime;
    private readonly object _stateLock = new();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    public MariaDBModule(ILogger<MariaDBModule> logger, MariaDBConfig? config = null, IMariaDBProcessRunner? runner = null)
    {
        _logger = logger;
        _config = config ?? new MariaDBConfig();
        _runner = runner ?? new CliWrapMariaDBProcessRunner();
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "MariaDB initialized. Binaries root: {Root}. TODO: implement datadir init via mariadb-install-db, my.cnf generation, DPAPI root password.",
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

    // ── IServiceModule: ValidateConfigAsync ─────────────────────────────────

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        var exe = ResolveDaemonExecutable();
        if (string.IsNullOrEmpty(exe))
            return new ValidationResult(false, "mariadbd/mysqld executable not found");

        var args = new List<string> { "--verbose", "--help" };
        if (!string.IsNullOrEmpty(_config.ConfigFile))
            args.Add($"--defaults-file={_config.ConfigFile}");

        _logger.LogInformation("Validating mariadb config via {Exe} --verbose --help ({Cfg})", exe, _config.ConfigFile);

        var result = await _runner.RunAsync(exe, args, null, ct);

        // mariadbd --verbose --help prints the full help banner to stdout on
        // success (exit 0) and emits parse errors to stderr on failure.
        if (result.ExitCode == 0)
        {
            _logger.LogInformation("MariaDB config validation passed");
            return new ValidationResult(true);
        }

        var output = (result.StandardError + result.StandardOutput).Trim();
        _logger.LogError("MariaDB config validation failed (exit={Code}):\n{Output}", result.ExitCode, output);
        return new ValidationResult(false,
            string.IsNullOrEmpty(output) ? $"mariadbd --verbose --help exited with code {result.ExitCode}" : output);
    }

    // ── IServiceModule: StartAsync ───────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Running or ServiceState.Starting)
                throw new InvalidOperationException($"MariaDB is already {_state}.");
            _state = ServiceState.Starting;
        }

        try
        {
            _logger.LogInformation("Starting MariaDB on port {Port}...", _config.Port);

            // SPEC §9 Port Conflict Detection — raise a diagnostic error before
            // letting mariadbd fail with a cryptic bind() error.
            var conflict = PortConflictDetector.CheckPort(_config.Port);
            if (conflict is not null)
            {
                var fallback = PortConflictDetector.SuggestFallback(_config.Port);
                var msg = conflict.ToUserMessage(fallback);
                _logger.LogError("MariaDB cannot bind: {Msg}", msg);
                throw new InvalidOperationException(msg);
            }

            var exe = ResolveDaemonExecutable()
                ?? throw new InvalidOperationException("MariaDB executable (mariadbd/mysqld) not found.");

            var args = new List<string>();
            if (!string.IsNullOrEmpty(_config.ConfigFile))
                args.Add($"--defaults-file={_config.ConfigFile}");
            args.Add($"--datadir={_config.DataDir}");
            args.Add($"--port={_config.Port}");
            if (OperatingSystem.IsWindows())
                args.Add("--console");

            _process = _runner.Spawn(exe, args, null);
            DaemonJobObject.AssignProcess(_process);

            _startTime = DateTime.UtcNow;
            _logger.LogInformation("MariaDB PID {Pid} launched, waiting for port {Port}...",
                _process.Id, _config.Port);

            // MariaDB startup can be slow on first run (InnoDB redo log init),
            // so we allow up to 30s — generous compared to nginx's 15s.
            var ready = await WaitForPortBindAsync(_config.Port, TimeSpan.FromSeconds(30), ct);
            if (!ready)
            {
                lock (_stateLock) _state = ServiceState.Crashed;
                throw new TimeoutException($"MariaDB did not bind to port {_config.Port} within 30 seconds.");
            }

            lock (_stateLock) _state = ServiceState.Running;
            _logger.LogInformation("MariaDB running (PID={Pid}, port={Port})", _process.Id, _config.Port);

            // TODO(next-iteration): DPAPI root password bootstrap — same pattern as
            // MySqlModule.SetRootPasswordIfNeededAsync. For now we leave root
            // passwordless (or auth-socket on Unix) and expect Stop/Reload to use
            // --skip-password.
        }
        catch
        {
            lock (_stateLock)
            {
                if (_state != ServiceState.Crashed)
                    _state = ServiceState.Stopped;
            }
            throw;
        }
    }

    // ── IServiceModule: StopAsync ────────────────────────────────────────────

    public async Task StopAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state == ServiceState.Stopped) return;
            _state = ServiceState.Stopping;
        }

        try
        {
            await RunGracefulStopAsync(ct);
        }
        finally
        {
            lock (_stateLock) _state = ServiceState.Stopped;
            _logger.LogInformation("MariaDB stopped");
        }
    }

    private async Task RunGracefulStopAsync(CancellationToken ct)
    {
        var admin = ResolveAdminExecutable();
        if (!string.IsNullOrEmpty(admin))
        {
            // TODO(next-iteration): swap --skip-password for DPAPI-stored root
            // password once MariaDBRootPassword bootstrap lands. For the current
            // iteration we rely on MariaDB's default install leaving root
            // passwordless (Unix auth-socket / Windows no-password).
            var args = new List<string>
            {
                $"--port={_config.Port}",
                "-u", "root",
                "--skip-password",
                "shutdown",
            };

            try
            {
                var result = await _runner.RunAsync(admin, args, null, ct);
                if (result.ExitCode != 0)
                    _logger.LogWarning(
                        "`mariadb-admin shutdown` exited {Code}: {Err}",
                        result.ExitCode,
                        result.StandardError.Trim());
                else
                    _logger.LogInformation("MariaDB shutdown initiated via mariadb-admin");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mariadb-admin shutdown threw — will fall back to process kill");
            }
        }
        else
        {
            _logger.LogWarning("mariadb-admin not available — falling back to SIGTERM/Kill");
        }

        if (_process is null || _process.HasExited)
        {
            _process?.Dispose();
            _process = null;
            return;
        }

        var pid = _process.Id;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.GracefulTimeoutSecs));

        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "MariaDB did not stop in {Timeout}s — escalating (SIGTERM/Kill) PID {Pid}",
                _config.GracefulTimeoutSecs,
                pid);

            if (!_process.HasExited)
            {
                if (OperatingSystem.IsWindows())
                {
                    _process.Kill(entireProcessTree: true);
                }
                else
                {
                    kill(pid, SIGTERM);
                    try
                    {
                        using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await _process.WaitForExitAsync(killCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!_process.HasExited)
                            _process.Kill(entireProcessTree: true);
                    }
                }
            }
        }

        _process.Dispose();
        _process = null;
    }

    // ── IServiceModule: ReloadAsync ──────────────────────────────────────────

    public async Task ReloadAsync(CancellationToken ct)
    {
        _logger.LogInformation("Reloading MariaDB configuration...");

        var admin = ResolveAdminExecutable();
        if (string.IsNullOrEmpty(admin))
        {
            _logger.LogWarning("mariadb-admin not available — cannot reload without restart");
            return;
        }

        // `reload` flushes privileges + reopens log files. Non-destructive —
        // no need to pre-validate like nginx's `-s reload` does.
        var args = new List<string>
        {
            $"--port={_config.Port}",
            "-u", "root",
            "--skip-password",
            "reload",
        };

        var result = await _runner.RunAsync(admin, args, null, ct);
        if (result.ExitCode != 0)
        {
            var err = (result.StandardError + result.StandardOutput).Trim();
            throw new InvalidOperationException(
                $"mariadb-admin reload exited with code {result.ExitCode}: {err}");
        }

        _logger.LogInformation("MariaDB reloaded successfully");
    }

    // ── IServiceModule: GetStatusAsync ───────────────────────────────────────

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ServiceState state;
        int? pid;
        lock (_stateLock) { state = _state; pid = _process?.Id; }

        var (cpu, memory) = ProcessMetricsSampler.Sample(_process);
        var uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;

        return Task.FromResult(new ServiceStatus("mariadb", "MariaDB", state, pid, cpu, memory, uptime));
    }

    // ── IServiceModule: GetLogsAsync ─────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        // TODO(next-iteration): log tailing via FileSystemWatcher on
        // _config.LogDirectory — mirror MySqlModule._logBuffer pattern.
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the configured mariadbd/mysqld path. If <see cref="MariaDBConfig.ExecutablePath"/>
    /// is a bare name (e.g. "mariadbd"), returns it as-is so CliWrap / ProcessStartInfo
    /// can resolve it via PATH — this is how the unit tests inject a deterministic
    /// executable name without touching the filesystem.
    /// </summary>
    private string? ResolveDaemonExecutable()
    {
        var configured = _config.ExecutablePath;
        if (string.IsNullOrEmpty(configured)) return null;

        // If caller configured a bare command (no directory part), trust it —
        // PATH resolution happens at spawn time. Tests rely on this to inject
        // "mariadbd" without a real binary.
        if (!Path.IsPathRooted(configured)) return configured;

        if (File.Exists(configured)) return configured;

        // Configured path doesn't exist — try sibling mariadbd/mysqld in same bin dir.
        var binDir = Path.GetDirectoryName(configured);
        if (string.IsNullOrEmpty(binDir)) return null;

        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var mariadbd = Path.Combine(binDir, "mariadbd" + ext);
        if (File.Exists(mariadbd)) return mariadbd;
        var mysqld = Path.Combine(binDir, "mysqld" + ext);
        if (File.Exists(mysqld)) return mysqld;

        return null;
    }

    /// <summary>
    /// Returns the configured mariadb-admin path with same bare-name fallback
    /// semantics as <see cref="ResolveDaemonExecutable"/>.
    /// </summary>
    private string? ResolveAdminExecutable()
    {
        var configured = _config.MariadbAdminPath;
        if (string.IsNullOrEmpty(configured)) return null;
        if (!Path.IsPathRooted(configured)) return configured;
        if (File.Exists(configured)) return configured;

        var binDir = Path.GetDirectoryName(configured);
        if (string.IsNullOrEmpty(binDir)) return null;

        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var mariaAdmin = Path.Combine(binDir, "mariadb-admin" + ext);
        if (File.Exists(mariaAdmin)) return mariaAdmin;
        var mysqlAdmin = Path.Combine(binDir, "mysqladmin" + ext);
        if (File.Exists(mysqlAdmin)) return mysqlAdmin;

        return null;
    }

    private static async Task<bool> WaitForPortBindAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(200);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // not yet listening — back off and retry
            }

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 2000));
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        }
        _process?.Dispose();
        await ValueTask.CompletedTask;
    }
}
