using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;

namespace NKS.WebDevConsole.Plugin.Hosts;

/// <summary>
/// IWdcPlugin entry point for Windows hosts file management.
/// Manages entries in a delimited block within the system hosts file.
/// </summary>
public sealed class HostsPlugin : IWdcPlugin
{
    public string Id => "nks.wdc.hosts";
    public string DisplayName => "Hosts Manager";
    public string Version => "1.0.0";

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
            _logger?.LogWarning("Cannot write hosts file — elevation required. Content prepared but not written.");
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
            _logger?.LogWarning("Cannot write hosts file — elevation required. Content prepared but not written.");
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

}
