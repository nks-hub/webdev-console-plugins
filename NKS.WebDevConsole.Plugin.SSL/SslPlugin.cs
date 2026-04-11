using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Interfaces;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.SSL;

/// <summary>
/// IWdcPlugin entry point for SSL certificate management via mkcert.
/// </summary>
public sealed class SslPlugin : IWdcPlugin
{
    public string Id => "nks.wdc.ssl";
    public string DisplayName => "SSL (mkcert)";
    public string Version => "1.0.0";

    private MkcertManager? _mkcert;
    private ILogger? _logger;

    private readonly Dictionary<string, CertInfo> _certs = new(StringComparer.OrdinalIgnoreCase);
    private string _certsBaseDir = null!;

    public void Initialize(IServiceCollection services, IPluginContext context)
    {
        services.AddSingleton<MkcertManager>();
    }

    public async Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        _logger = context.GetLogger<SslPlugin>();
        _logger.LogInformation("SSL plugin v{Version} loaded", Version);

        _mkcert = context.ServiceProvider.GetRequiredService<MkcertManager>();

        _certsBaseDir = Path.Combine(WdcPaths.SslRoot, "sites");
        Directory.CreateDirectory(_certsBaseDir);

        var detected = await _mkcert.DetectAsync();
        if (!detected)
        {
            _logger.LogWarning("mkcert is not installed. SSL certificate features will be unavailable.");
            return;
        }

        // Scan existing certs from disk
        ScanExistingCerts();
        _logger.LogInformation("SSL plugin ready. {Count} existing certificate(s) found.", _certs.Count);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _logger?.LogInformation("SSL plugin stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Installs the mkcert root CA into the system trust store.
    /// </summary>
    public async Task<bool> InstallCA()
    {
        if (_mkcert is null || !_mkcert.IsInstalled)
        {
            _logger?.LogError("mkcert not available");
            return false;
        }

        return await _mkcert.InstallCaAsync();
    }

    /// <summary>
    /// Generates a certificate for the domain and optional aliases.
    /// </summary>
    public async Task<CertInfo?> GenerateCert(string domain, params string[] aliases)
    {
        if (_mkcert is null || !_mkcert.IsInstalled)
        {
            _logger?.LogError("mkcert not available");
            return null;
        }

        var domainDir = Path.Combine(_certsBaseDir, domain);
        var result = await _mkcert.GenerateCertAsync(domain, aliases, domainDir);

        if (result is null)
            return null;

        var (certPath, keyPath) = result.Value;
        var info = new CertInfo(domain, certPath, keyPath, DateTime.UtcNow, aliases);
        _certs[domain] = info;

        _logger?.LogInformation("Certificate stored for {Domain}", domain);
        return info;
    }

    /// <summary>
    /// Returns all tracked certificates.
    /// </summary>
    public IReadOnlyDictionary<string, CertInfo> GetCerts()
    {
        return _certs;
    }

    /// <summary>
    /// Revokes (deletes) the certificate files for a domain.
    /// </summary>
    public bool RevokeCert(string domain)
    {
        if (!_certs.TryGetValue(domain, out var info))
        {
            _logger?.LogWarning("No certificate found for {Domain}", domain);
            return false;
        }

        var domainDir = Path.GetDirectoryName(info.CertPath);
        if (domainDir != null && Directory.Exists(domainDir))
        {
            try
            {
                Directory.Delete(domainDir, recursive: true);
                _logger?.LogInformation("Deleted certificate directory for {Domain}", domain);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to delete certificate directory for {Domain}", domain);
                return false;
            }
        }

        _certs.Remove(domain);
        return true;
    }

    private void ScanExistingCerts()
    {
        if (!Directory.Exists(_certsBaseDir))
            return;

        foreach (var dir in Directory.GetDirectories(_certsBaseDir))
        {
            var certFile = Path.Combine(dir, "cert.pem");
            var keyFile = Path.Combine(dir, "key.pem");

            if (!File.Exists(certFile) || !File.Exists(keyFile))
                continue;

            var domain = Path.GetFileName(dir);
            var created = File.GetCreationTimeUtc(certFile);
            _certs[domain] = new CertInfo(domain, certFile, keyFile, created, []);

            _logger?.LogDebug("Found existing cert for {Domain}", domain);
        }
    }
}

/// <summary>
/// Tracked certificate metadata.
/// </summary>
public record CertInfo(
    string Domain,
    string CertPath,
    string KeyPath,
    DateTime CreatedUtc,
    string[] Aliases
);
