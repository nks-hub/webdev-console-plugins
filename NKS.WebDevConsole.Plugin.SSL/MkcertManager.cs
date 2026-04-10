using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Plugin.SSL;

/// <summary>
/// Wraps mkcert CLI for CA installation and certificate generation.
/// </summary>
public class MkcertManager
{
    private readonly ILogger<MkcertManager> _logger;
    private string? _mkcertPath;

    public MkcertManager(ILogger<MkcertManager> logger)
    {
        _logger = logger;
    }

    public bool IsInstalled => _mkcertPath != null;
    public string? MkcertPath => _mkcertPath;

    /// <summary>
    /// Scans NKS WDC managed binaries for mkcert.
    /// </summary>
    public async Task<bool> DetectAsync()
    {
        // Priority 1: own binary under ~/.wdc/binaries/mkcert/
        var mkcertRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc", "binaries", "mkcert");

        if (Directory.Exists(mkcertRoot))
        {
            var exeName = OperatingSystem.IsWindows() ? "mkcert.exe" : "mkcert";
            foreach (var vdir in Directory.GetDirectories(mkcertRoot)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderByDescending(d => d))
            {
                var candidate = Path.Combine(vdir, exeName);
                if (File.Exists(candidate))
                {
                    _mkcertPath = candidate;
                    _logger.LogInformation("Found mkcert at {Path}", candidate);
                    return true;
                }
            }
        }

        _logger.LogWarning(
            "mkcert not found. Install with POST /api/binaries/install {{ \"app\": \"mkcert\", \"version\": \"1.4.4\" }}");
        return false;
    }

    /// <summary>
    /// Installs the local CA into the system trust store.
    /// </summary>
    public async Task<bool> InstallCaAsync()
    {
        if (_mkcertPath is null)
        {
            _logger.LogError("Cannot install CA: mkcert not detected");
            return false;
        }

        try
        {
            var result = await Cli.Wrap(_mkcertPath)
                .WithArguments("-install")
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode == 0)
            {
                _logger.LogInformation("mkcert CA installed successfully");
                return true;
            }

            _logger.LogError("mkcert -install failed (exit {Code}): {StdErr}",
                result.ExitCode, result.StandardError);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install mkcert CA");
            return false;
        }
    }

    /// <summary>
    /// Generates a certificate and key for the given domain and aliases.
    /// </summary>
    public async Task<(string CertPath, string KeyPath)?> GenerateCertAsync(
        string domain, string[] aliases, string outputDir)
    {
        if (_mkcertPath is null)
        {
            _logger.LogError("Cannot generate cert: mkcert not detected");
            return null;
        }

        Directory.CreateDirectory(outputDir);

        var certPath = Path.Combine(outputDir, "cert.pem");
        var keyPath = Path.Combine(outputDir, "key.pem");

        var args = new List<string>
        {
            "-cert-file", certPath,
            "-key-file", keyPath,
            domain
        };
        args.AddRange(aliases);

        try
        {
            var result = await Cli.Wrap(_mkcertPath)
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode == 0)
            {
                _logger.LogInformation("Certificate generated for {Domain} at {Dir}", domain, outputDir);
                return (certPath, keyPath);
            }

            _logger.LogError("mkcert cert generation failed (exit {Code}): {StdErr}",
                result.ExitCode, result.StandardError);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate certificate for {Domain}", domain);
            return null;
        }
    }
}
