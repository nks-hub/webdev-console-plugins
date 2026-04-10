using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.MySQL;

public sealed class MySqlConfig
{
    /// <summary>Root for NKS WDC managed MySQL installs.</summary>
    public string BinariesRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "binaries", "mysql");

    /// <summary>Where this instance stores its datafiles. Default ~/.wdc/data/mysql.</summary>
    public string DataDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "data", "mysql");

    public string? ExecutablePath { get; set; }
    public string? MysqladminPath { get; set; }
    public string? ConfigFile { get; set; }
    public string LogDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wdc", "logs", "mysql");
    public int Port { get; set; } = 3306;
    public int GracefulTimeoutSecs { get; set; } = 30;

    /// <summary>
    /// Resolves the highest installed MySQL version under BinariesRoot and points
    /// ExecutablePath / MysqladminPath at it. Does NOT touch MAMP or any third-party install.
    /// </summary>
    public bool ApplyOwnBinaryDefaults()
    {
        if (!Directory.Exists(BinariesRoot)) return false;

        var versionDirs = Directory.GetDirectories(BinariesRoot)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderByDescending(d => d, StringComparer.Ordinal)
            .ToList();

        foreach (var vdir in versionDirs)
        {
            var mysqld = Path.Combine(vdir, "bin", "mysqld.exe");
            if (!File.Exists(mysqld)) continue;

            ExecutablePath = mysqld;
            MysqladminPath = Path.Combine(vdir, "bin", "mysqladmin.exe");
            return true;
        }
        return false;
    }
}

/// <summary>
/// Full IServiceModule implementation for MySQL (MAMP).
/// Manages process lifecycle, config validation, log streaming, and metrics.
/// </summary>
public sealed class MySqlModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "mysql";
    public string DisplayName => "MySQL";
    public ServiceType Type => ServiceType.Database;

    private readonly ILogger<MySqlModule> _logger;
    private readonly MySqlConfig _config;

    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime? _startTime;
    private int _restartCount;
    private readonly object _stateLock = new();

    private readonly List<string> _logBuffer = new();
    private readonly object _logLock = new();
    private const int MaxLogEntries = 2000;

    private FileSystemWatcher? _logWatcher;
    private CancellationTokenSource? _watcherCts;
    private readonly ConcurrentDictionary<string, long> _logFilePositions = new();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    public MySqlModule(ILogger<MySqlModule> logger, MySqlConfig? config = null)
    {
        _logger = logger;
        _config = config ?? new MySqlConfig();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_config.LogDirectory);
        Directory.CreateDirectory(_config.DataDir);

        if (_config.ApplyOwnBinaryDefaults())
        {
            _logger.LogInformation("MySQL binary detected: {Path}", _config.ExecutablePath);
            await EnsureDataDirInitializedAsync(ct);
        }
        else
        {
            _logger.LogWarning(
                "No MySQL installed under {Root}. Install with POST /api/binaries/install " +
                "{{ \"app\": \"mysql\", \"version\": \"8.4.8\" }}",
                _config.BinariesRoot);
        }
    }

    /// <summary>
    /// Initialize the MySQL data directory on first start (mysqld --initialize-insecure).
    /// Idempotent: skips if data dir already contains a system tablespace.
    /// </summary>
    private async Task EnsureDataDirInitializedAsync(CancellationToken ct)
    {
        var ibdata = Path.Combine(_config.DataDir, "ibdata1");
        var mysqlSystemDb = Path.Combine(_config.DataDir, "mysql");
        if (File.Exists(ibdata) || Directory.Exists(mysqlSystemDb))
        {
            _logger.LogDebug("MySQL data dir already initialized at {Path}", _config.DataDir);
            return;
        }

        if (string.IsNullOrEmpty(_config.ExecutablePath) || !File.Exists(_config.ExecutablePath))
            return;

        _logger.LogInformation("Initializing MySQL data dir at {Path} (first run)...", _config.DataDir);

        var args = $"--initialize-insecure --datadir=\"{_config.DataDir}\" --console";
        var result = await Cli.Wrap(_config.ExecutablePath)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode == 0)
            _logger.LogInformation("MySQL data dir initialized successfully");
        else
            _logger.LogError("MySQL --initialize-insecure failed (exit {Code}): {Err}",
                result.ExitCode, result.StandardError.Trim());
    }

    // -- IServiceModule: ValidateConfigAsync --

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.ExecutablePath) || !File.Exists(_config.ExecutablePath))
            return new ValidationResult(false, "mysqld executable not found");

        if (!string.IsNullOrEmpty(_config.ConfigFile) && !File.Exists(_config.ConfigFile))
            return new ValidationResult(false, $"Config file not found: {_config.ConfigFile}");

        if (!string.IsNullOrEmpty(_config.DataDir) && !Directory.Exists(_config.DataDir))
            return new ValidationResult(false, $"Data directory not found: {_config.DataDir}");

        return new ValidationResult(true);
    }

    // -- IServiceModule: StartAsync --

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Running or ServiceState.Starting)
                throw new InvalidOperationException($"MySQL is already {_state}.");
            _state = ServiceState.Starting;
        }

        if (string.IsNullOrEmpty(_config.ExecutablePath))
            throw new InvalidOperationException("MySQL executable not found.");

        var validation = await ValidateConfigAsync(ct);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Config validation failed: {validation.ErrorMessage}");

        _logger.LogInformation("Starting MySQL on port {Port}...", _config.Port);

        var psi = BuildStartInfo();
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                PublishLog(e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                PublishLog($"[ERR] {e.Data}");
        };
        _process.Exited += OnProcessExited;

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _startTime = DateTime.UtcNow;
        _logger.LogInformation("MySQL PID {Pid} launched, waiting for port {Port}...",
            _process.Id, _config.Port);

        var ready = await WaitForPortAsync(_config.Port, TimeSpan.FromSeconds(30), ct);
        if (!ready)
        {
            lock (_stateLock) _state = ServiceState.Crashed;
            throw new TimeoutException($"MySQL did not bind to port {_config.Port} within 30 seconds.");
        }

        lock (_stateLock) _state = ServiceState.Running;
        _logger.LogInformation("MySQL running (PID={Pid}, port={Port})", _process.Id, _config.Port);

        StartLogFileWatcher();
    }

    // -- IServiceModule: StopAsync --

    public async Task StopAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state == ServiceState.Stopped) return;
            _state = ServiceState.Stopping;
        }

        StopLogFileWatcher();
        await RunGracefulStopAsync(ct);

        lock (_stateLock) _state = ServiceState.Stopped;
        _logger.LogInformation("MySQL stopped");
    }

    private async Task RunGracefulStopAsync(CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
        {
            _process = null;
            return;
        }

        int pid = _process.Id;

        // Try mysqladmin shutdown first (graceful)
        if (!string.IsNullOrEmpty(_config.MysqladminPath) && File.Exists(_config.MysqladminPath))
        {
            try
            {
                await Cli.Wrap(_config.MysqladminPath)
                    .WithArguments($"-u root --port={_config.Port} shutdown")
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(ct);

                _logger.LogInformation("MySQL shutdown initiated via mysqladmin");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mysqladmin shutdown failed, falling back to process kill");
            }
        }
        else if (!OperatingSystem.IsWindows() && !_process.HasExited)
        {
            kill(pid, SIGTERM);
        }

        // Wait up to GracefulTimeout, then force-kill
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.GracefulTimeoutSecs));

        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("MySQL did not stop in {Timeout}s — force-killing PID {Pid}",
                _config.GracefulTimeoutSecs, pid);

            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
        _process = null;
    }

    // -- IServiceModule: ReloadAsync --

    public async Task ReloadAsync(CancellationToken ct)
    {
        _logger.LogInformation("Reloading MySQL configuration...");

        // mysqladmin reload flushes privileges and reopens log files
        if (!string.IsNullOrEmpty(_config.MysqladminPath) && File.Exists(_config.MysqladminPath))
        {
            var result = await Cli.Wrap(_config.MysqladminPath)
                .WithArguments($"-u root --port={_config.Port} reload")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0)
                throw new InvalidOperationException($"mysqladmin reload failed: {result.StandardError.Trim()}");

            _logger.LogInformation("MySQL reloaded successfully");
        }
        else
        {
            _logger.LogWarning("mysqladmin not available — cannot reload MySQL config without restart");
        }
    }

    // -- IServiceModule: GetStatusAsync --

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ServiceState state;
        int? pid;
        lock (_stateLock) { state = _state; pid = _process?.Id; }

        var (cpu, memory) = NKS.WebDevConsole.Core.Services.ProcessMetricsSampler.Sample(_process);
        var uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;

        return Task.FromResult(new ServiceStatus("mysql", "MySQL", state, pid, cpu, memory, uptime));
    }

    // -- IServiceModule: GetLogsAsync --

    public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        lock (_logLock)
        {
            var skip = Math.Max(0, _logBuffer.Count - lines);
            return Task.FromResult<IReadOnlyList<string>>(
                _logBuffer.Skip(skip).Take(lines).ToList());
        }
    }

    // -- Log streaming via FileSystemWatcher --

    private void StartLogFileWatcher()
    {
        _watcherCts = new CancellationTokenSource();

        var logDir = _config.LogDirectory;
        if (!Directory.Exists(logDir))
            return;

        _logWatcher = new FileSystemWatcher(logDir, "mysql*.log")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        var cts = _watcherCts;
        _logWatcher.Changed += (_, e) =>
        {
            if (cts is null || cts.IsCancellationRequested) return;
            TailLogFile(e.FullPath);
        };
        _logger.LogDebug("MySQL log watcher started on {Dir}", logDir);
    }

    private void TailLogFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _logFilePositions.TryGetValue(path, out var pos);
            if (pos > fs.Length) pos = 0; // file was rotated
            fs.Seek(pos, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                PublishLog($"[{Path.GetFileName(path)}] {line}");
                pos = fs.Position;
            }

            _logFilePositions[path] = pos;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Log tail error for {Path}: {Message}", path, ex.Message);
        }
    }

    private void StopLogFileWatcher()
    {
        _watcherCts?.Cancel();
        if (_logWatcher is not null)
        {
            _logWatcher.EnableRaisingEvents = false;
            _logWatcher.Dispose();
            _logWatcher = null;
        }
    }

    private void PublishLog(string line)
    {
        lock (_logLock)
        {
            _logBuffer.Add(line);
            if (_logBuffer.Count > MaxLogEntries)
                _logBuffer.RemoveAt(0);
        }
    }

    // -- Process exit handler --

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogWarning("MySQL process exited with code {ExitCode}", exitCode);

        lock (_stateLock)
        {
            if (_state != ServiceState.Stopping)
            {
                _state = ServiceState.Crashed;
                _restartCount++;
                _logger.LogError("MySQL crashed (restart #{Count})", _restartCount);
            }
        }
    }

    // -- Helpers --

    private ProcessStartInfo BuildStartInfo()
    {
        var args = $"--port={_config.Port} --datadir=\"{_config.DataDir}\" --console";

        if (!string.IsNullOrEmpty(_config.ConfigFile) && File.Exists(_config.ConfigFile))
            args = $"--defaults-file=\"{_config.ConfigFile}\" " + args;

        return new ProcessStartInfo
        {
            FileName = _config.ExecutablePath!,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
                return true;
            }
            catch (Exception) when (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(500, cts.Token);
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        StopLogFileWatcher();

        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        }

        _process?.Dispose();
        _watcherCts?.Dispose();
    }
}
