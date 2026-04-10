using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.Mailpit;

public sealed class MailpitConfig
{
    public string? ExecutablePath { get; set; }
    public int SmtpPort { get; set; } = 1025;
    public int WebPort { get; set; } = 8025;
    public int GracefulTimeoutSecs { get; set; } = 10;
}

/// <summary>
/// Full IServiceModule implementation for Mailpit.
/// Manages process lifecycle, config validation, log streaming, and metrics.
/// </summary>
public sealed class MailpitModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "mailpit";
    public string DisplayName => "Mailpit";
    public ServiceType Type => ServiceType.MailServer;

    private readonly ILogger<MailpitModule> _logger;
    private readonly MailpitConfig _config;

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

    public MailpitModule(ILogger<MailpitModule> logger, MailpitConfig? config = null)
    {
        _logger = logger;
        _config = config ?? new MailpitConfig();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        DetectMailpitExecutable();

        if (!string.IsNullOrEmpty(_config.ExecutablePath) && File.Exists(_config.ExecutablePath))
        {
            _logger.LogInformation("Using Mailpit: {Path}", _config.ExecutablePath);
            return;
        }

        _logger.LogWarning("mailpit executable not found");
    }

    private void DetectMailpitExecutable()
    {
        if (!string.IsNullOrEmpty(_config.ExecutablePath) && File.Exists(_config.ExecutablePath))
            return;

        // Only look under NKS WDC managed binaries
        var mailpitRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc", "binaries", "mailpit");

        if (!Directory.Exists(mailpitRoot))
            return;

        var exeName = OperatingSystem.IsWindows() ? "mailpit.exe" : "mailpit";

        var versionDirs = Directory.GetDirectories(mailpitRoot)
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

    // -- IServiceModule: ValidateConfigAsync --

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.ExecutablePath) || !File.Exists(_config.ExecutablePath))
            return new ValidationResult(false, "mailpit executable not found");

        return new ValidationResult(true);
    }

    // -- IServiceModule: StartAsync --

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Running or ServiceState.Starting)
                throw new InvalidOperationException($"Mailpit is already {_state}.");
            _state = ServiceState.Starting;
        }

        if (string.IsNullOrEmpty(_config.ExecutablePath))
            throw new InvalidOperationException("mailpit executable not found.");

        var validation = await ValidateConfigAsync(ct);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Config validation failed: {validation.ErrorMessage}");

        _logger.LogInformation("Starting Mailpit (SMTP:{SmtpPort}, Web:{WebPort})...",
            _config.SmtpPort, _config.WebPort);

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
        _logger.LogInformation("Mailpit PID {Pid} launched, waiting for Web UI on port {Port}...",
            _process.Id, _config.WebPort);

        var ready = await WaitForHttpAsync(_config.WebPort, TimeSpan.FromSeconds(15), ct);
        if (!ready)
        {
            lock (_stateLock) _state = ServiceState.Crashed;
            throw new TimeoutException($"Mailpit Web UI did not start on port {_config.WebPort} within 15 seconds.");
        }

        lock (_stateLock) _state = ServiceState.Running;
        _logger.LogInformation("Mailpit running (PID={Pid}, SMTP={SmtpPort}, Web={WebPort})",
            _process.Id, _config.SmtpPort, _config.WebPort);
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
        _logger.LogInformation("Mailpit stopped");
    }

    private async Task RunGracefulStopAsync(CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
        {
            _process = null;
            return;
        }

        int pid = _process.Id;

        // Mailpit has no graceful shutdown command — send SIGTERM on Linux, kill on Windows
        if (!OperatingSystem.IsWindows() && !_process.HasExited)
        {
            kill(pid, SIGTERM);
        }
        else if (!_process.HasExited)
        {
            // On Windows, try to kill gracefully via process tree
            _process.Kill(entireProcessTree: true);
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
            _logger.LogWarning("Mailpit did not stop in {Timeout}s -- force-killing PID {Pid}",
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
        _logger.LogInformation("Mailpit does not support live config reload — restart required");
    }

    // -- IServiceModule: GetStatusAsync --

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ServiceState state;
        int? pid;
        lock (_stateLock) { state = _state; pid = _process?.Id; }

        double cpu = 0;
        long memory = 0;
        TimeSpan uptime = TimeSpan.Zero;

        if (_process is not null && !_process.HasExited)
        {
            try
            {
                _process.Refresh();
                memory = _process.WorkingSet64;
                uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;
                cpu = uptime.TotalMilliseconds > 0
                    ? _process.TotalProcessorTime.TotalMilliseconds / (Environment.ProcessorCount * uptime.TotalMilliseconds) * 100
                    : 0;
            }
            catch { /* process may have exited between check and Refresh() */ }
        }

        return Task.FromResult(new ServiceStatus("mailpit", "Mailpit", state, pid, cpu, memory, uptime));
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

    // -- Health check: HTTP GET /api/v1/info --

    public async Task<bool> CheckHealthAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await http.GetAsync($"http://localhost:{_config.WebPort}/api/v1/info", ct);
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

    // -- Process exit handler --

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogWarning("Mailpit process exited with code {ExitCode}", exitCode);

        lock (_stateLock)
        {
            if (_state != ServiceState.Stopping)
            {
                _state = ServiceState.Crashed;
                _restartCount++;
                _logger.LogError("Mailpit crashed (restart #{Count})", _restartCount);
            }
        }
    }

    // -- Helpers --

    private ProcessStartInfo BuildStartInfo()
    {
        var args = $"--smtp 0.0.0.0:{_config.SmtpPort} --listen 0.0.0.0:{_config.WebPort}";

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

    private static async Task<bool> WaitForHttpAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                var response = await http.GetAsync($"http://localhost:{port}/api/v1/info", cts.Token);
                if (response.IsSuccessStatusCode)
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
