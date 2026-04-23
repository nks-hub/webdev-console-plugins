using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Models;
using NKS.WebDevConsole.Plugin.SDK;

namespace NKS.WebDevConsole.Plugin.Hosts;

/// <summary>
/// IWdcPlugin entry point for Windows hosts file management.
/// Manages entries in a delimited block within the system hosts file.
/// </summary>
public sealed class HostsPlugin : IWdcPlugin, IFrontendPanelProvider
{
    public string Id => "nks.wdc.hosts";
    public string DisplayName => "Hosts Manager";
    public string Version => "1.0.0";

    public PluginUiDefinition GetUiDefinition() =>
        new UiSchemaBuilder(Id)
            .Category("Tools")
            .Icon("Files")
            .AddNavEntry("hosts", "Hosts", "/hosts", "Files", order: 40)
            .Build();

    private HostsManager? _manager;
    private ILogger? _logger;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<HostsManager>();
    }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        _logger = context.GetLogger<HostsPlugin>();
        _logger.LogInformation("Hosts plugin v{Version} loaded", Version);

        _manager = context.ServiceProvider.GetRequiredService<HostsManager>();

        if (!IsRunningAsAdmin())
        {
            _logger.LogWarning(
                "Process is not running with elevated privileges. " +
                "Writing to the hosts file will require elevation.");
        }

        var entries = _manager.GetManagedEntries();
        _logger.LogInformation("Hosts plugin found {Count} managed entries", entries.Count);

        foreach (var (ip, domain) in entries)
        {
            _logger.LogDebug("  {Ip} -> {Domain}", ip, domain);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("Hosts plugin stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a domain entry to the managed block and writes the hosts file.
    /// Falls back to dry-run logging if not running elevated.
    /// </summary>
    public async Task<string?> AddEntry(string domain, string ip = "127.0.0.1")
    {
        if (_manager is null) return null;

        var entries = _manager.GetManagedEntries();
        if (entries.Any(e => e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)))
        {
            _logger?.LogInformation("Domain {Domain} already exists in managed block", domain);
            return null;
        }

        var allDomains = entries.Select(e => e.Domain).Append(domain).Distinct().ToList();
        var currentContent = File.Exists(_manager.HostsPath)
            ? await File.ReadAllTextAsync(_manager.HostsPath)
            : string.Empty;

        var updated = _manager.BuildUpdatedContent(currentContent, allDomains, ip);

        try
        {
            await File.WriteAllTextAsync(_manager.HostsPath, updated);
            _logger?.LogInformation("Added {Domain} to hosts file", domain);
            try { Process.Start("ipconfig", "/flushdns")?.WaitForExit(5000); } catch { }
        }
        catch (UnauthorizedAccessException)
        {
            if (!await TryWriteWithElevationAsync(updated))
            {
                _logger?.LogWarning("Cannot write hosts file — elevation required. Content prepared but not written.");
            }
            else
            {
                _logger?.LogInformation("Added {Domain} to hosts file (via elevation)", domain);
            }
        }

        return updated;
    }

    /// <summary>
    /// Removes a domain entry from the managed block and writes the hosts file.
    /// Falls back to dry-run logging if not running elevated.
    /// </summary>
    public async Task<string?> RemoveEntry(string domain)
    {
        if (_manager is null) return null;

        var entries = _manager.GetManagedEntries();
        var filtered = entries
            .Where(e => !e.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Domain)
            .ToList();

        if (filtered.Count == entries.Count)
        {
            _logger?.LogInformation("Domain {Domain} not found in managed block", domain);
            return null;
        }

        var currentContent = File.Exists(_manager.HostsPath)
            ? await File.ReadAllTextAsync(_manager.HostsPath)
            : string.Empty;

        var updated = _manager.BuildUpdatedContent(currentContent, filtered);

        try
        {
            await File.WriteAllTextAsync(_manager.HostsPath, updated);
            _logger?.LogInformation("Removed {Domain} from hosts file", domain);
            try { Process.Start("ipconfig", "/flushdns")?.WaitForExit(5000); } catch { }
        }
        catch (UnauthorizedAccessException)
        {
            if (!await TryWriteWithElevationAsync(updated))
            {
                _logger?.LogWarning("Cannot write hosts file — elevation required. Content prepared but not written.");
            }
            else
            {
                _logger?.LogInformation("Removed {Domain} from hosts file (via elevation)", domain);
            }
        }

        return updated;
    }

    /// <summary>
    /// Returns all entries currently in the managed block.
    /// </summary>
    public List<(string Ip, string Domain)> GetManagedEntries()
    {
        return _manager?.GetManagedEntries() ?? [];
    }

    private static bool IsRunningAsAdmin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Best-effort write of the hosts file via an OS-native elevation
    /// prompt. macOS uses osascript's `with administrator privileges`
    /// (TouchID/password dialog); Linux falls back to pkexec when the
    /// user is at a desktop session. Windows doesn't need this — the
    /// app.manifest already ships requireAdministrator and the direct
    /// File.Write would have succeeded. Returns true on success, false
    /// on cancel/timeout/platform-not-supported.
    /// </summary>
    private async Task<bool> TryWriteWithElevationAsync(string content)
    {
        if (_manager is null) return false;
        var tmp = Path.Combine(Path.GetTempPath(), $"nks-wdc-hosts.{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(tmp, content);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Build the AppleScript in managed code. `quoted form of` makes
                // AppleScript itself shell-quote the tmp/dest paths so spaces,
                // apostrophes, and special chars in Path.GetTempPath() cannot
                // corrupt the /bin/cp invocation. We pass the script as a SINGLE
                // -e argument via ArgumentList — no shell, no outer round of
                // double-quote escaping.
                var script =
                    "do shell script \"/bin/cp \" & quoted form of \"" + EscapeForAppleScriptString(tmp) + "\" & \" \" & " +
                    "quoted form of \"" + EscapeForAppleScriptString(_manager.HostsPath) + "\" " +
                    "with prompt \"NKS WebDev Console needs permission to update " + EscapeForAppleScriptString(_manager.HostsPath) + ".\" " +
                    "with administrator privileges";

                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                _logger?.LogInformation("[hosts] requesting elevation prompt for {Path} write (tmp={Tmp})", _manager.HostsPath, tmp);
                using var p = Process.Start(psi);
                if (p is null) return false;
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    var err = (await p.StandardError.ReadToEndAsync()).Trim();
                    if (err.Contains("User canceled", StringComparison.OrdinalIgnoreCase)
                        || err.Contains("(-128)", StringComparison.Ordinal))
                        _logger?.LogWarning("[hosts] user cancelled elevation prompt");
                    else
                        _logger?.LogWarning("[hosts] osascript elevation failed (exit={Code}): {Err}", p.ExitCode, err);
                    return false;
                }
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // pkexec present on most desktop distros; falls back to a
                // clean error when run headlessly (no DISPLAY) — caller
                // then shows the dry-run warning.
                var psi = new ProcessStartInfo
                {
                    FileName = "pkexec",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("/bin/cp");
                psi.ArgumentList.Add(tmp);
                psi.ArgumentList.Add(_manager.HostsPath);

                _logger?.LogInformation("[hosts] requesting elevation prompt via pkexec for {Path} write", _manager.HostsPath);
                using var p = Process.Start(psi);
                if (p is null) return false;
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    var err = (await p.StandardError.ReadToEndAsync()).Trim();
                    _logger?.LogWarning("[hosts] pkexec elevation failed (exit={Code}): {Err}", p.ExitCode, err);
                    return false;
                }
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("[hosts] elevation helper failed: {Message}", ex.Message);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static string EscapeForAppleScriptString(string value)
    {
        // AppleScript strings use backslash escapes for " and \ only.
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
