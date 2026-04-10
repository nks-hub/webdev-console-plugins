using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;

using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;

namespace NKS.WebDevConsole.Plugin.PHP;

public sealed class PhpModuleConfig
{
    public string ConfigBaseDirectory { get; set; } = string.Empty;
    public string ShimDirectory { get; set; } = string.Empty;
    public string AppDirectory { get; set; } = string.Empty;
    public string RunDirectory { get; set; } = Path.GetTempPath();
    public string LogDirectory { get; set; } = string.Empty;
    public int GracefulTimeoutSecs { get; set; } = 10;
}

/// <summary>
/// Tracks a single running PHP-FPM (Unix) or php-cgi.exe (Windows) process per version.
/// </summary>
internal sealed record PhpRunningProcess(
    PhpInstallation Installation,
    Process Process,
    DateTime StartTime,
    Channel<string> LogChannel
);

/// <summary>
/// IServiceModule for multi-version PHP.
/// Manages one FPM/CGI process per active PHP version simultaneously.
/// The daemon calls StartVersionAsync/StopVersionAsync for individual versions.
/// StartAsync/StopAsync manage ALL active versions.
/// </summary>
public sealed class PhpModule : IServiceModule, IAsyncDisposable
{
    public string ServiceId => "php";
    public string DisplayName => "PHP (Multi-version)";
    public ServiceType Type => ServiceType.Other;

    private readonly PhpVersionManager _versionManager;
    private readonly PhpIniManager _iniManager;
    private readonly PhpExtensionManager _extensionManager;
    private readonly PhpCliAliasManager _aliasManager;
    private readonly ILogger<PhpModule> _logger;
    private readonly PhpModuleConfig _config;
    private readonly Template _fpmTemplate;

    private IReadOnlyList<PhpInstallation> _installations = [];
    private readonly ConcurrentDictionary<string, PhpRunningProcess> _running = new();
    private ServiceState _state = ServiceState.Stopped;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
    private const int SIGQUIT = 3;
    private const int SIGTERM = 15;

    public PhpModule(
        PhpVersionManager versionManager,
        PhpIniManager iniManager,
        PhpExtensionManager extensionManager,
        PhpCliAliasManager aliasManager,
        ILogger<PhpModule> logger,
        PhpModuleConfig? config = null)
    {
        _versionManager = versionManager;
        _iniManager = iniManager;
        _extensionManager = extensionManager;
        _aliasManager = aliasManager;
        _logger = logger;
        _config = config ?? new PhpModuleConfig();
        _fpmTemplate = LoadFpmTemplate();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _installations = await _versionManager.DetectAllAsync(_config.AppDirectory, ct);
        _logger.LogInformation("PHP module initialized with {Count} version(s): {Versions}",
            _installations.Count,
            string.Join(", ", _installations.Select(x => x.Version)));

        // Generate php.ini for all detected versions
        foreach (var php in _installations)
        {
            var opts = new PhpIniOptions(
                Version: php.Version,
                Profile: PhpIniProfile.Development,
                Mode: PhpIniMode.Web,
                ExtDir: Path.Combine(Path.GetDirectoryName(php.ExecutablePath)!, "ext"),
                ErrorLog: Path.Combine(_config.LogDirectory, $"php{php.MajorMinor}-errors.log"),
                TmpDir: Path.GetTempPath()
            );

            // Detect xdebug
            var xdebugSo = _extensionManager.FindXdebugSo(php);
            if (xdebugSo is not null)
                opts = opts with { XdebugSo = xdebugSo };

            await _iniManager.WriteAllVariantsAsync(php, _config.ConfigBaseDirectory, opts, ct);
        }

        await _aliasManager.CreateAllShimsAsync(
            _installations, _config.ShimDirectory, _config.ConfigBaseDirectory, ct);
    }

    // ── IServiceModule: ValidateConfigAsync ─────────────────────────────────

    public async Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        var errors = new List<string>();

        foreach (var php in _installations)
        {
            var result = await Cli.Wrap(php.ExecutablePath)
                .WithArguments(["-n", "-c", Path.Combine(_config.ConfigBaseDirectory, php.MajorMinor, "php.ini"), "-r", "echo 'ok';"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0 || !result.StandardOutput.Contains("ok"))
                errors.Add($"PHP {php.Version}: {result.StandardError.Trim()}");
        }

        return errors.Count == 0
            ? new ValidationResult(true)
            : new ValidationResult(false, string.Join("\n", errors));
    }

    // ── IServiceModule: StartAsync — starts all installed versions ──────────

    public async Task StartAsync(CancellationToken ct)
    {
        _state = ServiceState.Starting;
        var tasks = _installations
            .Where(p => p.FpmExecutable is not null)
            .Select(p => StartVersionAsync(p, ct));

        await Task.WhenAll(tasks);
        _state = _running.IsEmpty ? ServiceState.Crashed : ServiceState.Running;
        _logger.LogInformation("PHP module started {Count} version(s)", _running.Count);
    }

    // ── IServiceModule: StopAsync ────────────────────────────────────────────

    public async Task StopAsync(CancellationToken ct)
    {
        _state = ServiceState.Stopping;
        var tasks = _running.Keys.ToList().Select(v => StopVersionAsync(v, ct));
        await Task.WhenAll(tasks);
        _state = ServiceState.Stopped;
    }

    // ── IServiceModule: ReloadAsync ──────────────────────────────────────────

    public async Task ReloadAsync(CancellationToken ct)
    {
        var validation = await ValidateConfigAsync(ct);
        if (!validation.IsValid)
            throw new InvalidOperationException($"PHP config validation failed: {validation.ErrorMessage}");

        foreach (var (version, running) in _running)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // PHP-FPM: SIGUSR2 triggers graceful reload
                    kill(running.Process.Id, 12); // SIGUSR2
                    _logger.LogInformation("PHP-FPM {Version} reloaded (SIGUSR2)", version);
                }
                else
                {
                    // Windows php-cgi: no graceful reload — restart
                    await StopVersionAsync(version, ct);
                    var php = _installations.First(p => p.MajorMinor == version);
                    await StartVersionAsync(php, ct);
                    _logger.LogInformation("PHP-CGI {Version} restarted (Windows reload)", version);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Reload failed for PHP {Version}: {Message}", version, ex.Message);
            }
        }
    }

    // ── IServiceModule: GetStatusAsync ───────────────────────────────────────

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        long totalMemory = 0;
        double totalCpu = 0;
        int? firstPid = null;

        foreach (var (_, rp) in _running)
        {
            try
            {
                rp.Process.Refresh();
                totalMemory += rp.Process.WorkingSet64;
                var uptime = (DateTime.UtcNow - rp.StartTime).TotalMilliseconds;
                totalCpu += rp.Process.TotalProcessorTime.TotalMilliseconds /
                            (Environment.ProcessorCount * uptime) * 100;
                firstPid ??= rp.Process.Id;
            }
            catch { /* process may have died */ }
        }

        var status = new ServiceStatus(
            "php-cgi", "PHP-CGI", _state, firstPid, totalCpu, totalMemory,
            _running.IsEmpty ? TimeSpan.Zero : DateTime.UtcNow - _running.Values.First().StartTime);

        return Task.FromResult(status);
    }

    // ── IServiceModule: GetLogsAsync ─────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        var result = new List<string>(lines);
        foreach (var (_, rp) in _running)
        {
            while (result.Count < lines && rp.LogChannel.Reader.TryRead(out var line))
                result.Add(line);
        }
        return result;
    }

    // ── Per-version start/stop ───────────────────────────────────────────────

    public async Task StartVersionAsync(PhpInstallation php, CancellationToken ct)
    {
        if (_running.ContainsKey(php.MajorMinor))
        {
            _logger.LogDebug("PHP {Version} already running", php.MajorMinor);
            return;
        }

        if (php.FpmExecutable is null)
        {
            _logger.LogWarning("PHP {Version}: no FPM/CGI executable found, skipping", php.MajorMinor);
            return;
        }

        _logger.LogInformation("Starting PHP {Version} on port {Port}...", php.Version, php.FcgiPort);

        Process process;
        if (OperatingSystem.IsWindows())
            process = StartPhpCgi(php);
        else
            process = await StartPhpFpmAsync(php, ct);

        var logChannel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                logChannel.Writer.TryWrite(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                logChannel.Writer.TryWrite($"[ERR] {e.Data}");
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            _logger.LogWarning("PHP {Version} (PID={Pid}) exited with code {Code}",
                php.MajorMinor, process.Id, process.ExitCode);
            _running.TryRemove(php.MajorMinor, out _);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _running[php.MajorMinor] = new PhpRunningProcess(php, process, DateTime.UtcNow, logChannel);
        _logger.LogInformation("PHP {Version} running (PID={Pid}, port={Port})",
            php.Version, process.Id, php.FcgiPort);
    }

    public async Task StopVersionAsync(string majorMinor, CancellationToken ct)
    {
        if (!_running.TryRemove(majorMinor, out var rp))
            return;

        _logger.LogInformation("Stopping PHP {Version} (PID={Pid})...", majorMinor, rp.Process.Id);

        if (OperatingSystem.IsWindows())
        {
            // php-cgi.exe: no graceful stop, give in-flight requests 5 seconds then kill
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (!rp.Process.HasExited)
                rp.Process.Kill(entireProcessTree: true);
        }
        else
        {
            // PHP-FPM: SIGQUIT = graceful stop (waits for workers)
            if (!rp.Process.HasExited)
                kill(rp.Process.Id, SIGQUIT);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.GracefulTimeoutSecs));

            try
            {
                await rp.Process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!rp.Process.HasExited)
                    rp.Process.Kill(entireProcessTree: true);
            }
        }

        rp.Process.Dispose();
        _logger.LogInformation("PHP {Version} stopped", majorMinor);
    }

    // ── Xdebug toggle ────────────────────────────────────────────────────────

    /// <summary>
    /// Toggles Xdebug for the given PHP version by rewriting its php.ini and reloading.
    /// </summary>
    public async Task ToggleXdebugAsync(
        string majorMinor,
        bool enable,
        CancellationToken ct = default)
    {
        var php = _installations.FirstOrDefault(p => p.MajorMinor == majorMinor)
            ?? throw new ArgumentException($"PHP {majorMinor} not installed.");

        var xdebugSo = _extensionManager.FindXdebugSo(php);
        if (xdebugSo is null && enable)
            throw new InvalidOperationException($"Xdebug extension not found for PHP {majorMinor}.");

        var extDir = Path.Combine(Path.GetDirectoryName(php.ExecutablePath)!, "ext");
        var opts = new PhpIniOptions(
            Version: php.Version,
            Profile: PhpIniProfile.Development,
            Mode: PhpIniMode.Web,
            ExtDir: extDir,
            ErrorLog: Path.Combine(_config.LogDirectory, $"php{majorMinor}-errors.log"),
            TmpDir: Path.GetTempPath(),
            XdebugEnabled: enable,
            XdebugSo: xdebugSo
        );

        var iniPath = Path.Combine(_config.ConfigBaseDirectory, majorMinor, "php.ini");
        await _iniManager.WriteAsync(opts, iniPath, ct);
        _logger.LogInformation("Xdebug {Status} for PHP {Version}", enable ? "enabled" : "disabled", majorMinor);

        // Reload the running process if active
        if (_running.ContainsKey(majorMinor))
            await ReloadAsync(ct);
    }

    // ── Process start helpers ─────────────────────────────────────────────────

    private Process StartPhpCgi(PhpInstallation php)
    {
        var iniDir = Path.Combine(_config.ConfigBaseDirectory, php.MajorMinor);
        var psi = new ProcessStartInfo
        {
            FileName = php.FpmExecutable!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // php-cgi listens on FCGI_PORT env var
        psi.Environment["PHP_FCGI_CHILDREN"] = "4";
        psi.Environment["PHP_FCGI_MAX_REQUESTS"] = "500";
        psi.Environment["PHPRC"] = iniDir;
        psi.Environment["FCGI_PORT"] = php.FcgiPort.ToString();
        psi.Environment["PHP_FCGI_PORT"] = php.FcgiPort.ToString();

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private async Task<Process> StartPhpFpmAsync(PhpInstallation php, CancellationToken ct)
    {
        var fpmConf = await RenderFpmConfAsync(php, ct);
        var confPath = Path.Combine(_config.ConfigBaseDirectory, php.MajorMinor, "php-fpm.conf");
        var tmp = confPath + ".tmp";
        await File.WriteAllTextAsync(tmp, fpmConf, ct);
        File.Move(tmp, confPath, overwrite: true);

        var psi = new ProcessStartInfo
        {
            FileName = php.FpmExecutable!,
            Arguments = $"--fpm-config \"{confPath}\" --nodaemonize",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private async Task<string> RenderFpmConfAsync(PhpInstallation php, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(_config.ConfigBaseDirectory, php.MajorMinor));

        var tag = php.MajorMinor.Replace(".", "");
        var socketPath = Path.Combine(_config.RunDirectory, $"php{tag}-fpm.sock");

        var scriptObj = new ScriptObject();
        scriptObj.Import(new
        {
            version = php.Version,
            version_tag = tag,
            pool_name = $"nks-wdc-php{tag}",
            socket_path = socketPath,
            run_dir = _config.RunDirectory.Replace('\\', '/'),
            log_dir = _config.LogDirectory.Replace('\\', '/'),
            tmp_dir = Path.GetTempPath().Replace('\\', '/'),
            fpm_user = Environment.UserName,
            fpm_group = Environment.UserName,
            pm_max_children = 10,
            pm_start_servers = 2,
            pm_min_spare = 1,
            pm_max_spare = 4
        });

        var ctx = new TemplateContext();
        ctx.PushGlobal(scriptObj);
        return _fpmTemplate.Render(ctx);
    }

    private static Template LoadFpmTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{asm.GetName().Name}.Templates.php-fpm.conf.scriban";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        var tpl = Template.Parse(reader.ReadToEnd());
        if (tpl.HasErrors)
            throw new InvalidOperationException(string.Join("; ", tpl.Messages));
        return tpl;
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await StopAsync(cts.Token);
    }
}
