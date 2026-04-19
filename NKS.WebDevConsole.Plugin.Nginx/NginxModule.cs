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

namespace NKS.WebDevConsole.Plugin.Nginx;

public sealed class NginxConfig
{
    /// <summary>NKS WDC managed Nginx binaries root.</summary>
    public string BinariesRoot { get; set; } = Path.Combine(WdcPaths.BinariesRoot, "nginx");

    public string ExecutablePath { get; set; } = "nginx";
    public string ServerRoot { get; set; } = string.Empty;
    public string ConfigFile { get; set; } = "conf/nginx.conf";

    public string VhostsDirectory { get; set; } = Path.Combine(WdcPaths.GeneratedRoot, "nginx", "sites-enabled");
    public string LogDirectory { get; set; } = Path.Combine(WdcPaths.LogsRoot, "nginx");

    public int HttpPort { get; set; } = OperatingSystem.IsWindows() ? 80 : 8080;
    public int HttpsPort { get; set; } = OperatingSystem.IsWindows() ? 443 : 8443;

    /// <summary>
    /// How long <c>nginx -s quit</c> is given to drain in-flight requests
    /// before <see cref="NginxModule.StopAsync"/> falls back to SIGTERM / Kill.
    /// Mirrors <c>ApacheConfig.GracefulTimeoutSecs</c>.
    /// </summary>
    public int GracefulTimeoutSecs { get; set; } = 30;
}

/// <summary>
/// Lightweight abstraction over nginx child-process invocations so unit tests
/// can verify argv construction without actually spawning nginx. The default
/// <see cref="CliWrapNginxProcessRunner"/> uses CliWrap; tests substitute a Moq.
/// </summary>
public interface INginxProcessRunner
{
    /// <summary>
    /// Runs nginx synchronously (wait for exit) and returns exit code + captured
    /// stdout/stderr. Used for <c>-t</c> validation, <c>-s quit</c>, <c>-s reload</c>.
    /// </summary>
    Task<NginxCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken ct);

    /// <summary>
    /// Spawns nginx in foreground and returns the live <see cref="Process"/>
    /// handle. Caller is responsible for registering with DaemonJobObject,
    /// attaching event handlers, and disposing.
    /// </summary>
    Process Spawn(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory);
}

public sealed record NginxCommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class CliWrapNginxProcessRunner : INginxProcessRunner
{
    public async Task<NginxCommandResult> RunAsync(
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
        return new NginxCommandResult(result.ExitCode, result.StandardOutput, result.StandardError);
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
/// IServiceModule implementation for Nginx. Manages process lifecycle, config
/// validation, graceful reload, and (scaffolded) log / metrics surfaces.
/// </summary>
public sealed class NginxModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "nginx";
    public string DisplayName => "Nginx";
    public ServiceType Type => ServiceType.WebServer;

    private readonly ILogger<NginxModule> _logger;
    private readonly NginxConfig _config;
    private readonly INginxProcessRunner _runner;

    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime? _startTime;
    private readonly object _stateLock = new();

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    public NginxModule(ILogger<NginxModule> logger, NginxConfig? config = null, INginxProcessRunner? runner = null)
    {
        _logger = logger;
        _config = config ?? new NginxConfig();
        _runner = runner ?? new CliWrapNginxProcessRunner();
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Nginx scaffold initialized. Binaries root: {Root}. TODO: implement binary discovery + nginx.conf generation.",
            _config.BinariesRoot);

        // Ensure our managed dirs exist so future vhost writes don't fail
        try
        {
            Directory.CreateDirectory(_config.VhostsDirectory);
            Directory.CreateDirectory(_config.LogDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create nginx managed dirs: {Message}", ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Renders the embedded Scriban server-block template for the given site
    /// and writes it to the configured VhostsDirectory. This is the one piece
    /// of the scaffold that is actually wired up — the rest is stubbed.
    /// </summary>
    public async Task GenerateVhostAsync(SiteConfig site, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.VhostsDirectory))
            throw new InvalidOperationException("Nginx module is not initialized (VhostsDirectory is empty).");

        Directory.CreateDirectory(_config.VhostsDirectory);

        var templateContent = await LoadEmbeddedTemplateAsync("nginx-vhost.scriban");
        var template = Scriban.Template.Parse(templateContent);
        if (template.HasErrors)
            throw new InvalidOperationException(
                $"nginx-vhost template parse error: {string.Join(", ", template.Messages)}");

        var model = new
        {
            site = new
            {
                domain = site.Domain,
                aliases = site.Aliases ?? Array.Empty<string>(),
                root = site.DocumentRoot,
            },
            port = site.HttpPort > 0 ? site.HttpPort : _config.HttpPort,
        };

        var result = template.Render(model, m => m.Name);
        var outPath = Path.Combine(_config.VhostsDirectory, $"{site.Domain}.conf");
        await File.WriteAllTextAsync(outPath, result, ct);
        _logger.LogInformation("Generated nginx server block for {Domain} at {Path}", site.Domain, outPath);
    }

    public Task RemoveVhostAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.VhostsDirectory))
            return Task.CompletedTask;

        // Defense-in-depth: domain should already be validated upstream by
        // SiteOrchestrator.ValidateDomain, but an extra containment check
        // prevents File.Delete from escaping VhostsDirectory if a malformed
        // SiteConfig ever reaches this code path (e.g. direct plugin call
        // from a test harness).
        var baseDir = Path.GetFullPath(_config.VhostsDirectory);
        var requestedPath = Path.GetFullPath(Path.Combine(baseDir, $"{domain}.conf"));
        if (!requestedPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Refused to remove nginx vhost outside managed dir — domain='{Domain}' resolved to '{Path}', base '{Base}'",
                domain,
                requestedPath,
                baseDir);
            return Task.CompletedTask;
        }

        if (File.Exists(requestedPath))
        {
            File.Delete(requestedPath);
            _logger.LogInformation("Removed nginx server block for {Domain}", domain);
        }
        return Task.CompletedTask;
    }

    private static async Task<string> LoadEmbeddedTemplateAsync(string name)
    {
        var asm = typeof(NginxModule).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded template not found: {name}");

        await using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Cannot open embedded template: {resourceName}");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    // ── IServiceModule: ValidateConfigAsync ─────────────────────────────────

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        var configPath = ResolveConfigPath();
        var prefix = ResolveManagedPrefix();
        _logger.LogInformation("Validating nginx config: {Path} (prefix={Prefix})", configPath, prefix);

        // `nginx -t -c <path> -p <prefix>` — prefix is needed so nginx looks
        // for relative include directives / log paths under our managed dir
        // instead of the compiled-in default which may not exist on the
        // developer machine.
        var args = new List<string> { "-t", "-c", configPath };
        if (!string.IsNullOrEmpty(prefix))
        {
            args.Add("-p");
            args.Add(prefix);
        }

        var result = await _runner.RunAsync(_config.ExecutablePath, args, _config.ServerRoot, ct);

        // nginx -t writes its "syntax is ok" / "test is successful" banner to
        // stderr on BOTH success and failure paths, so we concat both streams
        // for the user-facing error message.
        var output = (result.StandardError + result.StandardOutput).Trim();

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("Nginx config validation passed");
            return new ValidationResult(true);
        }

        _logger.LogError("Nginx config validation failed (exit={Code}):\n{Output}", result.ExitCode, output);
        return new ValidationResult(false, string.IsNullOrEmpty(output) ? $"nginx -t exited with code {result.ExitCode}" : output);
    }

    // ── IServiceModule: StartAsync ───────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Running or ServiceState.Starting)
                throw new InvalidOperationException($"Nginx is already {_state}.");
            _state = ServiceState.Starting;
        }

        try
        {
            _logger.LogInformation("Starting nginx on port {Port}...", _config.HttpPort);

            // SPEC §9 Port Conflict Detection — raise a diagnostic error before
            // letting nginx fail with a cryptic bind() error.
            var conflict = PortConflictDetector.CheckPort(_config.HttpPort);
            if (conflict is not null)
            {
                var fallback = PortConflictDetector.SuggestFallback(_config.HttpPort);
                var msg = conflict.ToUserMessage(fallback);
                _logger.LogError("Nginx cannot bind: {Msg}", msg);
                throw new InvalidOperationException(msg);
            }

            var prefix = ResolveManagedPrefix();
            var configPath = ResolveConfigPath();
            var args = new List<string> { "-c", configPath };
            if (!string.IsNullOrEmpty(prefix))
            {
                args.Add("-p");
                args.Add(prefix);
            }
            // Run in foreground on Unix so the parent can observe exit; on
            // Windows nginx foregrounds by default when spawned without
            // `-s <signal>`.
            if (!OperatingSystem.IsWindows())
            {
                args.Add("-g");
                args.Add("daemon off;");
            }

            _process = _runner.Spawn(_config.ExecutablePath, args, _config.ServerRoot);
            DaemonJobObject.AssignProcess(_process);

            _startTime = DateTime.UtcNow;
            _logger.LogInformation("Nginx PID {Pid} launched, waiting for port {Port}...",
                _process.Id, _config.HttpPort);

            var ready = await WaitForPortBindAsync(_config.HttpPort, TimeSpan.FromSeconds(15), ct);
            if (!ready)
            {
                lock (_stateLock) _state = ServiceState.Crashed;
                throw new TimeoutException($"Nginx did not bind to port {_config.HttpPort} within 15 seconds.");
            }

            lock (_stateLock) _state = ServiceState.Running;
            _logger.LogInformation("Nginx running (PID={Pid}, port={Port})", _process.Id, _config.HttpPort);
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
            _logger.LogInformation("Nginx stopped");
        }
    }

    private async Task RunGracefulStopAsync(CancellationToken ct)
    {
        // Even without a tracked process handle we still issue `nginx -s quit`
        // so a nginx left over from a previous daemon instance gets a chance
        // to drain. Mirrors `apachectl graceful-stop` behaviour.
        var prefix = ResolveManagedPrefix();
        var configPath = ResolveConfigPath();
        var args = new List<string> { "-s", "quit", "-c", configPath };
        if (!string.IsNullOrEmpty(prefix))
        {
            args.Add("-p");
            args.Add(prefix);
        }

        try
        {
            var result = await _runner.RunAsync(_config.ExecutablePath, args, _config.ServerRoot, ct);
            if (result.ExitCode != 0)
                _logger.LogWarning(
                    "`nginx -s quit` exited {Code}: {Err}",
                    result.ExitCode,
                    result.StandardError.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to issue nginx -s quit: {Message}", ex.Message);
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
                "Nginx did not stop in {Timeout}s — escalating (SIGTERM/Kill) PID {Pid}",
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
                    // Unix: SIGTERM first (polite), then Kill if it lingers.
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
        _logger.LogInformation("Reloading nginx...");

        // Validate first — issuing `-s reload` against a broken config is a
        // guaranteed way to take production down. `nginx -t` is cheap.
        var validation = await ValidateConfigAsync(ct);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Reload aborted — config invalid: {validation.ErrorMessage}");

        var prefix = ResolveManagedPrefix();
        var configPath = ResolveConfigPath();
        var args = new List<string> { "-s", "reload", "-c", configPath };
        if (!string.IsNullOrEmpty(prefix))
        {
            args.Add("-p");
            args.Add(prefix);
        }

        var result = await _runner.RunAsync(_config.ExecutablePath, args, _config.ServerRoot, ct);
        if (result.ExitCode != 0)
        {
            var err = (result.StandardError + result.StandardOutput).Trim();
            throw new InvalidOperationException(
                $"nginx -s reload exited with code {result.ExitCode}: {err}");
        }

        _logger.LogInformation("Nginx reloaded successfully");
    }

    // ── IServiceModule: GetStatusAsync ───────────────────────────────────────

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ServiceState state;
        int? pid;
        lock (_stateLock) { state = _state; pid = _process?.Id; }

        var (cpu, memory) = ProcessMetricsSampler.Sample(_process);
        var uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;

        return Task.FromResult(new ServiceStatus("nginx", "Nginx", state, pid, cpu, memory, uptime));
    }

    // ── IServiceModule: GetLogsAsync ─────────────────────────────────────────

    public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        // TODO: real log tailing via FileSystemWatcher on _config.LogDirectory.
        // Mirror ApacheModule's _logBuffer + _logWatchers once we have a
        // canonical access.log / error.log location.
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string ResolveConfigPath()
    {
        if (Path.IsPathRooted(_config.ConfigFile))
            return _config.ConfigFile;
        return Path.Combine(_config.ServerRoot, _config.ConfigFile);
    }

    /// <summary>
    /// The nginx "-p" prefix dictates where nginx resolves relative include
    /// directives and log paths. We point it at <c>ServerRoot</c> when set,
    /// or the parent of the config file as a sane fallback.
    /// </summary>
    private string ResolveManagedPrefix()
    {
        if (!string.IsNullOrEmpty(_config.ServerRoot) && Directory.Exists(_config.ServerRoot))
            return _config.ServerRoot;

        var cfg = ResolveConfigPath();
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(cfg));
        return parent ?? string.Empty;
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
