using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Plugin.PHP;

public record PhpExtension(
    string Name,        // "pdo_mysql"
    bool IsLoaded,      // currently active in this PHP process
    bool IsCore,        // built-in (cannot be disabled)
    string? SoFile      // "pdo_mysql.so" or "php_pdo_mysql.dll"
);

/// <summary>
/// Manages PHP extensions per installed version.
/// Reads available .so/.dll files from the extension_dir, cross-references with loaded extensions.
/// Writes enable/disable state to php.ini (managed by PhpIniManager).
/// </summary>
public sealed class PhpExtensionManager
{
    private readonly ILogger<PhpExtensionManager> _logger;

    public PhpExtensionManager(ILogger<PhpExtensionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns all extensions available for a given PHP installation:
    /// both loaded (from php -m) and available-but-not-loaded (from extension_dir *.so/*.dll files).
    /// </summary>
    public async Task<IReadOnlyList<PhpExtension>> GetExtensionsAsync(
        PhpInstallation php,
        CancellationToken ct = default)
    {
        var loaded = await GetLoadedExtensionsAsync(php.ExecutablePath, ct);
        var available = GetAvailableExtensionFiles(php.ExecutablePath);
        var results = new Dictionary<string, PhpExtension>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in loaded)
            results[ext] = new PhpExtension(ext, true, IsCoreExtension(ext), null);

        foreach (var soFile in available)
        {
            var name = ExtractExtensionName(soFile);
            if (!results.ContainsKey(name))
                results[name] = new PhpExtension(name, false, false, Path.GetFileName(soFile));
            else
            {
                // Update with the .so filename
                var existing = results[name];
                results[name] = existing with { SoFile = Path.GetFileName(soFile) };
            }
        }

        return results.Values.OrderBy(x => x.Name).ToList();
    }

    private async Task<HashSet<string>> GetLoadedExtensionsAsync(string phpExe, CancellationToken ct)
    {
        var result = await Cli.Wrap(phpExe)
            .WithArguments(["-m"])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !trimmed.StartsWith('['))
                extensions.Add(trimmed.ToLowerInvariant());
        }
        return extensions;
    }

    private static IEnumerable<string> GetAvailableExtensionFiles(string phpExe)
    {
        var dir = Path.GetDirectoryName(phpExe) ?? string.Empty;
        var extDir = Path.Combine(dir, "ext");

        if (!Directory.Exists(extDir))
            extDir = dir;

        var pattern = OperatingSystem.IsWindows() ? "php_*.dll" : "*.so";

        return Directory.Exists(extDir)
            ? Directory.GetFiles(extDir, pattern)
            : [];
    }

    private static string ExtractExtensionName(string soPath)
    {
        var name = Path.GetFileNameWithoutExtension(soPath);
        // "php_pdo_mysql.dll" → "pdo_mysql"; "pdo_mysql.so" → "pdo_mysql"
        if (name.StartsWith("php_", StringComparison.OrdinalIgnoreCase))
            name = name[4..];
        return name.ToLowerInvariant();
    }

    private static readonly HashSet<string> CoreExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "core", "date", "ereg", "libxml", "pcre", "reflection",
        "spl", "standard", "zend opcache"
    };

    private static bool IsCoreExtension(string name)
        => CoreExtensions.Contains(name);

    /// <summary>
    /// Default set of dev extensions — enabled if the corresponding .dll/.so file exists in ext dir.
    /// Commonly needed by PHP frameworks (Nette, Laravel, Symfony, WordPress, etc.).
    /// Built-in modules (ctype, dom, iconv, json, mbstring on some builds, session, tokenizer,
    /// xml family, zlib, hash, pcre, pdo, simplexml, spl) are already loaded into php.exe and
    /// do not need explicit "extension=" directives — listing them here is harmless on Windows
    /// (PHP ignores duplicate loads of statically linked modules).
    /// </summary>
    private static readonly string[] DefaultDevExtensions =
    [
        // Database drivers — pdo_mysql is HARD requirement of nette/database
        "pdo_mysql", "mysqli", "pdo_sqlite", "sqlite3", "pdo_pgsql", "pgsql",
        // Networking / IO
        "curl", "openssl", "sockets", "ftp", "ldap", "soap",
        // String / charset
        "mbstring", "intl", "gettext",
        // Files / mime / images / archives
        "fileinfo", "gd", "exif", "zip", "bz2", "xsl",
        // Crypto / math
        "sodium", "gmp", "bcmath", "calendar",
        // HTML / output
        "tidy",
    ];

    /// <summary>
    /// Returns the list of extensions to enable by default in php.ini for the given installation,
    /// filtered to only those actually present in the ext directory.
    /// </summary>
    public IReadOnlyList<(string Name, bool Enabled)> GetDefaultEnabledExtensions(PhpInstallation php)
    {
        var available = GetAvailableExtensionFiles(php.ExecutablePath)
            .Select(ExtractExtensionName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return DefaultDevExtensions
            .Where(name => available.Contains(name))
            .Select(name => (name, true))
            .ToList();
    }

    /// <summary>
    /// Returns the xdebug .so/.dll path for the given PHP installation, if present.
    /// </summary>
    public string? FindXdebugSo(PhpInstallation php)
    {
        var dir = Path.Combine(Path.GetDirectoryName(php.ExecutablePath) ?? "", "ext");
        if (!Directory.Exists(dir)) dir = Path.GetDirectoryName(php.ExecutablePath) ?? "";

        var pattern = OperatingSystem.IsWindows() ? "php_xdebug*.dll" : "xdebug.so";
        var matches = Directory.Exists(dir)
            ? Directory.GetFiles(dir, pattern)
            : [];

        return matches.FirstOrDefault();
    }
}
