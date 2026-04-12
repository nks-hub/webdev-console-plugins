using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.Node;

/// <summary>
/// Per-site Node.js process supervisor. Unlike single-process modules
/// (Apache, Redis) this manages N independent child processes — one per
/// site that has <see cref="SiteConfig.NodeUpstreamPort"/> > 0. Each
/// process is identified by the site domain.
///
/// The module exposes a single <see cref="IServiceModule"/> whose
/// <see cref="ServiceState"/> is an aggregate: Running if any child is
/// running, Stopped if all are stopped, etc. Individual per-site status
/// is available via <see cref="GetSiteStatus"/>.
/// </summary>
public sealed class NodeModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "node";
    public string DisplayName => "Node.js";
    public ServiceType Type => ServiceType.Other;

    private readonly ILogger<NodeModule> _logger;
    private readonly ConcurrentDictionary<string, NodeSiteProcess> _processes = new(StringComparer.OrdinalIgnoreCase);

    private readonly Channel<string> _logChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(4000) { FullMode = BoundedChannelFullMode.DropOldest });

    private string? _nodeExecutable;
    private string? _npmExecutable;

    private const int GracefulTimeoutSecs = 10;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    public NodeModule(ILogger<NodeModule> logger)
    {
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        DetectNodeExecutable();
        return Task.CompletedTask;
    }

    private void DetectNodeExecutable()
    {
        // 1. Check managed binaries under ~/.wdc/binaries/node/
        var nodeRoot = Path.Combine(WdcPaths.BinariesRoot, "node");
        if (Directory.Exists(nodeRoot))
        {
            var exeName = OperatingSystem.IsWindows() ? "node.exe" : "node";
            var npmName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";

            var versionDirs = Directory.GetDirectories(nodeRoot)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderByDescending(d => d, StringComparer.Ordinal);

            foreach (var vdir in versionDirs)
            {
                var candidates = new[]
                {
                    Path.Combine(vdir, exeName),
                    Path.Combine(vdir, "bin", exeName),
                };
                foreach (var c in candidates)
                {
                    if (!File.Exists(c)) continue;
                    _nodeExecutable = c;
                    var dir = Path.GetDirectoryName(c)!;
                    _npmExecutable = Path.Combine(dir, npmName);
                    if (!File.Exists(_npmExecutable))
                        _npmExecutable = null;
                    _logger.LogInformation("Using managed Node.js: {Path}", _nodeExecutable);
                    return;
                }
            }
        }

        // 2. Fall back to PATH (system Node)
        var pathNode = FindInPath(OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (pathNode is not null)
        {
            _nodeExecutable = pathNode;
            _npmExecutable = FindInPath(OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
            _logger.LogInformation("Using system Node.js: {Path}", _nodeExecutable);
            return;
        }

        _logger.LogWarning("Node.js executable not found — install via Binaries page or system PATH");
    }

    private static string? FindInPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathVar.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir.Trim(), exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    // ── IServiceModule: aggregate operations ──────────────────────────

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        if (_nodeExecutable is null)
            return new ValidationResult(false, "Node.js executable not found. Install via Binaries page or add to PATH.");
        return new ValidationResult(true);
    }

    /// <summary>
    /// Start is a no-op at the module level. Per-site processes are
    /// started via <see cref="StartSiteAsync"/> when sites are applied.
    /// </summary>
    public Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Node.js module ready ({Count} site processes tracked)", _processes.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct) => await StopAllAsync(ct);

    public async Task ReloadAsync(CancellationToken ct)
    {
        _logger.LogInformation("Node.js module reload — restarting all site processes");
        foreach (var (domain, proc) in _processes)
        {
            if (proc.State == ServiceState.Running)
            {
                await StopSiteAsync(domain, ct);
                await StartSiteAsync(domain, proc.DocumentRoot, proc.Port, proc.StartCommand, ct);
            }
        }
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        var running = _processes.Values.Count(p => p.State == ServiceState.Running);
        var total = _processes.Count;

        var state = running > 0 ? ServiceState.Running
            : total > 0 && _processes.Values.Any(p => p.State == ServiceState.Crashed) ? ServiceState.Crashed
            : ServiceState.Stopped;

        // Aggregate metrics across all child processes
        double totalCpu = 0;
        long totalMem = 0;
        TimeSpan maxUptime = TimeSpan.Zero;
        int? anyPid = null;

        foreach (var proc in _processes.Values)
        {
            if (proc.Process is null || proc.Process.HasExited) continue;
            anyPid ??= proc.Process.Id;
            var (cpu, mem) = ProcessMetricsSampler.Sample(proc.Process);
            totalCpu += cpu;
            totalMem += mem;
            if (proc.StartTime.HasValue)
            {
                var up = DateTime.UtcNow - proc.StartTime.Value;
                if (up > maxUptime) maxUptime = up;
            }
        }

        var displayName = running > 0 ? $"Node.js ({running}/{total} sites)" : "Node.js";
        return Task.FromResult(new ServiceStatus("node", displayName, state, anyPid, totalCpu, totalMem, maxUptime));
    }

    public async Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        var result = new List<string>(lines);
        var reader = _logChannel.Reader;
        while (result.Count < lines && reader.TryRead(out var line))
            result.Add(line);
        return result;
    }

    // ── Per-site process management ───────────────────────────────────

    /// <summary>
    /// Start or restart the Node.js process for a specific site.
    /// Called by SiteOrchestrator.ApplyAsync when a Node site is saved.
    /// </summary>
    public async Task StartSiteAsync(string domain, string documentRoot, int port, string startCommand, CancellationToken ct)
    {
        if (_nodeExecutable is null)
            throw new InvalidOperationException("Node.js executable not found.");

        // Stop any existing process for this site first
        if (_processes.TryGetValue(domain, out var existing) && existing.State is ServiceState.Running or ServiceState.Starting)
            await StopSiteAsync(domain, ct);

        var cmd = string.IsNullOrWhiteSpace(startCommand) ? "npm start" : startCommand.Trim();
        var siteProc = new NodeSiteProcess
        {
            Domain = domain,
            DocumentRoot = documentRoot,
            Port = port,
            StartCommand = cmd,
            State = ServiceState.Starting,
        };

        _processes[domain] = siteProc;
        PublishLog($"[{domain}] Starting: {cmd} (port {port})");

        try
        {
            var psi = BuildProcessStartInfo(cmd, documentRoot, port);
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    PublishLog($"[{domain}] {e.Data}");
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    PublishLog($"[{domain}] [ERR] {e.Data}");
            };
            process.Exited += (_, _) => OnSiteProcessExited(domain);

            process.Start();
            DaemonJobObject.AssignProcess(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            siteProc.Process = process;
            siteProc.StartTime = DateTime.UtcNow;

            _logger.LogInformation("[{Domain}] Node process PID {Pid} started, waiting for port {Port}...",
                domain, process.Id, port);

            var ready = await WaitForPortAsync(port, TimeSpan.FromSeconds(30), ct);
            if (!ready)
            {
                siteProc.State = ServiceState.Crashed;
                PublishLog($"[{domain}] TIMEOUT: port {port} not ready after 30s");
                _logger.LogWarning("[{Domain}] Node did not bind to port {Port} in 30s", domain, port);
                return;
            }

            siteProc.State = ServiceState.Running;
            PublishLog($"[{domain}] Running (PID={process.Id}, port={port})");
            _logger.LogInformation("[{Domain}] Node running (PID={Pid}, port={Port})", domain, process.Id, port);
        }
        catch (Exception ex)
        {
            siteProc.State = ServiceState.Crashed;
            PublishLog($"[{domain}] FAILED: {ex.Message}");
            _logger.LogError(ex, "[{Domain}] Failed to start Node process", domain);
        }
    }

    /// <summary>Stop the Node.js process for a specific site.</summary>
    public async Task StopSiteAsync(string domain, CancellationToken ct)
    {
        if (!_processes.TryGetValue(domain, out var siteProc))
            return;

        if (siteProc.Process is null || siteProc.Process.HasExited)
        {
            siteProc.State = ServiceState.Stopped;
            siteProc.Process = null;
            return;
        }

        siteProc.State = ServiceState.Stopping;
        PublishLog($"[{domain}] Stopping PID {siteProc.Process.Id}...");

        var pid = siteProc.Process.Id;

        // Graceful: SIGTERM on Unix, taskkill tree on Windows
        if (OperatingSystem.IsWindows())
        {
            // Kill entire process tree (npm spawns node as child)
            try { siteProc.Process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        }
        else
        {
            kill(pid, SIGTERM);
        }

        // Wait with timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(GracefulTimeoutSecs));

        try
        {
            await siteProc.Process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[{Domain}] Node did not stop in {Timeout}s — force-killing",
                domain, GracefulTimeoutSecs);
            if (!siteProc.Process.HasExited)
                siteProc.Process.Kill(entireProcessTree: true);
        }

        siteProc.Process.Dispose();
        siteProc.Process = null;
        siteProc.State = ServiceState.Stopped;
        PublishLog($"[{domain}] Stopped");
        _logger.LogInformation("[{Domain}] Node stopped", domain);
    }

    /// <summary>Stop all tracked site processes.</summary>
    public async Task StopAllAsync(CancellationToken ct)
    {
        foreach (var domain in _processes.Keys.ToList())
            await StopSiteAsync(domain, ct);
    }

    /// <summary>Remove a site from tracking (called when a site is deleted).</summary>
    public async Task RemoveSiteAsync(string domain, CancellationToken ct)
    {
        await StopSiteAsync(domain, ct);
        _processes.TryRemove(domain, out _);
    }

    /// <summary>Get status for a specific site's Node process.</summary>
    public NodeSiteStatus? GetSiteStatus(string domain)
    {
        if (!_processes.TryGetValue(domain, out var proc))
            return null;

        var (cpu, mem) = proc.Process is not null && !proc.Process.HasExited
            ? ProcessMetricsSampler.Sample(proc.Process)
            : (0.0, 0L);

        return new NodeSiteStatus(
            domain, proc.State, proc.Process?.Id,
            proc.Port, proc.StartCommand,
            cpu, mem,
            proc.StartTime.HasValue ? DateTime.UtcNow - proc.StartTime.Value : null);
    }

    /// <summary>List all tracked site processes.</summary>
    public IReadOnlyList<NodeSiteStatus> ListSiteProcesses()
    {
        return _processes.Values.Select(p =>
        {
            var (cpu, mem) = p.Process is not null && !p.Process.HasExited
                ? ProcessMetricsSampler.Sample(p.Process)
                : (0.0, 0L);
            return new NodeSiteStatus(
                p.Domain, p.State, p.Process?.Id,
                p.Port, p.StartCommand,
                cpu, mem,
                p.StartTime.HasValue ? DateTime.UtcNow - p.StartTime.Value : null);
        }).ToList();
    }

    // ── Private helpers ───────────────────────────────────────────────

    private ProcessStartInfo BuildProcessStartInfo(string command, string workingDir, int port)
    {
        // Parse command into executable + args
        // "npm start" → npm(.cmd), "start"
        // "npm run dev" → npm(.cmd), "run dev"
        // "node server.js" → node(.exe), "server.js"
        var parts = command.Split(' ', 2, StringSplitOptions.TrimEntries);
        var exe = parts[0];
        var args = parts.Length > 1 ? parts[1] : "";

        string resolvedExe;
        if (exe.Equals("npm", StringComparison.OrdinalIgnoreCase))
        {
            resolvedExe = _npmExecutable ?? (OperatingSystem.IsWindows() ? "npm.cmd" : "npm");
        }
        else if (exe.Equals("npx", StringComparison.OrdinalIgnoreCase))
        {
            var npxName = OperatingSystem.IsWindows() ? "npx.cmd" : "npx";
            var dir = _npmExecutable is not null ? Path.GetDirectoryName(_npmExecutable) : null;
            resolvedExe = dir is not null ? Path.Combine(dir, npxName) : npxName;
        }
        else if (exe.Equals("node", StringComparison.OrdinalIgnoreCase))
        {
            resolvedExe = _nodeExecutable!;
        }
        else
        {
            // Arbitrary command — run via shell
            if (OperatingSystem.IsWindows())
            {
                args = $"/c {command}";
                resolvedExe = "cmd.exe";
            }
            else
            {
                args = $"-c \"{command.Replace("\"", "\\\"")}\"";
                resolvedExe = "/bin/sh";
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExe,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Inject PORT env var so apps that read process.env.PORT work out of the box
        psi.Environment["PORT"] = port.ToString();
        psi.Environment["NODE_ENV"] = "development";

        // Propagate PATH so child processes can find node_modules/.bin
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (_nodeExecutable is not null)
        {
            var nodeDir = Path.GetDirectoryName(_nodeExecutable)!;
            psi.Environment["PATH"] = nodeDir + (OperatingSystem.IsWindows() ? ";" : ":") + existingPath;
        }

        return psi;
    }

    private void OnSiteProcessExited(string domain)
    {
        if (!_processes.TryGetValue(domain, out var siteProc)) return;

        var exitCode = siteProc.Process?.ExitCode ?? -1;

        if (siteProc.State == ServiceState.Stopping)
            return; // intentional stop

        siteProc.State = ServiceState.Crashed;
        siteProc.RestartCount++;
        PublishLog($"[{domain}] Process exited with code {exitCode} (crash #{siteProc.RestartCount})");
        _logger.LogWarning("[{Domain}] Node process exited with code {ExitCode} (crash #{Count})",
            domain, exitCode, siteProc.RestartCount);
    }

    private void PublishLog(string line)
    {
        _logChannel.Writer.TryWrite($"[{DateTime.UtcNow:HH:mm:ss}] {line}");
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
        foreach (var proc in _processes.Values)
        {
            if (proc.Process is not null && !proc.Process.HasExited)
            {
                try { proc.Process.Kill(entireProcessTree: true); }
                catch { /* best-effort */ }
            }
            proc.Process?.Dispose();
        }
        _processes.Clear();
        _logChannel.Writer.TryComplete();
    }
}

/// <summary>Internal state for a single site's Node.js process.</summary>
internal sealed class NodeSiteProcess
{
    public string Domain { get; set; } = "";
    public string DocumentRoot { get; set; } = "";
    public int Port { get; set; }
    public string StartCommand { get; set; } = "npm start";
    public Process? Process { get; set; }
    public ServiceState State { get; set; } = ServiceState.Stopped;
    public DateTime? StartTime { get; set; }
    public int RestartCount { get; set; }
}

/// <summary>Public status DTO for a single site's Node process.</summary>
public record NodeSiteStatus(
    string Domain,
    ServiceState State,
    int? Pid,
    int Port,
    string StartCommand,
    double CpuPercent,
    long MemoryBytes,
    TimeSpan? Uptime);
