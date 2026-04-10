using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Plugin.PHP;

/// <summary>
/// Runs Composer commands using a specific PHP version, using the correct php.ini.
/// Downloads composer.phar if not already present in the NKS WDC appDir.
/// </summary>
public sealed class ComposerIntegration
{
    private readonly ILogger<ComposerIntegration> _logger;
    private readonly string _appDirectory;

    private const string ComposerPharUrl = "https://getcomposer.org/composer-stable.phar";

    public ComposerIntegration(ILogger<ComposerIntegration> logger, string appDirectory)
    {
        _logger = logger;
        _appDirectory = appDirectory;
    }

    public string ComposerPharPath => Path.Combine(_appDirectory, "bin", "composer.phar");

    /// <summary>
    /// Ensures composer.phar is present. Downloads it if missing.
    /// </summary>
    public async Task EnsureComposerAsync(CancellationToken ct = default)
    {
        var path = ComposerPharPath;
        if (File.Exists(path))
        {
            _logger.LogDebug("Composer already present at {Path}", path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _logger.LogInformation("Downloading Composer from {Url}", ComposerPharUrl);

        using var http = new HttpClient();
        await using var stream = await http.GetStreamAsync(ComposerPharUrl, ct);
        await using var file = File.Create(path);
        await stream.CopyToAsync(file, ct);

        _logger.LogInformation("Composer downloaded to {Path}", path);
    }

    /// <summary>
    /// Runs a Composer command in the given working directory using the specified PHP version.
    /// Streams output line-by-line to the provided callback.
    /// </summary>
    public async Task RunAsync(
        PhpInstallation php,
        string iniDirectory,
        string workingDirectory,
        IReadOnlyList<string> composerArgs,
        Action<string>? outputCallback = null,
        CancellationToken ct = default)
    {
        await EnsureComposerAsync(ct);

        var iniPath = Path.Combine(iniDirectory, php.MajorMinor, "php-cli.ini");
        var phpEnv = new Dictionary<string, string?>
        {
            ["PHPRC"] = Path.GetDirectoryName(iniPath),
            ["COMPOSER_HOME"] = Path.Combine(_appDirectory, "composer-home")
        };

        // Build argument list: [composer.phar, ...composerArgs]
        var args = new List<string> { ComposerPharPath };
        args.AddRange(composerArgs);
        args.Add("--no-interaction");

        _logger.LogInformation("Running Composer {Args} in {Dir} with PHP {Version}",
            string.Join(" ", composerArgs), workingDirectory, php.Version);

        var cmd = Cli.Wrap(php.ExecutablePath)
            .WithArguments(args)
            .WithWorkingDirectory(workingDirectory)
            .WithEnvironmentVariables(phpEnv)
            .WithValidation(CommandResultValidation.None);

        if (outputCallback is not null)
        {
            await cmd
                .WithStandardOutputPipe(PipeTarget.ToDelegate(outputCallback))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line => outputCallback($"[ERR] {line}")))
                .ExecuteAsync(ct);
        }
        else
        {
            var result = await cmd.ExecuteBufferedAsync(ct);
            if (result.ExitCode != 0)
            {
                _logger.LogError("Composer failed (exit {Code}):\n{Output}",
                    result.ExitCode, result.StandardError);
                throw new InvalidOperationException(
                    $"Composer exited with code {result.ExitCode}: {result.StandardError.Trim()}");
            }
        }
    }

    /// <summary>
    /// Returns the Composer version string (requires PHP).
    /// </summary>
    public async Task<string?> GetVersionAsync(PhpInstallation php, CancellationToken ct = default)
    {
        await EnsureComposerAsync(ct);

        var result = await Cli.Wrap(php.ExecutablePath)
            .WithArguments([ComposerPharPath, "--version", "--no-interaction"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0) return null;
        // "Composer version 2.7.2 2024-03-11 17:12:18"
        return result.StandardOutput.Trim();
    }
}
