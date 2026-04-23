using System.Diagnostics;
using System.Runtime.InteropServices;
using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;
using NKS.WebDevConsole.Core.Services;

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
    /// Scans NKS WDC managed binaries for mkcert. Pure filesystem walk —
    /// wrapped in Task for interface compatibility but does not await.
    /// </summary>
    public Task<bool> DetectAsync()
    {
        // Priority 1: own binary under ~/.wdc/binaries/mkcert/
        var mkcertRoot = Path.Combine(WdcPaths.BinariesRoot, "mkcert");

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
                    return Task.FromResult(true);
                }
                // FiloSottile/mkcert releases publish assets with the
                // platform suffix baked in (mkcert-v1.4.4-darwin-arm64,
                // mkcert-v1.4.4-windows-amd64.exe); the BinaryDownloader
                // extracts them verbatim without renaming to a stable
                // "mkcert" symlink. Match the canonical "mkcert-v*"
                // prefix so detection works straight out of the wizard
                // without an extra symlink step.
                var match = Directory.EnumerateFiles(vdir, "mkcert-v*")
                    .FirstOrDefault(f =>
                    {
                        var name = Path.GetFileName(f);
                        return OperatingSystem.IsWindows()
                            ? name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            : !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                    });
                if (match is not null)
                {
                    _mkcertPath = match;
                    _logger.LogInformation("Found mkcert at {Path}", match);
                    return Task.FromResult(true);
                }
            }
        }

        _logger.LogWarning(
            "mkcert not found. Install with POST /api/binaries/install {{ \"app\": \"mkcert\", \"version\": \"1.4.4\" }}");
        return Task.FromResult(false);
    }

    /// <summary>
    /// Installs the local CA into the system trust store.
    /// On macOS/Linux the underlying <c>security add-trusted-cert</c> (or
    /// the NSS/CA-bundle update) needs root; when the direct call fails
    /// with a permission error we retry through an OS-native elevation
    /// prompt (osascript / pkexec) so the user is asked for their
    /// password once instead of silently hitting exit 1.
    /// </summary>
    public async Task<bool> InstallCaAsync()
        => await RunMkcertTrustStoreCommandAsync("-install", "install");

    /// <summary>
    /// Removes the local CA from the system trust store (<c>mkcert -uninstall</c>).
    /// Same elevation path as <see cref="InstallCaAsync"/>.
    /// </summary>
    public async Task<bool> UninstallCaAsync()
        => await RunMkcertTrustStoreCommandAsync("-uninstall", "uninstall");

    private async Task<bool> RunMkcertTrustStoreCommandAsync(string arg, string verb)
    {
        if (_mkcertPath is null)
        {
            _logger.LogError("Cannot {Verb} CA: mkcert not detected", verb);
            return false;
        }

        try
        {
            var result = await Cli.Wrap(_mkcertPath)
                .WithArguments(arg)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (result.ExitCode == 0)
            {
                _logger.LogInformation("mkcert CA {Verb}ed successfully", verb);
                return true;
            }

            // On macOS/Linux, mkcert needs root to touch the system trust
            // store. Detect that flavour of failure and retry through the
            // platform's native elevation prompt.
            if (!OperatingSystem.IsWindows() && LooksLikePermissionFailure(result))
            {
                _logger.LogWarning(
                    "[mkcert] direct {Verb} failed (exit {Code}), requesting elevation prompt",
                    verb, result.ExitCode);
                return await RunMkcertWithElevationAsync(arg, verb);
            }

            _logger.LogError("mkcert {Arg} failed (exit {Code}): {StdErr}",
                arg, result.ExitCode, result.StandardError);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to {Verb} mkcert CA", verb);
            return false;
        }
    }

    private static bool LooksLikePermissionFailure(BufferedCommandResult result)
    {
        if (result.ExitCode == 0) return false;
        var haystack = (result.StandardError ?? string.Empty) + "\n" + (result.StandardOutput ?? string.Empty);
        return result.ExitCode != 0 && (
            haystack.Contains("security add-trusted-cert", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("SecTrustSettingsSetTrustSettings", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("must be run as root", StringComparison.OrdinalIgnoreCase) ||
            haystack.Contains("sudo", StringComparison.OrdinalIgnoreCase) ||
            // Bare exit 1 on mac with empty stderr is the typical
            // "security add-trusted-cert prompted for password in a
            // non-TTY and was denied" signature — fall through to the
            // elevation path rather than give up.
            string.IsNullOrWhiteSpace(haystack.Trim()));
    }

    /// <summary>
    /// Re-runs the same <c>mkcert &lt;arg&gt;</c> invocation but under an
    /// OS-native elevation prompt so the user can type their password in
    /// a real dialog instead of the (hidden) non-TTY prompt that CliWrap
    /// swallows.
    ///
    /// macOS: <c>osascript -e 'do shell script "..." with administrator privileges'</c>.
    /// The AppleScript uses <c>quoted form of</c> around every dynamic
    /// value so shell metacharacters in paths can't break out.
    ///
    /// Linux: <c>pkexec</c> with <c>ArgumentList</c>, which needs no
    /// escaping because it's not a shell.
    ///
    /// Both paths forward <c>CAROOT</c> explicitly — mkcert reads it from
    /// the environment, and under osascript/pkexec the elevated shell
    /// gets a fresh environment, so without this the elevated mkcert
    /// would read/write a different CA than the non-elevated detect +
    /// generate calls.
    /// </summary>
    private async Task<bool> RunMkcertWithElevationAsync(string arg, string verb)
    {
        if (_mkcertPath is null) return false;

        var caroot = Environment.GetEnvironmentVariable("CAROOT");

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Build AppleScript that:
                //   (env CAROOT=... )? <mkcert> <arg>
                // with every dynamic token wrapped in `quoted form of`
                // so the shell receives properly quoted paths.
                var sb = new System.Text.StringBuilder();
                sb.Append("do shell script ");
                if (!string.IsNullOrEmpty(caroot))
                {
                    sb.Append("\"/usr/bin/env CAROOT=\" & quoted form of \"")
                      .Append(EscapeForAppleScriptString(caroot))
                      .Append("\" & \" \" & quoted form of \"")
                      .Append(EscapeForAppleScriptString(_mkcertPath))
                      .Append("\" & \" ")
                      .Append(arg)
                      .Append("\"");
                }
                else
                {
                    sb.Append("quoted form of \"")
                      .Append(EscapeForAppleScriptString(_mkcertPath))
                      .Append("\" & \" ")
                      .Append(arg)
                      .Append("\"");
                }
                sb.Append(" with prompt \"NKS WebDev Console needs permission to ")
                  .Append(verb)
                  .Append(" the local mkcert certificate authority.\" with administrator privileges");

                var script = sb.ToString();

                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                using var p = Process.Start(psi);
                if (p is null)
                {
                    _logger.LogWarning("[mkcert] user cancelled / failed: osascript did not start");
                    return false;
                }
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    var err = (await p.StandardError.ReadToEndAsync()).Trim();
                    if (err.Contains("User canceled", StringComparison.OrdinalIgnoreCase)
                        || err.Contains("(-128)", StringComparison.Ordinal))
                    {
                        _logger.LogWarning("[mkcert] user cancelled / failed: cancelled at password prompt");
                    }
                    else
                    {
                        _logger.LogWarning("[mkcert] user cancelled / failed: osascript exit {Code}: {Err}",
                            p.ExitCode, err);
                    }
                    return false;
                }

                _logger.LogInformation("[mkcert] CA {Verb}ed successfully (via elevation)", verb);
                return true;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // pkexec: no shell, so arguments are passed literally via
                // ArgumentList. Prefix with `env CAROOT=...` so the CA
                // path survives the privilege boundary.
                var psi = new ProcessStartInfo
                {
                    FileName = "pkexec",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                if (!string.IsNullOrEmpty(caroot))
                {
                    psi.ArgumentList.Add("env");
                    psi.ArgumentList.Add($"CAROOT={caroot}");
                }
                psi.ArgumentList.Add(_mkcertPath);
                psi.ArgumentList.Add(arg);

                using var p = Process.Start(psi);
                if (p is null)
                {
                    _logger.LogWarning("[mkcert] user cancelled / failed: pkexec did not start");
                    return false;
                }
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    var err = (await p.StandardError.ReadToEndAsync()).Trim();
                    // pkexec exit 126 = not authorised, 127 = dismissed.
                    _logger.LogWarning("[mkcert] user cancelled / failed: pkexec exit {Code}: {Err}",
                        p.ExitCode, err);
                    return false;
                }

                _logger.LogInformation("[mkcert] CA {Verb}ed successfully (via elevation)", verb);
                return true;
            }

            _logger.LogWarning("[mkcert] user cancelled / failed: elevation not supported on this platform");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[mkcert] user cancelled / failed: {Message}", ex.Message);
            return false;
        }
    }

    private static string EscapeForAppleScriptString(string value)
    {
        // AppleScript strings use backslash escapes for " and \ only.
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
