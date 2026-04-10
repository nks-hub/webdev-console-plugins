using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.Apache;

public sealed class ApacheConfig
{
    /// <summary>
    /// MAMP default installation root. Used as fallback when no explicit paths are set.
    /// </summary>
    public string MampRoot { get; set; } = @"C:\MAMP";

    public string ExecutablePath { get; set; } = "httpd";
    public string ServerRoot { get; set; } = string.Empty;
    public string ConfigFile { get; set; } = "conf/httpd.conf";
    public string VhostsDirectory { get; set; } = "conf/sites-enabled";
    public string LogDirectory { get; set; } = "logs";
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public int GracefulTimeoutSecs { get; set; } = 30;

    /// <summary>
    /// Applies MAMP defaults when ServerRoot/ExecutablePath are not explicitly configured.
    /// </summary>
    public void ApplyMampDefaults()
    {
        if (!Directory.Exists(MampRoot))
            return;

        var mampHttpd = Path.Combine(MampRoot, "bin", "apache", "bin", "httpd.exe");
        if (File.Exists(mampHttpd) && (ExecutablePath == "httpd" || string.IsNullOrEmpty(ExecutablePath)))
            ExecutablePath = mampHttpd;

        var mampServerRoot = Path.Combine(MampRoot, "bin", "apache");
        if (Directory.Exists(mampServerRoot) && string.IsNullOrEmpty(ServerRoot))
            ServerRoot = mampServerRoot;

        var mampConfDir = Path.Combine(MampRoot, "conf", "apache");
        if (Directory.Exists(mampConfDir) && ConfigFile == "conf/httpd.conf")
            ConfigFile = Path.Combine(mampConfDir, "httpd.conf");

        var mampLogDir = Path.Combine(MampRoot, "logs");
        if (Directory.Exists(mampLogDir) && LogDirectory == "logs")
            LogDirectory = mampLogDir;

        var sitesDir = Path.Combine(mampConfDir, "sites-enabled");
        if (VhostsDirectory == "conf/sites-enabled")
            VhostsDirectory = sitesDir;
    }
}

/// <summary>
/// Full IServiceModule implementation for Apache httpd.
/// Manages process lifecycle, config validation, log streaming, and metrics.
/// </summary>
public sealed class ApacheModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "apache";
    public string DisplayName => "Apache HTTP Server";
    public ServiceType Type => ServiceType.WebServer;

    private readonly ApacheVersionManager _versionManager;
    private readonly ApacheConfigGenerator _configGen;
    private readonly ApacheHealthChecker _healthChecker;
    private readonly ILogger<ApacheModule> _logger;
    private readonly ApacheConfig _config;

    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime? _startTime;
    private int _restartCount;
    private readonly object _stateLock = new();

    // Bounded ring-buffer for log lines — drops oldest when full
    private readonly Channel<string> _logChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly List<FileSystemWatcher> _logWatchers = new();
    private CancellationTokenSource? _watcherCts;

#if WINDOWS
    private nint _jobHandle = nint.Zero;
#endif

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGTERM = 15;

    public ApacheModule(
        ApacheVersionManager versionManager,
        ApacheConfigGenerator configGen,
        ApacheHealthChecker healthChecker,
        ILogger<ApacheModule> logger,
        ApacheConfig? config = null)
    {
        _versionManager = versionManager;
        _configGen = configGen;
        _healthChecker = healthChecker;
        _logger = logger;
        _config = config ?? new ApacheConfig();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        // Try MAMP defaults first (most common local dev setup on Windows)
        if (OperatingSystem.IsWindows())
        {
            _config.ApplyMampDefaults();
            if (_config.ExecutablePath != "httpd" && File.Exists(_config.ExecutablePath))
            {
                _logger.LogInformation("Using MAMP Apache: {Path}", _config.ExecutablePath);

                // Ensure vhosts directory exists
                if (!string.IsNullOrEmpty(_config.VhostsDirectory))
                    Directory.CreateDirectory(_config.VhostsDirectory);

                return;
            }
        }

        // Fall back to auto-detection via ApacheVersionManager
        if (string.IsNullOrEmpty(_config.ExecutablePath) || _config.ExecutablePath == "httpd")
        {
            var installations = await _versionManager.DetectAllAsync(
                AppContext.BaseDirectory, ct);

            if (installations.Count > 0)
            {
                _config.ExecutablePath = installations[0].ExecutablePath;
                if (string.IsNullOrEmpty(_config.ServerRoot))
                    _config.ServerRoot = installations[0].ServerRoot;
                _logger.LogInformation("Auto-detected Apache: {Path} v{Version}",
                    _config.ExecutablePath, installations[0].Version);
            }
        }
    }

    // ── IServiceModule: ValidateConfigAsync ─────────────────────────────────

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        var configPath = ResolveConfigPath();
        _logger.LogInformation("Validating Apache config: {Path}", configPath);

        var result = await Cli.Wrap(_config.ExecutablePath)
            .WithArguments(["-t", "-f", configPath])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        // httpd -t writes to stderr even on success ("Syntax OK")
        var output = result.StandardError + result.StandardOutput;

        if (result.ExitCode == 0 && output.Contains("Syntax OK", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Apache config validation passed");
            return new ValidationResult(true);
        }

        _logger.LogError("Apache config validation failed:\n{Output}", output);
        return new ValidationResult(false, output.Trim());
    }

    // ── IServiceModule: StartAsync ───────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Running or ServiceState.Starting)
                throw new InvalidOperationException($"Apache is already {_state}.");
            _state = ServiceState.Starting;
        }

        _logger.LogInformation("Starting Apache on port {Port}...", _config.HttpPort);

        var validation = await ValidateConfigAsync(ct);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Config validation failed: {validation.ErrorMessage}");

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

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            _jobHandle = JobObjects.CreateKillOnCloseJob();
            JobObjects.AssignProcess(_jobHandle, _process);
        }
#endif

        _startTime = DateTime.UtcNow;
        _logger.LogInformation("Apache PID {Pid} launched, waiting for port {Port}...",
            _process.Id, _config.HttpPort);

        var ready = await _healthChecker.WaitForReadyAsync(
            _config.HttpPort,
            TimeSpan.FromSeconds(15),
            ct);

        if (!ready)
        {
            lock (_stateLock) _state = ServiceState.Crashed;
            throw new TimeoutException($"Apache did not bind to port {_config.HttpPort} within 15 seconds.");
        }

        lock (_stateLock) _state = ServiceState.Running;
        _logger.LogInformation("Apache running (PID={Pid}, port={Port})",
            _process.Id, _config.HttpPort);

        StartLogFileWatchers();
    }

    // ── IServiceModule: StopAsync ────────────────────────────────────────────

    public async Task StopAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state == ServiceState.Stopped) return;
            _state = ServiceState.Stopping;
        }

        StopLogFileWatchers();
        await RunGracefulStopAsync(ct);

        lock (_stateLock) _state = ServiceState.Stopped;
        _logger.LogInformation("Apache stopped");
    }

    private async Task RunGracefulStopAsync(CancellationToken ct)
    {
        if (_process is null || _process.HasExited)
        {
            _process = null;
            return;
        }

        int pid = _process.Id;

        if (OperatingSystem.IsWindows())
        {
            // Apache Windows: httpd -k stop sends WM_CLOSE to the parent process
            try
            {
                await Cli.Wrap(_config.ExecutablePath)
                    .WithArguments(["-k", "stop"])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("httpd -k stop failed: {Message}", ex.Message);
            }
        }
        else
        {
            // Unix: apachectl graceful-stop (waits for in-flight requests)
            try
            {
                await Cli.Wrap("apachectl")
                    .WithArguments(["graceful-stop"])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync(ct);
            }
            catch
            {
                // Fall back to SIGTERM on the master process
                if (!_process.HasExited)
                    kill(pid, SIGTERM);
            }
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
            _logger.LogWarning("Apache did not stop in {Timeout}s — force-killing PID {Pid}",
                _config.GracefulTimeoutSecs, pid);

            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
        _process = null;
    }

    // ── IServiceModule: ReloadAsync ──────────────────────────────────────────

    public async Task ReloadAsync(CancellationToken ct)
    {
        _logger.LogInformation("Graceful reload of Apache...");

        var validation = await ValidateConfigAsync(ct);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Reload aborted — config invalid: {validation.ErrorMessage}");

        if (OperatingSystem.IsWindows())
        {
            await Cli.Wrap(_config.ExecutablePath)
                .WithArguments(["-k", "restart"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
        }
        else
        {
            await Cli.Wrap(_config.ExecutablePath)
                .WithArguments(["-k", "graceful"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
        }

        _logger.LogInformation("Apache reloaded successfully");
    }

    // ── IServiceModule: GetStatusAsync ───────────────────────────────────────

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
                // CPU % requires two samples; use TotalProcessorTime delta in production
                cpu = _process.TotalProcessorTime.TotalMilliseconds /
                      (Environment.ProcessorCount * uptime.TotalMilliseconds) * 100;
            }
            catch { /* process may have exited between check and Refresh() */ }
        }

        return Task.FromResult(new ServiceStatus("apache", "Apache HTTP Server", state, pid, cpu, memory, uptime));
    }

    // ── IServiceModule: GetLogsAsync ─────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        var result = new List<string>(lines);
        var reader = _logChannel.Reader;

        // Drain up to `lines` entries without blocking
        while (result.Count < lines && reader.TryRead(out var line))
            result.Add(line);

        return result;
    }

    // ── Log streaming via FileSystemWatcher on log files ─────────────────────

    private void StartLogFileWatchers()
    {
        _watcherCts = new CancellationTokenSource();
        var logDir = Path.IsPathRooted(_config.LogDirectory)
            ? _config.LogDirectory
            : Path.Combine(_config.ServerRoot, _config.LogDirectory);

        if (!Directory.Exists(logDir))
            return;

        foreach (var pattern in new[] { "*-access.log", "*-error.log", "access.log", "error.log" })
        {
            var watcher = new FileSystemWatcher(logDir, pattern)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, e) => TailLogFile(e.FullPath);
            _logWatchers.Add(watcher);
        }

        _logger.LogDebug("Log watchers started on {Dir}", logDir);
    }

    private readonly Dictionary<string, long> _logFilePositions = new();

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

    private void StopLogFileWatchers()
    {
        _watcherCts?.Cancel();
        foreach (var w in _logWatchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _logWatchers.Clear();
    }

    private void PublishLog(string line)
    {
        _logChannel.Writer.TryWrite(line);
    }

    // ── Process exit handler ─────────────────────────────────────────────────

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitCode = _process?.ExitCode ?? -1;
        _logger.LogWarning("Apache process exited with code {ExitCode}", exitCode);

        lock (_stateLock)
        {
            if (_state != ServiceState.Stopping)
            {
                _state = ServiceState.Crashed;
                _restartCount++;
                _logger.LogError("Apache crashed (restart #{Count})", _restartCount);
                // The daemon's HealthMonitor / RestartPolicy will pick this up and restart if configured
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ProcessStartInfo BuildStartInfo()
    {
        var configPath = ResolveConfigPath();
        return new ProcessStartInfo
        {
            FileName = _config.ExecutablePath,
            Arguments = $"-f \"{configPath}\" -D FOREGROUND",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _config.ServerRoot
        };
    }

    private string ResolveConfigPath()
    {
        if (Path.IsPathRooted(_config.ConfigFile))
            return _config.ConfigFile;
        return Path.Combine(_config.ServerRoot, _config.ConfigFile);
    }

    public async ValueTask DisposeAsync()
    {
        StopLogFileWatchers();

        if (_process is not null && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        }

        _process?.Dispose();
        _logChannel.Writer.TryComplete();
        _watcherCts?.Dispose();
    }
}

// ── Windows Job Object support (conditionally compiled) ───────────────────────
#if WINDOWS
internal static class JobObjects
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(nint hJob, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags, MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit, Affinity, PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    const int JobObjectExtendedLimitInformation = 9;

    public static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObject(nint.Zero, null);
        if (job == nint.Zero) throw new System.ComponentModel.Win32Exception();
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        uint size = (uint)Marshal.SizeOf(info);
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size))
            throw new System.ComponentModel.Win32Exception();
        return job;
    }

    public static void AssignProcess(nint jobHandle, Process process)
    {
        if (!AssignProcessToJobObject(jobHandle, process.Handle))
            throw new System.ComponentModel.Win32Exception();
    }
}
#endif
