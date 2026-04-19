using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.Composer;

/// <summary>
/// Configuration for the Composer plugin. Holds paths to the PHP interpreter,
/// the composer.phar file (or a system shim), and the managed binaries root
/// where WDC downloads versioned composer.phar files.
/// </summary>
public sealed class ComposerConfig
{
    /// <summary>Root directory for NKS WDC managed Composer installs.</summary>
    public string BinariesRoot { get; set; } = Path.Combine(WdcPaths.BinariesRoot, "composer");

    /// <summary>
    /// Path to the PHP executable used to invoke composer.phar.
    /// Leave empty to rely on <c>php</c> being on PATH (system PHP).
    /// </summary>
    public string PhpPath { get; set; } = "php";

    /// <summary>
    /// Absolute path to <c>composer.phar</c> or a system binary shim (e.g. <c>/usr/bin/composer</c>).
    /// Populated by <see cref="ApplyOwnBinaryDefaults"/> or overridden by config.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// Scans <see cref="BinariesRoot"/> for versioned <c>composer.phar</c> subdirectories,
    /// picks the newest by semver folder name, and sets <see cref="ExecutablePath"/>.
    /// Falls back to <c>composer</c> on PATH if no managed phar is found.
    /// </summary>
    /// <returns><c>true</c> if a managed phar was found; <c>false</c> if the PATH fallback is used.</returns>
    public bool ApplyOwnBinaryDefaults()
    {
        // F33: Scan for a WDC-managed PHP binary FIRST so PhpPath is set
        // regardless of whether the composer phar is found. Without this
        // ordering, the original implementation returned early on phar-found
        // and never reached the PHP scan, leaving PhpPath = "php" which
        // fails on machines without system PHP on PATH.
        var phpBinariesRoot = Path.Combine(Path.GetDirectoryName(BinariesRoot)!, "php");
        if (Directory.Exists(phpBinariesRoot))
        {
            var phpVersionDirs = Directory.GetDirectories(phpBinariesRoot)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderByDescending(d => Path.GetFileName(d), SemverVersionComparer.Instance)
                .ToList();

            foreach (var vdir in phpVersionDirs)
            {
                var phpExe = OperatingSystem.IsWindows()
                    ? Path.Combine(vdir, "php.exe")
                    : Path.Combine(vdir, "php");
                if (File.Exists(phpExe))
                {
                    PhpPath = phpExe;
                    break;
                }
            }
        }

        if (Directory.Exists(BinariesRoot))
        {
            var versionDirs = Directory.GetDirectories(BinariesRoot)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderByDescending(d => Path.GetFileName(d), SemverVersionComparer.Instance)
                .ToList();

            foreach (var vdir in versionDirs)
            {
                var phar = Path.Combine(vdir, "composer.phar");
                if (File.Exists(phar))
                {
                    ExecutablePath = phar;
                    return true;
                }
            }
        }

        // Nothing found under BinariesRoot — fall back to the system composer shim.
        // ComposerInvoker will detect whether this resolves to a phar invocation
        // or a native binary by inspecting the extension.
        ExecutablePath = OperatingSystem.IsWindows() ? "composer.bat" : "composer";

        return false;
    }
}
