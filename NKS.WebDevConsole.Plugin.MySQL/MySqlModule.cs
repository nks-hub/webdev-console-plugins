using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.MySQL;

public sealed class MySqlConfig
{
    /// <summary>Root for NKS WDC managed MySQL installs.</summary>
    public string BinariesRoot { get; set; } = Path.Combine(WdcPaths.BinariesRoot, "mysql");

    /// <summary>Where this instance stores its datafiles. Default ~/.wdc/data/mysql.</summary>
    public string DataDir { get; set; } = Path.Combine(WdcPaths.DataRoot, "mysql");

    public string? ExecutablePath { get; set; }
    public string? MysqladminPath { get; set; }
    public string? ConfigFile { get; set; }
    public string LogDirectory { get; set; } = Path.Combine(WdcPaths.LogsRoot, "mysql");
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
            .OrderByDescending(d => Path.GetFileName(d), SemverVersionComparer.Instance)
            .ToList();

        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var vdir in versionDirs)
        {
            var mysqld = Path.Combine(vdir, "bin", "mysqld" + ext);
            if (!File.Exists(mysqld)) continue;

            ExecutablePath = mysqld;
            MysqladminPath = Path.Combine(vdir, "bin", "mysqladmin" + ext);
            return true;
        }
        return false;
    }
}

/// <summary>
/// Full IServiceModule implementation for MySQL.
/// Manages process lifecycle, config validation, log streaming, and metrics.
/// Works only with NKS WDC managed MySQL binaries under <c>~/.wdc/binaries/mysql/</c>
/// — never touches MAMP / XAMPP / system installs.
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
    /// First-init also generates a DPAPI-protected root password and stores it
    /// (set on the running instance later by <see cref="SetRootPasswordIfNeededAsync"/>).
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

        // MariaDB ≥ 10.4 doesn't accept `mysqld --initialize-insecure` (it's a
        // MySQL-only flag). When the binary next to mysqld looks like MariaDB
        // — most reliably detected by the presence of the `mariadb-install-db`
        // helper in the same bin/ — use that instead. Otherwise fall back to
        // upstream MySQL's `--initialize-insecure`.
        var binDir = Path.GetDirectoryName(_config.ExecutablePath)!;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var mariaInit = Path.Combine(binDir, "mariadb-install-db" + ext);
        var isMariaDB = File.Exists(mariaInit);

        Directory.CreateDirectory(_config.DataDir);

        BufferedCommandResult result;
        if (isMariaDB)
        {
            _logger.LogInformation("Detected MariaDB — using {Tool}", mariaInit);
            var user = Environment.UserName;
            result = await Cli.Wrap(mariaInit)
                .WithArguments(new[] { $"--datadir={_config.DataDir}", $"--user={user}", "--auth-root-authentication-method=normal" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
        }
        else
        {
            var args = $"--initialize-insecure --datadir=\"{_config.DataDir}\" --console";
            result = await Cli.Wrap(_config.ExecutablePath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);
        }

        if (result.ExitCode == 0)
        {
            _logger.LogInformation("MySQL data dir initialized successfully");
            // Generate the DPAPI-protected root password now so the next start can apply it.
            var generated = NKS.WebDevConsole.Core.Services.MySqlRootPassword.EnsureExists();
            _logger.LogInformation("Generated DPAPI-protected MySQL root password ({Length} chars)", generated.Length);
            _needsPasswordSet = true;
        }
        else
        {
            _logger.LogError("MySQL data-dir init failed (exit {Code}): {Err}",
                result.ExitCode, result.StandardError.Trim());
        }
    }

    /// <summary>True between <c>--initialize-insecure</c> and the first successful root SET PASSWORD.</summary>
    private bool _needsPasswordSet;

    /// <summary>
    /// Right after the first start, while root is still passwordless, set the
    /// DPAPI-stored password via mysql CLI: <c>mysql -u root -e "ALTER USER 'root'@'localhost' IDENTIFIED BY '...'"</c>.
    /// Called from <see cref="StartAsync"/> after the server reports ready.
    /// </summary>
    private async Task SetRootPasswordIfNeededAsync(CancellationToken ct)
    {
        if (!_needsPasswordSet) return;
        if (string.IsNullOrEmpty(_config.ExecutablePath)) return;

        var cliExt = OperatingSystem.IsWindows() ? ".exe" : "";
        var mysqlCli = Path.Combine(Path.GetDirectoryName(_config.ExecutablePath)!, "mysql" + cliExt);
        if (!File.Exists(mysqlCli))
        {
            _logger.LogWarning("mysql{Ext} not found next to mysqld — cannot set root password", cliExt);
            return;
        }

        var password = NKS.WebDevConsole.Core.Services.MySqlRootPassword.TryRead();
        if (string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("DPAPI password store empty — skipping SET PASSWORD");
            return;
        }

        // SQL is parameter-free; the password is single-quoted with embedded quotes escaped.
        var escaped = password.Replace("'", "''");
        var sql = $"ALTER USER 'root'@'localhost' IDENTIFIED BY '{escaped}'; FLUSH PRIVILEGES;";
        try
        {
            // Pass via -e (no shell interpretation thanks to CliWrap argument array)
            var result = await Cli.Wrap(mysqlCli)
                .WithArguments(new[] { "-h", "127.0.0.1", "-P", _config.Port.ToString(), "-u", "root", "-e", sql })
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode == 0)
            {
                _logger.LogInformation("MySQL root password applied (DPAPI-protected at rest)");
                _needsPasswordSet = false;
            }
            else
            {
                _logger.LogWarning("SET PASSWORD failed (exit {Code}): {Err}",
                    result.ExitCode, result.StandardError.Trim());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SET PASSWORD threw");
        }
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

        // Envelope: ensure _state always leaves Starting on any exception so
        // the Dashboard toggle remains clickable — same bug-class fix as
        // CaddyModule/ApacheModule in this commit.
        try
        {
            // Daemon booted before the user's wizard install, so InitializeAsync's
            // binary probe returned false and ExecutablePath is null. Retry
            // detection at Start-time now that ~/.wdc/binaries/mysql has been
            // populated — mirrors the ApacheModule lazy-init fix.
            if (string.IsNullOrEmpty(_config.ExecutablePath) && Directory.Exists(_config.BinariesRoot))
            {
                await InitializeAsync(ct);
            }
            if (string.IsNullOrEmpty(_config.ExecutablePath))
                throw new InvalidOperationException("MySQL executable not found.");

            var validation = await ValidateConfigAsync(ct);
            if (!validation.IsValid)
                throw new InvalidOperationException($"Config validation failed: {validation.ErrorMessage}");

            // Ensure a my.ini exists in the data directory so the Service
            // Config UI has something to render (and the user can tweak
            // it). mysqld on Windows reads the file implicitly when it
            // lives next to the datadir, no --defaults-file argument
            // needed. See EnsureDefaultMyIni for the tiny baseline we
            // generate on first run.
            EnsureDefaultMyIni();

            _logger.LogInformation("Starting MySQL on port {Port}...", _config.Port);

            // Terminate any orphaned mysqld.exe processes from a previous daemon
            // run that would hold port 3306 and starve the fresh spawn. Only our
            // managed binary is in scope — system/MAMP mysqlds are untouched.
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

            // First-run hook: if --initialize-insecure just ran, root is still passwordless.
            // Apply the DPAPI-stored password immediately so subsequent connections need it.
            await SetRootPasswordIfNeededAsync(ct);

            StartLogFileWatcher();
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
        var exitedPid = (sender as Process)?.Id ?? _process?.Id ?? -1;
        var exitCode = (sender as Process)?.ExitCode ?? -1;

        // MySQL 8.x on Windows uses an angel/child process model: the initial
        // mysqld we spawned sometimes exits as soon as a child mysqld takes
        // over the port, leaving _process referencing a dead PID. Before
        // marking the service crashed, give mysqld a short grace period to
        // rewrite its pidfile — if a live mysqld is recorded there, re-attach
        // and keep the service Running. Otherwise it's a real crash.
        lock (_stateLock)
        {
            if (_state == ServiceState.Stopping) return;
        }

        // Give mysqld up to 2s to finalise its pidfile hand-off.
        var realProcess = WaitForPidFileHandoffAsync(exitedPid, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        if (realProcess != null)
        {
            _logger.LogInformation(
                "MySQL angel process exited (PID={OldPid} code={Code}) — re-attaching to real mysqld from pidfile (PID={NewPid})",
                exitedPid, exitCode, realProcess.Id);
            try
            {
                realProcess.EnableRaisingEvents = true;
                realProcess.Exited += OnProcessExited;
                NKS.WebDevConsole.Core.Services.DaemonJobObject.AssignProcess(realProcess);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Re-attach post-wiring failed");
            }

            var oldProcess = _process;
            _process = realProcess;
            try { oldProcess?.Dispose(); } catch { }
            return;
        }

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

    /// <summary>
    /// Polls the MySQL pidfile for up to <paramref name="timeout"/> waiting for
    /// it to point at a live mysqld process that is NOT the one that just
    /// exited (<paramref name="exitedPid"/>). Returns the Process handle, or
    /// null on timeout / no such process.
    /// </summary>
    private async Task<Process?> WaitForPidFileHandoffAsync(int exitedPid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var proc = TryAttachFromPidFile();
            if (proc != null && proc.Id != exitedPid) return proc;
            await Task.Delay(150);
        }
        return null;
    }

    // -- Helpers --

    /// <summary>
    /// Writes a sensible default my.ini into the data directory on first
    /// run if none exists yet. mysqld on Windows auto-discovers `my.ini`
    /// in the datadir so no --defaults-file argument is needed. The
    /// defaults mirror what MAMP/XAMPP ship so a user coming from those
    /// stacks sees a familiar knob set (port, charset, innodb pool,
    /// max_connections). Safe to re-run: never overwrites a hand-edited
    /// file.
    /// </summary>
    private void EnsureDefaultMyIni()
    {
        try
        {
            if (string.IsNullOrEmpty(_config.DataDir)) return;
            Directory.CreateDirectory(_config.DataDir);
            var iniPath = Path.Combine(_config.DataDir, "my.ini");
            if (File.Exists(iniPath)) return;

            // Resolve basedir from executable path so users can point it
            // at alternate mysqld binaries without breaking the include.
            string baseDir = "";
            if (!string.IsNullOrEmpty(_config.ExecutablePath))
            {
                var binDir = Path.GetDirectoryName(_config.ExecutablePath);
                if (!string.IsNullOrEmpty(binDir))
                    baseDir = Path.GetDirectoryName(binDir) ?? "";
            }

            var content =
                "# Generated by NKS WebDev Console on first run.\n" +
                "# Edit via Settings → Services → MySQL → Configuration.\n" +
                "\n" +
                "[mysqld]\n" +
                $"port = {_config.Port}\n" +
                $"datadir = {_config.DataDir.Replace('\\', '/')}\n" +
                (string.IsNullOrEmpty(baseDir) ? "" : $"basedir = {baseDir.Replace('\\', '/')}\n") +
                "character-set-server = utf8mb4\n" +
                "collation-server = utf8mb4_unicode_ci\n" +
                "default-storage-engine = InnoDB\n" +
                "max_connections = 200\n" +
                "max_allowed_packet = 256M\n" +
                "innodb_buffer_pool_size = 256M\n" +
                "innodb_log_file_size = 64M\n" +
                "innodb_flush_log_at_trx_commit = 2\n" +
                "innodb_file_per_table = 1\n" +
                "# Extend as needed — WDC will not overwrite this file after first run.\n" +
                "\n" +
                "[client]\n" +
                $"port = {_config.Port}\n" +
                "default-character-set = utf8mb4\n";

            File.WriteAllText(iniPath, content);
            _logger.LogInformation("Generated default my.ini at {Path}", iniPath);
        }
        catch (Exception ex)
        {
            // Non-fatal: MySQL still starts with compiled defaults if we
            // can't write the file (permission, disk full, etc.).
            _logger.LogWarning(ex, "Failed to write default my.ini in {Dir} — MySQL will use compiled defaults", _config.DataDir);
        }
    }

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

    /// <summary>
    /// Finds and terminates any orphaned mysqld.exe processes that would hold
    /// our target port. Uses two strategies: (1) MainModule path match, (2)
    /// Get-NetTCPConnection port-holder match verified against process name.
    /// System/MAMP mysqlds on a different port are untouched.
    /// </summary>
    private void KillOrphanedProcesses()
    {
        if (string.IsNullOrEmpty(_config.ExecutablePath)) return;

        // Strategy 1: executable-path match.
        try
        {
            foreach (var proc in Process.GetProcessesByName("mysqld"))
            {
                try
                {
                    var exePath = proc.MainModule?.FileName;
                    if (exePath is not null && string.Equals(exePath, _config.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Killing orphaned mysqld.exe PID {Pid} (path-match) from previous run", proc.Id);
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(3000);
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
            _logger.LogDebug(ex, "Process enumeration for mysqld failed");
        }

        // Strategy 2: port-holder match.
        var pid = FindPidHoldingTcpPort(_config.Port);
        if (pid <= 0) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.ProcessName.Equals("mysqld", StringComparison.OrdinalIgnoreCase)) return;
            _logger.LogWarning("Killing orphan PID {Pid} holding port {Port} (name-match {Name})",
                pid, _config.Port, proc.ProcessName);
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "port-holder orphan kill skipped for PID {Pid}", pid);
        }
    }

    /// <summary>
    /// Returns the PID of the process listening on the given TCP port via
    /// Get-NetTCPConnection, or -1 on failure. Ships with Win10+.
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

    /// <summary>
    /// Reads the MySQL-written PID file from DataDir and returns a Process handle
    /// to the still-running mysqld, or null if no live PID is recorded. Used to
    /// recover after the spawned parent process exits under MySQL 8.x Windows's
    /// angel/child process model.
    /// </summary>
    private Process? TryAttachFromPidFile()
    {
        try
        {
            if (string.IsNullOrEmpty(_config.DataDir) || !Directory.Exists(_config.DataDir))
                return null;

            var pidFiles = Directory.GetFiles(_config.DataDir, "*.pid");
            foreach (var pidFile in pidFiles)
            {
                if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) continue;
                try
                {
                    var proc = Process.GetProcessById(pid);
                    // Sanity-check: must be a mysqld process, not some recycled PID.
                    if (proc.ProcessName.Equals("mysqld", StringComparison.OrdinalIgnoreCase)
                        && !proc.HasExited)
                    {
                        return proc;
                    }
                }
                catch (ArgumentException) { /* process no longer exists */ }
                catch (InvalidOperationException) { /* already exited */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TryAttachFromPidFile failed");
        }
        return null;
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
