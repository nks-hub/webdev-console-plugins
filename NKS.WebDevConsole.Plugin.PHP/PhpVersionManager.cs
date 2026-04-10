using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Plugin.PHP;

public record PhpInstallation(
    string Version,         // "8.2.25"
    string MajorMinor,      // "8.2"
    string ExecutablePath,  // full path to php / php.exe
    string? FpmExecutable,  // php-fpm path (Unix) or php-cgi.exe (Windows)
    string Directory,       // parent directory
    int FcgiPort,           // deterministic port (see plugin.json)
    string[] Extensions     // available extension names from ext/ dir
);

/// <summary>
/// Detects all PHP installations on the machine, across any PHP version from 5.6 to 8.4.
/// Port assignment: 90{major}{minor} — e.g., 8.2 → 9082, 7.4 → 9074, 5.6 → 9056.
/// </summary>
public sealed partial class PhpVersionManager
{
    private readonly ILogger<PhpVersionManager> _logger;

    /// <summary>Active (selected) PHP version for new sites. Defaults to highest detected.</summary>
    public string? ActiveVersion { get; private set; }

    // Deterministic port map matches plugin.json phpVersionPortMap
    private static readonly Dictionary<string, int> PortMap = new()
    {
        ["5.5"] = 9055, ["5.6"] = 9056,
        ["7.0"] = 9070, ["7.1"] = 9071, ["7.2"] = 9072,
        ["7.3"] = 9073, ["7.4"] = 9074,
        ["8.0"] = 9080, ["8.1"] = 9081, ["8.2"] = 9082, ["8.3"] = 9083, ["8.4"] = 9084
    };

    /// <summary>Regex matching MAMP-style directory names like "php8.4.12", "php5.6.34".</summary>
    [GeneratedRegex(@"^php(\d+\.\d+\.\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex MampDirNameRegex();

    /// <summary>Well-known MAMP base paths per platform.</summary>
    private static readonly string[] MampBasePaths = OperatingSystem.IsWindows()
        ? [@"C:\MAMP\bin\php", @"D:\MAMP\bin\php"]
        : OperatingSystem.IsMacOS()
            ? ["/Applications/MAMP/bin/php"]
            : [];

    public PhpVersionManager(ILogger<PhpVersionManager> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<PhpInstallation>> DetectAllAsync(
        string appDirectory,
        CancellationToken ct = default)
    {
        var candidates = GatherCandidateExecutables(appDirectory);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<PhpInstallation>();

        foreach (var exe in candidates.Distinct())
        {
            if (!File.Exists(exe) || !seen.Add(exe))
                continue;

            var installation = await ProbePhpAsync(exe, ct);
            if (installation is not null)
            {
                results.Add(installation);
                _logger.LogInformation("Detected PHP {Version} at {Path}", installation.Version, exe);
            }
        }

        var ordered = results.OrderByDescending(x => x.MajorMinor).ToList();

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

    private IEnumerable<string> GatherCandidateExecutables(string appDirectory)
    {
        var exe = OperatingSystem.IsWindows() ? "php.exe" : "php";

        // 1. NKS WDC-managed installs in appDir/php/{version}/
        if (Directory.Exists(Path.Combine(appDirectory, "php")))
        {
            foreach (var vdir in System.IO.Directory.GetDirectories(Path.Combine(appDirectory, "php")))
                yield return Path.Combine(vdir, exe);
        }

        // 2. PATH entries
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir))
                yield return Path.Combine(dir.Trim(), exe);
        }

        // 3. MAMP — scan actual directories instead of guessing names
        foreach (var candidate in ScanMampDirectories(exe))
            yield return candidate;

        if (OperatingSystem.IsWindows())
        {
            // WAMP, Laragon, manual installs
            foreach (var drive in new[] { "C:\\", "D:\\" })
            {
                foreach (var ver in new[] { "5.6", "7.4", "8.0", "8.1", "8.2", "8.3", "8.4" })
                {
                    yield return Path.Combine(drive, "php", ver, exe);
                    yield return Path.Combine(drive, $"php{ver.Replace(".", "")}", exe);
                    yield return Path.Combine(drive, "laragon", "bin", "php", $"php-{ver}", exe);
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Homebrew multi-version
            foreach (var ver in new[] { "5.6", "7.4", "8.0", "8.1", "8.2", "8.3", "8.4" })
            {
                yield return $"/opt/homebrew/opt/php@{ver}/bin/php";
                yield return $"/usr/local/opt/php@{ver}/bin/php";
            }
        }
        else
        {
            // Ubuntu/Debian: ondrej/php PPA
            foreach (var ver in new[] { "5.6", "7.4", "8.0", "8.1", "8.2", "8.3", "8.4" })
                yield return $"/usr/bin/php{ver}";
        }
    }

    /// <summary>
    /// Scans MAMP php directories for real version folders (e.g. php8.4.12, php7.4.30).
    /// Handles both Windows layout (php.exe at root) and macOS layout (php in bin/).
    /// </summary>
    private IEnumerable<string> ScanMampDirectories(string exeName)
    {
        foreach (var basePath in MampBasePaths)
        {
            if (!System.IO.Directory.Exists(basePath))
                continue;

            string[] dirs;
            try
            {
                dirs = System.IO.Directory.GetDirectories(basePath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Cannot enumerate MAMP directory {Path}: {Msg}", basePath, ex.Message);
                continue;
            }

            foreach (var dir in dirs)
            {
                var dirName = Path.GetFileName(dir);
                if (!MampDirNameRegex().IsMatch(dirName))
                    continue;

                // Windows MAMP: php.exe is at root of the version dir
                yield return Path.Combine(dir, exeName);

                // macOS MAMP: php is in bin/ subdir
                if (!OperatingSystem.IsWindows())
                    yield return Path.Combine(dir, "bin", exeName);
            }
        }
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
        if (!System.IO.Directory.Exists(extDir))
            return [];

        var pattern = OperatingSystem.IsWindows() ? "php_*.dll" : "*.so";
        return System.IO.Directory.GetFiles(extDir, pattern)
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

        // Unix: look for php-fpm in same dir, or system paths
        foreach (var candidate in new[]
        {
            Path.Combine(phpDir, "php-fpm"),
            $"/usr/sbin/php-fpm{majorMinor}",
            $"/opt/homebrew/opt/php@{majorMinor}/sbin/php-fpm"
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
            && int.TryParse(parts[2].Split('-')[0], out _);  // handle "8.4.0-dev"
    }

    public static int GetPortForVersion(string majorMinor)
        => PortMap.TryGetValue(majorMinor, out var p) ? p : throw new ArgumentException($"Unknown PHP version: {majorMinor}");
}
