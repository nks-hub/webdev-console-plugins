using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Plugin.PHP;

public record PhpInstallation(
    string Version,         // "8.4.20"
    string MajorMinor,      // "8.4"
    string ExecutablePath,  // full path to php / php.exe
    string? FpmExecutable,  // php-fpm path (Unix) or php-cgi.exe (Windows)
    string Directory,       // parent directory
    int FcgiPort,           // deterministic port (see plugin.json)
    string[] Extensions     // available extension names from ext/ dir
);

/// <summary>
/// Detects PHP installations under <c>~/.wdc/binaries/php/{version}/</c>.
/// NKS WDC manages its own PHP binaries — this manager does NOT scan MAMP, WAMP,
/// Laragon, Homebrew, system PATH, or any third-party install location.
/// Use BinaryManager (in the daemon) to download new versions.
/// Port assignment: 90{major}{minor} — e.g., 8.2 → 9082, 7.4 → 9074, 5.6 → 9056.
/// </summary>
public sealed class PhpVersionManager
{
    private readonly ILogger<PhpVersionManager> _logger;

    /// <summary>Active (selected) PHP version for new sites. Defaults to highest detected.</summary>
    public string? ActiveVersion { get; private set; }

    /// <summary>Root directory containing per-version PHP installs.</summary>
    public string BinariesRoot { get; }

    // Deterministic port map matches plugin.json phpVersionPortMap
    private static readonly Dictionary<string, int> PortMap = new()
    {
        ["5.5"] = 9055, ["5.6"] = 9056,
        ["7.0"] = 9070, ["7.1"] = 9071, ["7.2"] = 9072,
        ["7.3"] = 9073, ["7.4"] = 9074,
        ["8.0"] = 9080, ["8.1"] = 9081, ["8.2"] = 9082, ["8.3"] = 9083, ["8.4"] = 9084,
        ["8.5"] = 9085
    };

    public PhpVersionManager(ILogger<PhpVersionManager> logger)
    {
        _logger = logger;
        BinariesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".wdc", "binaries", "php");
    }

    public async Task<IReadOnlyList<PhpInstallation>> DetectAllAsync(
        string appDirectory,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(BinariesRoot))
        {
            _logger.LogWarning(
                "PHP binaries directory does not exist: {Path}. " +
                "Use POST /api/binaries/install with {{ \"app\": \"php\", \"version\": \"8.4.20\" }} to install a version.",
                BinariesRoot);
            return Array.Empty<PhpInstallation>();
        }

        var results = new List<PhpInstallation>();
        var exe = OperatingSystem.IsWindows() ? "php.exe" : "php";

        foreach (var versionDir in Directory.GetDirectories(BinariesRoot))
        {
            var version = Path.GetFileName(versionDir);
            var phpExe = Path.Combine(versionDir, exe);

            // Some archives extract into nested layouts (e.g. an inner bin/) — try a few common subdirs
            if (!File.Exists(phpExe))
            {
                foreach (var sub in new[] { "bin", "" })
                {
                    var candidate = Path.Combine(versionDir, sub, exe);
                    if (File.Exists(candidate)) { phpExe = candidate; break; }
                }
            }

            if (!File.Exists(phpExe))
            {
                _logger.LogDebug("Skipping {Dir}: no {Exe} found", versionDir, exe);
                continue;
            }

            var installation = await ProbePhpAsync(phpExe, ct);
            if (installation is not null)
            {
                results.Add(installation);
                _logger.LogInformation("Detected PHP {Version} at {Path}", installation.Version, phpExe);
            }
        }

        var ordered = results.OrderByDescending(x => Version.TryParse(x.Version, out var v) ? v : new Version()).ToList();

        // Default active version = highest detected
        if (ordered.Count > 0)
            ActiveVersion ??= ordered[0].MajorMinor;

        return ordered;
    }

    /// <summary>Sets the active PHP version used for new sites.</summary>
    public void SetActiveVersion(string majorMinor)
    {
        if (!PortMap.ContainsKey(majorMinor))
            throw new ArgumentException($"Unknown PHP version: {majorMinor}");
        ActiveVersion = majorMinor;
        _logger.LogInformation("Active PHP version set to {Version}", majorMinor);
    }

    private async Task<PhpInstallation?> ProbePhpAsync(string phpPath, CancellationToken ct)
    {
        try
        {
            var result = await Cli.Wrap(phpPath)
                .WithArguments(["-r", "echo PHP_VERSION;"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0)
                return null;

            var version = result.StandardOutput.Trim();
            if (!IsValidPhpVersion(version))
                return null;

            var majorMinor = string.Join(".", version.Split('.').Take(2));
            if (!PortMap.TryGetValue(majorMinor, out var port))
                port = 9000 + int.Parse(version.Split('.')[0]) * 10 + int.Parse(version.Split('.')[1]);

            var dir = Path.GetDirectoryName(phpPath)!;
            var fpmExe = FindFpmExecutable(dir, majorMinor);
            var extensions = ScanAvailableExtensions(dir);

            return new PhpInstallation(version, majorMinor, phpPath, fpmExe, dir, port, extensions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("PHP probe failed for {Path}: {Message}", phpPath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Scans the ext/ directory for available extension DLLs/SOs without executing PHP.
    /// </summary>
    private static string[] ScanAvailableExtensions(string phpDir)
    {
        var extDir = Path.Combine(phpDir, "ext");
        if (!Directory.Exists(extDir))
            return [];

        var pattern = OperatingSystem.IsWindows() ? "php_*.dll" : "*.so";
        return Directory.GetFiles(extDir, pattern)
            .Select(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                // "php_pdo_mysql.dll" → "pdo_mysql"; "pdo_mysql.so" → "pdo_mysql"
                if (name.StartsWith("php_", StringComparison.OrdinalIgnoreCase))
                    name = name[4..];
                return name.ToLowerInvariant();
            })
            .OrderBy(n => n)
            .ToArray();
    }

    private static string? FindFpmExecutable(string phpDir, string majorMinor)
    {
        if (OperatingSystem.IsWindows())
        {
            var cgi = Path.Combine(phpDir, "php-cgi.exe");
            return File.Exists(cgi) ? cgi : null;
        }

        foreach (var candidate in new[]
        {
            Path.Combine(phpDir, "php-fpm"),
            Path.Combine(phpDir, "sbin", "php-fpm"),
        })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static bool IsValidPhpVersion(string v)
    {
        var parts = v.Split('.');
        return parts.Length >= 3
            && int.TryParse(parts[0], out var major) && major >= 5
            && int.TryParse(parts[1], out _)
            && int.TryParse(parts[2].Split('-')[0], out _);
    }

    public static int GetPortForVersion(string majorMinor)
        => PortMap.TryGetValue(majorMinor, out var p) ? p : throw new ArgumentException($"Unknown PHP version: {majorMinor}");
}
