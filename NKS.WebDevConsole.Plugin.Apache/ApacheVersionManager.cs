using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Plugin.Apache;

public record ApacheInstallation(string ExecutablePath, Version Version, string ServerRoot);

/// <summary>
/// Detects installed Apache versions and manages downloads from Apache Lounge (Windows) or system packages.
/// </summary>
public sealed class ApacheVersionManager
{
    private readonly ILogger<ApacheVersionManager> _logger;

    // Canonical download for Apache Lounge (Windows x64 — VS17 build)
    private const string ApacheLoungeCdnBase = "https://www.apachelounge.com/download/VS17/binaries/";

    public ApacheVersionManager(ILogger<ApacheVersionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns all Apache installations found on the current machine.
    /// Scans: PATH, well-known install directories, and NKS WDC app-dir.
    /// </summary>
    public async Task<IReadOnlyList<ApacheInstallation>> DetectAllAsync(
        string appDirectory,
        CancellationToken ct = default)
    {
        var candidates = BuildSearchPaths(appDirectory);
        var results = new List<ApacheInstallation>();

        foreach (var candidate in candidates.Distinct())
        {
            if (!File.Exists(candidate))
                continue;

            var installation = await ProbeExecutableAsync(candidate, ct);
            if (installation is not null)
                results.Add(installation);
        }

        _logger.LogInformation("Detected {Count} Apache installation(s)", results.Count);
        return results;
    }

    private IEnumerable<string> BuildSearchPaths(string appDirectory)
    {
        var exe = OperatingSystem.IsWindows() ? "httpd.exe" : "httpd";

        // 1. NKS WDC bundled copy
        yield return Path.Combine(appDirectory, "apache", "bin", exe);

        // 2. PATH entries
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir))
                yield return Path.Combine(dir.Trim(), exe);
        }

        if (OperatingSystem.IsWindows())
        {
            // Apache Lounge default install locations
            foreach (var drive in new[] { "C:\\", "D:\\" })
            {
                yield return Path.Combine(drive, "Apache24", "bin", exe);
                yield return Path.Combine(drive, "Apache2.4", "bin", exe);
                yield return Path.Combine(drive, "wamp64", "bin", "apache", "apache2.4.58", "bin", exe);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin/" + exe;
            yield return "/usr/local/bin/" + exe;
            yield return "/usr/local/opt/httpd/bin/" + exe;
        }
        else
        {
            yield return "/usr/sbin/" + exe;
            yield return "/usr/bin/" + exe;
        }
    }

    private async Task<ApacheInstallation?> ProbeExecutableAsync(string path, CancellationToken ct)
    {
        try
        {
            var result = await Cli.Wrap(path)
                .WithArguments(["-v"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0)
                return null;

            // Output: "Server version: Apache/2.4.62 (Win64)"
            var versionLine = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.StartsWith("Server version:", StringComparison.OrdinalIgnoreCase));

            if (versionLine is null)
                return null;

            var versionStr = ExtractVersion(versionLine);
            if (!Version.TryParse(versionStr, out var version))
                return null;

            // Derive ServerRoot from binary path: /path/to/bin/httpd → /path/to
            var serverRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));

            return new ApacheInstallation(path, version, serverRoot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Probe failed for {Path}: {Message}", path, ex.Message);
            return null;
        }
    }

    private static string ExtractVersion(string versionLine)
    {
        // "Server version: Apache/2.4.62 (Win64)" → "2.4.62"
        var slash = versionLine.IndexOf('/');
        if (slash < 0) return string.Empty;

        var afterSlash = versionLine[(slash + 1)..];
        var space = afterSlash.IndexOf(' ');
        return space >= 0 ? afterSlash[..space] : afterSlash;
    }

    /// <summary>
    /// Downloads the latest Apache 2.4 from Apache Lounge for Windows into appDirectory.
    /// On Unix the caller should use system packages; this method throws on non-Windows.
    /// </summary>
    public async Task DownloadAndInstallAsync(
        string appDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Auto-download is only supported on Windows. Use your package manager.");

        // In production this would hit the Apache Lounge HTML page and scrape the latest filename.
        // Here we use the known stable filename for Apache 2.4.62 VS17 x64.
        const string fileName = "httpd-2.4.62-240904-win64-VS17.zip";
        var downloadUrl = ApacheLoungeCdnBase + fileName;
        var targetZip = Path.Combine(Path.GetTempPath(), fileName);
        var targetDir = Path.Combine(appDirectory, "apache");

        _logger.LogInformation("Downloading Apache from {Url}", downloadUrl);

        using var http = new HttpClient();
        using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(targetZip);
        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
                progress?.Report((double)downloaded / total);
        }

        await file.FlushAsync(ct);
        file.Close();

        _logger.LogInformation("Extracting Apache to {Dir}", targetDir);
        if (Directory.Exists(targetDir))
            Directory.Delete(targetDir, recursive: true);

        System.IO.Compression.ZipFile.ExtractToDirectory(targetZip, appDirectory);
        File.Delete(targetZip);

        _logger.LogInformation("Apache installed at {Dir}", targetDir);
    }
}
