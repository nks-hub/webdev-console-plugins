using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.Cloudflare;

/// <summary>
/// IServiceModule that runs <c>cloudflared</c> to operate a Cloudflare Tunnel.
///
/// Lifecycle:
///   - Start: launches <c>cloudflared tunnel --no-autoupdate run --token [JWT]</c>,
///     waits up to <c>StartupTimeoutSecs</c> for the process to come up and
///     report a registered connection.
///   - Stop: graceful SIGTERM on unix, Process.Kill(entireProcessTree) on Windows
///     (cloudflared doesn't have a dedicated "stop" subcommand).
///   - Health: alive if the child process is running.
///
/// Logs are tapped from stdout/stderr and fed into a bounded channel so the
/// Dashboard log viewer can subscribe via the daemon's SSE bridge.
/// </summary>
public sealed class CloudflareModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "cloudflare";
    public string DisplayName => "Cloudflare Tunnel";
    public ServiceType Type => ServiceType.Other;

    private readonly ILogger<CloudflareModule> _logger;
    private readonly CloudflareConfig _config;

    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime? _startTime;
    private readonly object _stateLock = new();

    private readonly Channel<string> _logChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    public CloudflareModule(ILogger<CloudflareModule> logger, CloudflareConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        DetectCloudflaredExecutable();
        if (!string.IsNullOrEmpty(_config.CloudflaredPath) && File.Exists(_config.CloudflaredPath))
            _logger.LogInformation("Using cloudflared: {Path}", _config.CloudflaredPath);
        else
            _logger.LogWarning(
                "cloudflared executable not found. Expected under "
                + "%USERPROFILE%/.wdc/binaries/cloudflared/<version>/ "
                + "or set CloudflareConfig.CloudflaredPath via /api/cloudflare/config.");

        Directory.CreateDirectory(WdcPaths.CloudflareRoot);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Searches multiple well-known locations for cloudflared so the plugin is
    /// useful without any manual configuration when the user already has the
    /// binary from FlyEnv, a manual download, or the WDC binaries root.
    /// </summary>
    private void DetectCloudflaredExecutable()
    {
        if (!string.IsNullOrEmpty(_config.CloudflaredPath) && File.Exists(_config.CloudflaredPath))
            return;

        var exeName = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        var candidates = new List<string>();

        // 1. NKS WDC managed binaries — highest version dir first
        var wdcRoot = Path.Combine(WdcPaths.BinariesRoot, "cloudflared");
        if (Directory.Exists(wdcRoot))
        {
            var versionDirs = Directory.GetDirectories(wdcRoot)
                .OrderByDescending(d => d, StringComparer.Ordinal);
            foreach (var vdir in versionDirs)
                candidates.Add(Path.Combine(vdir, exeName));
        }

        // 2. FlyEnv bundled cloudflared (common on the author's dev box).
        if (OperatingSystem.IsWindows())
        {
            var flyRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads", "FlyEnv-Data", "app", "cloudflared");
            if (Directory.Exists(flyRoot))
            {
                var versionDirs = Directory.GetDirectories(flyRoot)
                    .OrderByDescending(d => d, StringComparer.Ordinal);
                foreach (var vdir in versionDirs)
                    candidates.Add(Path.Combine(vdir, exeName));
            }
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                _config.CloudflaredPath = c;
                _config.Save();
                return;
            }
        }
    }

    public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.CloudflaredPath) || !File.Exists(_config.CloudflaredPath))
            return Task.FromResult(new ValidationResult(false, "cloudflared executable not found"));
        if (string.IsNullOrWhiteSpace(_config.TunnelToken))
            return Task.FromResult(new ValidationResult(false, "tunnel token (JWT) not configured"));
        return Task.FromResult(new ValidationResult(true));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Running or ServiceState.Starting)
                throw new InvalidOperationException($"Cloudflare Tunnel is already {_state}.");
            _state = ServiceState.Starting;
        }

        try
        {
            var validation = await ValidateConfigAsync(ct);
            if (!validation.IsValid)
                throw new InvalidOperationException($"Config validation failed: {validation.ErrorMessage}");

            _logger.LogInformation("Starting Cloudflare Tunnel (cloudflared)...");

            var psi = BuildStartInfo();
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) PublishLog(e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) PublishLog(e.Data); };
            _process.Exited += OnProcessExited;

            _process.Start();
            DaemonJobObject.AssignProcess(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _startTime = DateTime.UtcNow;

            _logger.LogInformation("cloudflared PID {Pid} launched", _process.Id);

            // Verify process didn't immediately crash (bad token, bad binary, etc).
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(3, Math.Min(_config.StartupTimeoutSecs, 30)));
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited)
                {
                    lock (_stateLock) _state = ServiceState.Crashed;
                    throw new InvalidOperationException(
                        $"cloudflared exited immediately (code {_process.ExitCode}). Check the tunnel token.");
                }
                await Task.Delay(300, ct);
                // After 2s of stable uptime we consider it up — the real "connection
                // ready" signal is in stdout ("Registered tunnel connection") but
                // scraping that would race against the log channel draining.
                if (DateTime.UtcNow - _startTime > TimeSpan.FromSeconds(2))
                    break;
            }

            lock (_stateLock) _state = ServiceState.Running;
            _logger.LogInformation("Cloudflare Tunnel running (PID={Pid})", _process.Id);
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

    public async Task StopAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state == ServiceState.Stopped) return;
            _state = ServiceState.Stopping;
        }
        await RunGracefulStopAsync(ct);
        lock (_stateLock) _state = ServiceState.Stopped;
        _logger.LogInformation("Cloudflare Tunnel stopped");
    }

    private async Task RunGracefulStopAsync(CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
        {
            _process = null;
            return;
        }

        int pid = _process.Id;

        try
        {
            if (!OperatingSystem.IsWindows())
                kill(pid, SIGTERM);
            else
                _process.Kill(entireProcessTree: true);
        }
        catch { /* process may have exited between checks */ }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_process.HasExited)
            {
                _logger.LogWarning("cloudflared did not stop in 10s — force-killing PID {Pid}", pid);
                _process.Kill(entireProcessTree: true);
            }
        }

        _process.Dispose();
        _process = null;
    }

    public Task ReloadAsync(CancellationToken ct)
    {
        // cloudflared does not have an in-process reload; changes to the
        // tunnel configuration are picked up via the /configurations API
        // endpoint we expose in CloudflareApi, which the daemon pushes
        // straight to Cloudflare's edge — no process restart required.
        _logger.LogInformation("cloudflared reload is a no-op (config changes go through Cloudflare API)");
        return Task.CompletedTask;
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ServiceState state;
        int? pid;
        lock (_stateLock) { state = _state; pid = _process?.Id; }

        var (cpu, memory) = ProcessMetricsSampler.Sample(_process);
        var uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;

        return Task.FromResult(new ServiceStatus("cloudflare", "Cloudflare Tunnel", state, pid, cpu, memory, uptime));
    }

    public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        var result = new List<string>(lines);
        var reader = _logChannel.Reader;
        while (result.Count < lines && reader.TryRead(out var line))
            result.Add(line);
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        // "Healthy" = process alive and not crashed. cloudflared self-heals
        // network flaps internally so we don't need to probe the edge.
        bool alive;
        lock (_stateLock)
        {
            alive = _state == ServiceState.Running && _process is not null && !_process.HasExited;
        }
        return Task.FromResult(alive);
    }

    private void PublishLog(string line)
    {
        _logChannel.Writer.TryWrite(line);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogWarning("cloudflared exited with code {ExitCode}", exitCode);

        lock (_stateLock)
        {
            if (_state != ServiceState.Stopping)
                _state = ServiceState.Crashed;
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        // --no-autoupdate because the WDC daemon manages binary versions via
        // the Binaries page. Letting cloudflared auto-update would race with
        // the daemon's own version tracking.
        var args = $"tunnel --no-autoupdate run --token {_config.TunnelToken}";
        return new ProcessStartInfo
        {
            FileName = _config.CloudflaredPath!,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null && !_process.HasExited)
        {
            try { await RunGracefulStopAsync(CancellationToken.None); }
            catch { /* best-effort */ }
        }
    }
}
