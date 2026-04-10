using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.Caddy;

public sealed class CaddyConfig
{
    public string? ExecutablePath { get; set; }
    public string? CaddyfilePath { get; set; }
    public int AdminPort { get; set; } = 2019;
    public int HttpPort { get; set; } = 2015;
    public int GracefulTimeoutSecs { get; set; } = 10;
}

/// <summary>
/// IServiceModule implementation for Caddy.
/// Manages caddy process lifecycle, Caddyfile generation, health check, and metrics.
/// Parallel to ApachePlugin / MailpitPlugin. Uses the shared ProcessMetricsSampler
/// for delta-based CPU% so metrics line up with other services.
/// </summary>
public sealed class CaddyModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "caddy";
    public string DisplayName => "Caddy";
    public ServiceType Type => ServiceType.WebServer;

    private readonly ILogger<CaddyModule> _logger;
    private readonly CaddyConfig _config;

    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime? _startTime;
    private readonly object _stateLock = new();

    private readonly Channel<string> _logChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    public CaddyModule(ILogger<CaddyModule> logger, CaddyConfig? config = null)
    {
        _logger = logger;
        _config = config ?? new CaddyConfig();
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        DetectCaddyExecutable();
        if (!string.IsNullOrEmpty(_config.ExecutablePath) && File.Exists(_config.ExecutablePath))
            _logger.LogInformation("Using Caddy: {Path}", _config.ExecutablePath);
        else
            _logger.LogWarning("caddy executable not found (expected under %USERPROFILE%/.wdc/binaries/caddy/<version>/)");

        // Ensure default Caddyfile exists
        var caddyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc", "caddy");
        Directory.CreateDirectory(caddyDir);
        _config.CaddyfilePath ??= Path.Combine(caddyDir, "Caddyfile");
        if (!File.Exists(_config.CaddyfilePath))
        {
            File.WriteAllText(_config.CaddyfilePath,
                "# NKS WebDev Console — Caddy default config\n" +
                "{\n" +
                $"    admin localhost:{_config.AdminPort}\n" +
                "    auto_https off\n" +
                "}\n" +
                $":{_config.HttpPort} {{\n" +
                "    respond \"Caddy ready — drop Caddyfile fragments into ~/.wdc/caddy/sites-enabled/*.caddy\"\n" +
                "}\n");
        }

        return Task.CompletedTask;
    }

    private void DetectCaddyExecutable()
    {
        if (!string.IsNullOrEmpty(_config.ExecutablePath) && File.Exists(_config.ExecutablePath))
            return;

        // Only look under NKS WDC managed binaries (NO MAMP / NO system-wide search per user rule)
        var caddyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc", "binaries", "caddy");

        if (!Directory.Exists(caddyRoot)) return;

        var exeName = OperatingSystem.IsWindows() ? "caddy.exe" : "caddy";
        var versionDirs = Directory.GetDirectories(caddyRoot)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .OrderByDescending(d => d, StringComparer.Ordinal);

        foreach (var vdir in versionDirs)
        {
            var candidate = Path.Combine(vdir, exeName);
            if (File.Exists(candidate))
            {
                _config.ExecutablePath = candidate;
                return;
            }
        }
    }

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.ExecutablePath) || !File.Exists(_config.ExecutablePath))
            return new ValidationResult(false, "caddy executable not found");
        if (string.IsNullOrEmpty(_config.CaddyfilePath) || !File.Exists(_config.CaddyfilePath))
            return new ValidationResult(false, "Caddyfile not found");

        // caddy validate --config <file>
        try
        {
            var psi = new ProcessStartInfo(_config.ExecutablePath, $"validate --config \"{_config.CaddyfilePath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                var err = await p.StandardError.ReadToEndAsync(ct);
                return new ValidationResult(false, $"caddy validate: {err.Trim()}");
            }
            return new ValidationResult(true);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"caddy validate threw: {ex.Message}");
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Running or ServiceState.Starting)
                throw new InvalidOperationException($"Caddy is already {_state}.");
            _state = ServiceState.Starting;
        }

        if (string.IsNullOrEmpty(_config.ExecutablePath))
            throw new InvalidOperationException("caddy executable not found.");

        var validation = await ValidateConfigAsync(ct);
        if (!validation.IsValid)
        {
            lock (_stateLock) _state = ServiceState.Stopped;
            throw new InvalidOperationException($"Config validation failed: {validation.ErrorMessage}");
        }

        _logger.LogInformation("Starting Caddy (HTTP:{HttpPort}, Admin:{AdminPort})...",
            _config.HttpPort, _config.AdminPort);

        var psi = BuildStartInfo();
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) PublishLog(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) PublishLog($"[ERR] {e.Data}"); };
        _process.Exited += OnProcessExited;

        _process.Start();
        NKS.WebDevConsole.Core.Services.DaemonJobObject.AssignProcess(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _startTime = DateTime.UtcNow;

        _logger.LogInformation("Caddy PID {Pid} launched, waiting for admin API on port {Port}...",
            _process.Id, _config.AdminPort);

        var ready = await WaitForHttpAsync(_config.AdminPort, "/config/", TimeSpan.FromSeconds(10), ct);
        if (!ready)
        {
            lock (_stateLock) _state = ServiceState.Crashed;
            throw new TimeoutException($"Caddy admin API did not start on port {_config.AdminPort} within 10 seconds.");
        }

        lock (_stateLock) _state = ServiceState.Running;
        _logger.LogInformation("Caddy running (PID={Pid})", _process.Id);
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
        _logger.LogInformation("Caddy stopped");
    }

    private async Task RunGracefulStopAsync(CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
        {
            _process = null;
            return;
        }

        int pid = _process.Id;

        // Caddy supports `caddy stop` via admin API — preferred path
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            await http.PostAsync($"http://localhost:{_config.AdminPort}/stop", null, ct);
        }
        catch
        {
            // Fall back to signal-based stop
            if (!OperatingSystem.IsWindows() && !_process.HasExited)
                kill(pid, SIGTERM);
            else if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_config.GracefulTimeoutSecs));
        try
        {
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Caddy did not stop in {Timeout}s — force-killing PID {Pid}",
                _config.GracefulTimeoutSecs, pid);
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
        _process = null;
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        // Caddy supports hot reload via admin API: POST /load with Caddyfile adapted to JSON,
        // or via CLI `caddy reload`. Using CLI for simplicity.
        if (string.IsNullOrEmpty(_config.ExecutablePath) || string.IsNullOrEmpty(_config.CaddyfilePath))
            return;
        try
        {
            var psi = new ProcessStartInfo(_config.ExecutablePath,
                $"reload --config \"{_config.CaddyfilePath}\" --address localhost:{_config.AdminPort}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                var err = await p.StandardError.ReadToEndAsync(ct);
                _logger.LogWarning("caddy reload failed: {Error}", err.Trim());
            }
            else
            {
                _logger.LogInformation("Caddy reloaded successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "caddy reload threw");
        }
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ServiceState state;
        int? pid;
        lock (_stateLock) { state = _state; pid = _process?.Id; }

        var (cpu, memory) = NKS.WebDevConsole.Core.Services.ProcessMetricsSampler.Sample(_process);
        var uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;

        return Task.FromResult(new ServiceStatus("caddy", "Caddy", state, pid, cpu, memory, uptime));
    }

    public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        var result = new List<string>(lines);
        var reader = _logChannel.Reader;
        while (result.Count < lines && reader.TryRead(out var line))
            result.Add(line);
        return Task.FromResult<IReadOnlyList<string>>(result);
    }

    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"http://localhost:{_config.AdminPort}/config/", ct);
            return response.IsSuccessStatusCode;
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

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogWarning("Caddy process exited with code {ExitCode}", exitCode);

        lock (_stateLock)
        {
            if (_state != ServiceState.Stopping)
                _state = ServiceState.Crashed;
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var args = $"run --config \"{_config.CaddyfilePath}\" --adapter caddyfile";
        return new ProcessStartInfo
        {
            FileName = _config.ExecutablePath!,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    private static async Task<bool> WaitForHttpAsync(int port, string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var response = await http.GetAsync($"http://localhost:{port}{path}", cts.Token);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception) when (!cts.Token.IsCancellationRequested)
            {
                try { await Task.Delay(500, cts.Token); } catch { return false; }
            }
        }
        return false;
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
