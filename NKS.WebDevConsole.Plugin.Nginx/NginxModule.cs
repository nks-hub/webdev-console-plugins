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
}

/// <summary>
/// Scaffold IServiceModule for Nginx. Lifecycle methods (Start/Stop/Reload/Validate)
/// are stubbed and throw NotImplementedException — full implementation is tracked
/// as the next iteration of the nginx plugin.
/// </summary>
public sealed class NginxModule : IServiceModule
{
    public string ServiceId => "nginx";
    public string DisplayName => "Nginx";
    public ServiceType Type => ServiceType.WebServer;

    private readonly ILogger<NginxModule> _logger;
    private readonly NginxConfig _config;

    public NginxModule(ILogger<NginxModule> logger, NginxConfig? config = null)
    {
        _logger = logger;
        _config = config ?? new NginxConfig();
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

    // ── IServiceModule — stubbed lifecycle ───────────────────────────────────
    // TODO(next-iteration): implement process management, config validation
    // via `nginx -t`, graceful reload via SIGHUP (Unix) / `nginx -s reload`,
    // log tailing, and metrics sampling. Mirror ApacheModule's structure.

    public Task<ValidationResult> ValidateConfigAsync(CancellationToken ct)
    {
        _logger.LogWarning("NginxModule.ValidateConfigAsync — not yet implemented (scaffold)");
        throw new NotImplementedException("Nginx config validation is not yet implemented. TODO: shell out to `nginx -t -c <path>`.");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _logger.LogWarning("NginxModule.StartAsync — not yet implemented (scaffold)");
        throw new NotImplementedException("Nginx start is not yet implemented. TODO: launch nginx process, wait for port bind, register with DaemonJobObject.");
    }

    public Task StopAsync(CancellationToken ct)
    {
        _logger.LogWarning("NginxModule.StopAsync — not yet implemented (scaffold)");
        throw new NotImplementedException("Nginx stop is not yet implemented. TODO: `nginx -s quit` (graceful), fall back to SIGTERM / Kill after timeout.");
    }

    public Task ReloadAsync(CancellationToken ct)
    {
        _logger.LogWarning("NginxModule.ReloadAsync — not yet implemented (scaffold)");
        throw new NotImplementedException("Nginx reload is not yet implemented. TODO: SIGHUP on Unix, `nginx -s reload` on Windows.");
    }

    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        // Non-throwing stub: reporting Stopped is safer than throwing here because
        // the daemon's status loop polls every few seconds and a throw would spam
        // the logs.
        var status = new ServiceStatus("nginx", "Nginx", ServiceState.Stopped, null, 0, 0, TimeSpan.Zero);
        return Task.FromResult(status);
    }

    public Task<IReadOnlyList<string>> GetLogsAsync(int lines, CancellationToken ct)
    {
        // Non-throwing stub: empty log list until log tailing is implemented.
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
