using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.Redis;

public sealed class RedisConfig
{
    public string? ExecutablePath { get; set; }
    public string? CliPath { get; set; }
    public int Port { get; set; } = 6379;
    public string? MaxMemory { get; set; }
    public string? DataDir { get; set; }
    public int GracefulTimeoutSecs { get; set; } = 10;
}

/// <summary>
/// Full IServiceModule implementation for Redis.
/// Manages process lifecycle, config validation, log streaming, and metrics.
/// </summary>
public sealed class RedisModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "redis";
    public string DisplayName => "Redis";
    public ServiceType Type => ServiceType.Cache;

    private readonly ILogger<RedisModule> _logger;
    private readonly RedisConfig _config;

    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime? _startTime;
    private int _restartCount;
    private readonly object _stateLock = new();

    private readonly Channel<string> _logChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    public RedisModule(ILogger<RedisModule> logger, RedisConfig? config = null)
    {
        _logger = logger;
        _config = config ?? new RedisConfig();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        DetectRedisExecutable();

        if (!string.IsNullOrEmpty(_config.ExecutablePath) && File.Exists(_config.ExecutablePath))
        {
            _logger.LogInformation("Using Redis: {Path}", _config.ExecutablePath);
            return;
        }

        _logger.LogWarning("redis-server executable not found");
    }

    private void DetectRedisExecutable()
    {
        if (!string.IsNullOrEmpty(_config.ExecutablePath) && File.Exists(_config.ExecutablePath))
            return;

        // Only look under NKS WDC managed binaries
        var redisRoot = Path.Combine(WdcPaths.BinariesRoot, "redis");

        if (!Directory.Exists(redisRoot))
            return;

        var exeName = OperatingSystem.IsWindows() ? "redis-server.exe" : "redis-server";
        var cliName = OperatingSystem.IsWindows() ? "redis-cli.exe" : "redis-cli";

        // Pick the highest installed version
        var versionDirs = Directory.GetDirectories(redisRoot)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderByDescending(d => d, StringComparer.Ordinal);

        foreach (var vdir in versionDirs)
        {
            // redis-windows archives: redis-server.exe at root or in bin/
            var candidates = new[]
            {
                Path.Combine(vdir, exeName),
                Path.Combine(vdir, "bin", exeName),
            };

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                _config.ExecutablePath = candidate;
                var dir = Path.GetDirectoryName(candidate)!;
                _config.CliPath ??= Path.Combine(dir, cliName);
                _config.DataDir ??= Path.Combine(WdcPaths.DataRoot, "redis");
                Directory.CreateDirectory(_config.DataDir);
                return;
            }
        }
    }

    // -- IServiceModule: ValidateConfigAsync --

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.ExecutablePath) || !File.Exists(_config.ExecutablePath))
            return new ValidationResult(false, "redis-server executable not found");

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
                throw new InvalidOperationException($"Redis is already {_state}.");
            _state = ServiceState.Starting;
        }

        // Envelope: every early throw must reset _state so the Dashboard toggle
        // doesn't get stuck in Starting. Same class of fix as Caddy/Apache/MySQL.
        try
        {
            if (string.IsNullOrEmpty(_config.ExecutablePath))
                throw new InvalidOperationException("redis-server executable not found.");

            var validation = await ValidateConfigAsync(ct);
            if (!validation.IsValid)
                throw new InvalidOperationException($"Config validation failed: {validation.ErrorMessage}");

            _logger.LogInformation("Starting Redis on port {Port}...", _config.Port);

            // Terminate orphaned redis-server.exe processes from a prior daemon
            // run so they don't steal port 6379 from the fresh spawn. Only our
            // managed binary is affected — system Redis installs are untouched.
            KillOrphanedProcesses();

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
            NKS.WebDevConsole.Core.Services.DaemonJobObject.AssignProcess(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            _startTime = DateTime.UtcNow;
            _logger.LogInformation("Redis PID {Pid} launched, waiting for port {Port}...",
                _process.Id, _config.Port);

            var ready = await WaitForPortAsync(_config.Port, TimeSpan.FromSeconds(15), ct);
            if (!ready)
            {
                lock (_stateLock) _state = ServiceState.Crashed;
                throw new TimeoutException($"Redis did not bind to port {_config.Port} within 15 seconds.");
            }

            lock (_stateLock) _state = ServiceState.Running;
            _logger.LogInformation("Redis running (PID={Pid}, port={Port})", _process.Id, _config.Port);
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

    // -- IServiceModule: StopAsync --

    public async Task StopAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state == ServiceState.Stopped) return;
            _state = ServiceState.Stopping;
        }

        await RunGracefulStopAsync(ct);

        lock (_stateLock) _state = ServiceState.Stopped;
        _logger.LogInformation("Redis stopped");
    }

    private async Task RunGracefulStopAsync(CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
        {
            _process = null;
            return;
        }

        int pid = _process.Id;

        // Try redis-cli SHUTDOWN first (graceful)
        if (!string.IsNullOrEmpty(_config.CliPath) && File.Exists(_config.CliPath))
        {
            try
            {
                await Cli.Wrap(_config.CliPath)
                    .WithArguments($"-p {_config.Port} SHUTDOWN NOSAVE")
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(ct);

                _logger.LogInformation("Redis shutdown initiated via redis-cli");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "redis-cli SHUTDOWN failed, falling back to process kill");
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
            _logger.LogWarning("Redis did not stop in {Timeout}s -- force-killing PID {Pid}",
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
        _logger.LogInformation("Redis does not support live config reload via this module — restart required");
    }

    // -- IServiceModule: GetStatusAsync --

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ServiceState state;
        int? pid;
        lock (_stateLock) { state = _state; pid = _process?.Id; }

        var (cpu, memory) = NKS.WebDevConsole.Core.Services.ProcessMetricsSampler.Sample(_process);
        var uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;

        return Task.FromResult(new ServiceStatus("redis", "Redis", state, pid, cpu, memory, uptime));
    }

    // -- IServiceModule: GetLogsAsync --

    public async Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        var result = new List<string>(lines);
        var reader = _logChannel.Reader;

        while (result.Count < lines && reader.TryRead(out var line))
            result.Add(line);

        return result;
    }

    // -- Health check: TCP PING/PONG --

    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", _config.Port, ct);
            var stream = tcp.GetStream();
            stream.ReadTimeout = 3000;
            stream.WriteTimeout = 3000;

            var pingCmd = Encoding.UTF8.GetBytes("PING\r\n");
            await stream.WriteAsync(pingCmd, ct);

            var buffer = new byte[64];
            var bytesRead = await stream.ReadAsync(buffer, ct);
            var response = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

            return response.Contains("PONG", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void PublishLog(string line)
    {
        _logChannel.Writer.TryWrite(line);
    }

    /// <summary>
    /// Finds and terminates any orphaned redis-server.exe processes that
    /// would hold port <see cref="_config.Port"/> and starve our fresh spawn.
    /// Uses two strategies, whichever finds something:
    ///   1. Match by executable path via <c>Process.MainModule</c>.
    ///   2. Match by TCP listener on the target port via PowerShell
    ///      <c>Get-NetTCPConnection</c>, verified against <c>redis-server</c>
    ///      process name. This catches orphans that MainModule couldn't read
    ///      due to cross-user / permission quirks on Windows.
    /// System-installed Redis on a different port is untouched.
    /// </summary>
    private void KillOrphanedProcesses()
    {
        if (string.IsNullOrEmpty(_config.ExecutablePath)) return;

        // Strategy 1: executable-path match (fast, no shell).
        try
        {
            foreach (var proc in Process.GetProcessesByName("redis-server"))
            {
                try
                {
                    var exePath = proc.MainModule?.FileName;
                    if (exePath is not null && string.Equals(exePath, _config.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Killing orphaned redis-server.exe PID {Pid} (path-match) from previous run", proc.Id);
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(2000);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "path-match orphan kill skipped for PID {Pid}", proc.Id);
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Process enumeration for redis-server failed");
        }

        // Strategy 2: port-holder match via PowerShell.
        var pid = FindPidHoldingTcpPort(_config.Port);
        if (pid <= 0) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.ProcessName.Equals("redis-server", StringComparison.OrdinalIgnoreCase)) return;
            _logger.LogWarning("Killing orphan PID {Pid} holding port {Port} (name-match {Name})",
                pid, _config.Port, proc.ProcessName);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "port-holder orphan kill skipped for PID {Pid}", pid);
        }
    }

    /// <summary>
    /// Returns the PID of the process currently listening on the given TCP port,
    /// or -1 if none found / PowerShell unavailable. Uses Get-NetTCPConnection
    /// which ships with every Windows 10+ system.
    /// </summary>
    private static int FindPidHoldingTcpPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty OwningProcess\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return int.TryParse(output, out var pid) ? pid : -1;
        }
        catch { return -1; }
    }

    // -- Process exit handler --

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogWarning("Redis process exited with code {ExitCode}", exitCode);

        lock (_stateLock)
        {
            if (_state != ServiceState.Stopping)
            {
                _state = ServiceState.Crashed;
                _restartCount++;
                _logger.LogError("Redis crashed (restart #{Count})", _restartCount);
            }
        }
    }

    // -- Helpers --

    private ProcessStartInfo BuildStartInfo()
    {
        var args = new StringBuilder();
        args.Append($"--port {_config.Port}");
        args.Append(" --daemonize no");

        if (!string.IsNullOrEmpty(_config.MaxMemory))
            args.Append($" --maxmemory {_config.MaxMemory}");

        if (!string.IsNullOrEmpty(_config.DataDir))
            args.Append($" --dir \"{_config.DataDir}\"");

        return new ProcessStartInfo
        {
            FileName = _config.ExecutablePath!,
            Arguments = args.ToString(),
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
                using var tcp = new TcpClient();
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
        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        }

        _process?.Dispose();
        _logChannel.Writer.TryComplete();
    }
}
