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
            _process = TryAttachPostgresProcess();
            if (_process is not null)
            {
                _process.EnableRaisingEvents = true;
                _process.Exited += OnProcessExited;
                DaemonJobObject.AssignProcess(_process);
            }

            _startTime = DateTime.UtcNow;
            lock (_stateLock) _state = ServiceState.Running;
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

        _process?.Dispose();
        _process = null;
        _startTime = null;
        lock (_stateLock) _state = ServiceState.Stopped;
    }

    public async Task ReloadAsync(CancellationToken ct)
    {
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
            if (_state is ServiceState.Running or ServiceState.Starting)
                _process ??= TryAttachPostgresProcess();
            if (_state is ServiceState.Running or ServiceState.Starting && _process is null)
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
            var tail = File.ReadLines(LogFile).TakeLast(lines).ToArray();
            result.AddRange(tail);
        }

        while (result.Count < lines && _logChannel.Reader.TryRead(out var line))
            result.Add(line);

        return await Task.FromResult(result);
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

    private Process? TryAttachPostgresProcess()
    {
        var pidFile = Path.Combine(_config.DataDir, "postmaster.pid");
        if (!File.Exists(pidFile)) return null;
        var first = File.ReadLines(pidFile).FirstOrDefault();
        return int.TryParse(first, out var pid) ? TryGetProcess(pid) : null;
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch { return null; }
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
