using System.Diagnostics;
using System.Threading.Channels;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.PostgreSQL;

public sealed class PostgreSqlConfig
{
    public string BinariesRoot { get; set; } = Path.Combine(WdcPaths.BinariesRoot, "postgresql");
    public string DataDir { get; set; } = Path.Combine(WdcPaths.DataRoot, "postgresql");
    public string LogDirectory { get; set; } = Path.Combine(WdcPaths.LogsRoot, "postgresql");
    public string? PostgresPath { get; set; }
    public string? PgCtlPath { get; set; }
    public string? InitDbPath { get; set; }
    public string? PgIsReadyPath { get; set; }
    public int Port { get; set; } = 5432;
    public int GracefulTimeoutSecs { get; set; } = 15;
}

public sealed class PostgreSqlModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "postgresql";
    public string DisplayName => "PostgreSQL";
    public ServiceType Type => ServiceType.Database;

    private readonly ILogger<PostgreSqlModule> _logger;
    private readonly PostgreSqlConfig _config;
    private readonly object _stateLock = new();
    private readonly Channel<string> _logChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

    private Process? _process;
    private ServiceState _state = ServiceState.Stopped;
    private DateTime? _startTime;
    private string LogFile => Path.Combine(_config.LogDirectory, "postgresql.log");

    public PostgreSqlModule(ILogger<PostgreSqlModule> logger, PostgreSqlConfig? config = null)
    {
        _logger = logger;
        _config = config ?? new PostgreSqlConfig();
    }

    public Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_config.DataDir);
        Directory.CreateDirectory(_config.LogDirectory);
        DetectBinaries();

        if (!string.IsNullOrEmpty(_config.PostgresPath) && File.Exists(_config.PostgresPath))
            _logger.LogInformation("Using PostgreSQL: {Path}", _config.PostgresPath);
        else
            _logger.LogWarning("postgres executable not found");

        return Task.CompletedTask;
    }

    public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_config.PostgresPath) || !File.Exists(_config.PostgresPath))
            return Task.FromResult(new ValidationResult(false, "postgres executable not found"));
        if (string.IsNullOrEmpty(_config.PgCtlPath) || !File.Exists(_config.PgCtlPath))
            return Task.FromResult(new ValidationResult(false, "pg_ctl executable not found"));
        if (string.IsNullOrEmpty(_config.InitDbPath) || !File.Exists(_config.InitDbPath))
            return Task.FromResult(new ValidationResult(false, "initdb executable not found"));
        if (string.IsNullOrEmpty(_config.PgIsReadyPath) || !File.Exists(_config.PgIsReadyPath))
            return Task.FromResult(new ValidationResult(false, "pg_isready executable not found"));
        if (_config.Port is < 1 or > 65535)
            return Task.FromResult(new ValidationResult(false, $"Invalid PostgreSQL port: {_config.Port}"));
        return Task.FromResult(new ValidationResult(true));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Running or ServiceState.Starting)
                throw new InvalidOperationException($"PostgreSQL is already {_state}.");
            _state = ServiceState.Starting;
        }

        try
        {
            var validation = await ValidateConfigAsync(ct);
            if (!validation.IsValid)
                throw new InvalidOperationException($"Config validation failed: {validation.ErrorMessage}");

            await EnsureDataDirInitializedAsync(ct);

            // A postmaster left running by an earlier daemon (crash, killed
            // tree) still owns the data dir; pg_ctl start would refuse it.
            if (TryAttachPostgresProcess() is { } orphan)
            {
                await WaitUntilReadyAsync(ct); // it may be shutting down right now
                AdoptRunning(orphan);
                _logger.LogInformation("PostgreSQL already running (PID {Pid}), attached", orphan.Id);
                return;
            }

            var args = new[]
            {
                "-D", _config.DataDir,
                "-l", LogFile,
                "-o", $"-p {_config.Port} -h 127.0.0.1",
                "-w",
                "-t", "20",
                "start"
            };
            var exitCode = await RunPgCtlStartAsync(args, ct);
            if (exitCode != 0)
                throw new InvalidOperationException($"pg_ctl start exited {exitCode}; see {LogFile}");

            await WaitUntilReadyAsync(ct);
            var postmaster = await AttachWithRetryAsync(ct)
                ?? throw new InvalidOperationException($"PostgreSQL answered but postmaster.pid has no live postgres process; see {LogFile}");
            AdoptRunning(postmaster);
            _logger.LogInformation("PostgreSQL running on port {Port}", _config.Port);
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

        try
        {
            if (!string.IsNullOrEmpty(_config.PgCtlPath) && File.Exists(_config.PgCtlPath))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_config.GracefulTimeoutSecs));
                var result = await Cli.Wrap(_config.PgCtlPath)
                    .WithArguments(new[] { "-D", _config.DataDir, "stop", "-m", "fast" })
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteBufferedAsync(cts.Token);
                PublishBuffered(result);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("pg_ctl stop did not finish in {Secs}s; killing the postmaster", _config.GracefulTimeoutSecs);
            try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        finally
        {
            // Never leave the module stuck in Stopping.
            _process?.Dispose();
            _process = null;
            _startTime = null;
            lock (_stateLock) _state = ServiceState.Stopped;
        }
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
        // Nothing to reload: the config is read on the next start. Without this
        // the daemon's startup hydration hit a stale postmaster.pid and warned.
        lock (_stateLock)
            if (_state != ServiceState.Running) return;
        if (string.IsNullOrEmpty(_config.PgCtlPath) || !File.Exists(_config.PgCtlPath))
            throw new InvalidOperationException("pg_ctl executable not found");

        var result = await Cli.Wrap(_config.PgCtlPath)
            .WithArguments(new[] { "-D", _config.DataDir, "reload" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        PublishBuffered(result);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"pg_ctl reload exited {result.ExitCode}: {result.StandardError.Trim()}");
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ServiceState state;
        int? pid;
        lock (_stateLock)
        {
            // While Starting there is no postmaster yet; only a Running service
            // without one has crashed. (Flagging Starting as Crashed fired a
            // "service crashed" toast on every start.)
            if (_state == ServiceState.Running && (_process is null || HasExited(_process)))
                _state = ServiceState.Crashed;
            state = _state;
            pid = _process?.Id;
        }

        var (cpu, memory) = ProcessMetricsSampler.Sample(_process);
        var uptime = _startTime.HasValue ? DateTime.UtcNow - _startTime.Value : TimeSpan.Zero;
        return Task.FromResult(new ServiceStatus(ServiceId, DisplayName, state, pid, cpu, memory, uptime));
    }

    public async Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        var result = new List<string>(lines);
        if (File.Exists(LogFile))
        {
            // postgres keeps the log open for writing.
            using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var tail = new Queue<string>(lines);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (tail.Count == lines) tail.Dequeue();
                tail.Enqueue(line);
            }
            result.AddRange(tail);
        }

        while (result.Count < lines && _logChannel.Reader.TryRead(out var line))
            result.Add(line);

        return result;
    }

    public ValueTask DisposeAsync()
    {
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void DetectBinaries()
    {
        if (!Directory.Exists(_config.BinariesRoot)) return;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var versionDirs = Directory.GetDirectories(_config.BinariesRoot)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && !Path.GetFileName(d).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => Path.GetFileName(d), SemverVersionComparer.Instance);

        foreach (var dir in versionDirs)
        {
            var bin = Path.Combine(dir, "bin");
            var postgres = Path.Combine(bin, "postgres" + ext);
            var pgCtl = Path.Combine(bin, "pg_ctl" + ext);
            var initDb = Path.Combine(bin, "initdb" + ext);
            var pgIsReady = Path.Combine(bin, "pg_isready" + ext);
            if (!File.Exists(postgres) || !File.Exists(pgCtl) || !File.Exists(initDb) || !File.Exists(pgIsReady))
                continue;

            _config.PostgresPath = postgres;
            _config.PgCtlPath = pgCtl;
            _config.InitDbPath = initDb;
            _config.PgIsReadyPath = pgIsReady;
            return;
        }
    }

    private async Task EnsureDataDirInitializedAsync(CancellationToken ct)
    {
        if (File.Exists(Path.Combine(_config.DataDir, "PG_VERSION")))
            return;

        var result = await Cli.Wrap(_config.InitDbPath!)
            .WithArguments(new[] { "-D", _config.DataDir, "-A", "trust", "-U", "postgres", "--no-locale" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);
        PublishBuffered(result);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"initdb exited {result.ExitCode}: {result.StandardError.Trim()}");
    }

    private async Task<int> RunPgCtlStartAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.PgCtlPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("pg_ctl start failed to launch");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException("pg_ctl start did not exit within 30 seconds");
        }
        return process.ExitCode;
    }

    private async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var result = await Cli.Wrap(_config.PgIsReadyPath!)
                .WithArguments(new[] { "-h", "127.0.0.1", "-p", _config.Port.ToString(), "-U", "postgres" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
            PublishBuffered(result);
            if (result.ExitCode == 0) return;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"PostgreSQL did not become ready on port {_config.Port} within 20 seconds.");
    }

    private void AdoptRunning(Process postmaster)
    {
        postmaster.EnableRaisingEvents = true;
        postmaster.Exited += OnProcessExited;
        DaemonJobObject.AssignProcess(postmaster);
        _process?.Dispose();
        _process = postmaster;
        _startTime = DateTime.UtcNow;
        lock (_stateLock) _state = ServiceState.Running;
    }

    private async Task<Process?> AttachWithRetryAsync(CancellationToken ct)
    {
        // postgres may be rewriting postmaster.pid right after it answers.
        for (var attempt = 0; ; attempt++)
        {
            if (TryAttachPostgresProcess() is { } p) return p;
            if (attempt == 2) return null;
            await Task.Delay(200, ct);
        }
    }

    private Process? TryAttachPostgresProcess()
    {
        var pidFile = Path.Combine(_config.DataDir, "postmaster.pid");
        if (!File.Exists(pidFile)) return null;
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(pidFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            while (lines.Count < 4 && reader.ReadLine() is { } line) lines.Add(line);
        }
        catch (IOException) { return null; }
        return MatchPostmaster(lines, _config.Port, TryGetPostgresProcess);
    }

    /// <summary>
    /// The live postmaster described by the first lines of postmaster.pid (PID,
    /// data dir, start time as Unix seconds, port), or null when the file is
    /// stale. After an unclean shutdown the file survives and Windows may hand
    /// its PID to another postgres (e.g. a system PostgreSQL service), so the
    /// port and the process start time must match too.
    /// </summary>
    internal static Process? MatchPostmaster(IReadOnlyList<string> lines, int port, Func<int, Process?> getProcess)
    {
        if (lines.Count < 4 || !int.TryParse(lines[0].Trim(), out var pid)) return null;
        if (lines[3].Trim() != port.ToString()) return null;
        if (!long.TryParse(lines[2].Trim(), out var startedEpoch)) return null;
        var p = getProcess(pid);
        if (p is null) return null;
        try
        {
            var started = DateTimeOffset.FromUnixTimeSeconds(startedEpoch);
            if (Math.Abs((new DateTimeOffset(p.StartTime) - started).TotalSeconds) <= 5) return p;
        }
        catch { /* StartTime not readable: treat as not ours */ }
        p.Dispose();
        return null;
    }

    private static Process? TryGetPostgresProcess(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (p.ProcessName.Equals("postgres", StringComparison.OrdinalIgnoreCase)) return p;
            p.Dispose();
        }
        catch { /* gone or not accessible */ }
        return null;
    }

    private static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch { return true; }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        lock (_stateLock)
        {
            if (_state is ServiceState.Stopping or ServiceState.Stopped)
                return;
            _state = ServiceState.Crashed;
            _startTime = null;
        }
    }

    private void PublishBuffered(BufferedCommandResult result)
    {
        foreach (var line in SplitLines(result.StandardOutput))
            _logChannel.Writer.TryWrite(line);
        foreach (var line in SplitLines(result.StandardError))
            _logChannel.Writer.TryWrite("[ERR] " + line);
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
}
