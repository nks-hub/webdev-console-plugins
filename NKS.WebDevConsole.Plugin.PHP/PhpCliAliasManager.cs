using System.Reflection;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;

namespace NKS.WebDevConsole.Plugin.PHP;

/// <summary>
/// Creates CLI shim scripts so developers can run php74, php82, php84 etc. from any terminal.
/// Windows: .cmd scripts written to a NKS WDC-managed bin dir that should be on PATH.
/// Unix: shell scripts written to a shims dir, symlinked from /usr/local/bin.
/// </summary>
public sealed class PhpCliAliasManager
{
    private readonly ILogger<PhpCliAliasManager> _logger;
    private readonly Template _cmdTemplate;

    public PhpCliAliasManager(ILogger<PhpCliAliasManager> logger)
    {
        _logger = logger;
        _cmdTemplate = LoadTemplate();
    }

    /// <summary>
    /// Creates / updates the shim for a given PHP installation.
    /// Returns the path to the created shim file.
    /// </summary>
    public async Task<string> CreateShimAsync(
        PhpInstallation php,
        string shimDirectory,
        string iniDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(shimDirectory);

        var tag = php.MajorMinor.Replace(".", "");  // "8.2" → "82"
        var iniDir = Path.Combine(iniDirectory, php.MajorMinor);

        if (OperatingSystem.IsWindows())
            return await CreateWindowsShimAsync(php, shimDirectory, tag, iniDir, ct);
        else
            return await CreateUnixShimAsync(php, shimDirectory, tag, iniDir, ct);
    }

    private async Task<string> CreateWindowsShimAsync(
        PhpInstallation php,
        string shimDir,
        string tag,
        string iniDir,
        CancellationToken ct)
    {
        var shimPath = Path.Combine(shimDir, $"php{tag}.cmd");

        var scriptObj = new ScriptObject();
        scriptObj.Import(new
        {
            version = php.Version,
            version_tag = tag,
            php_exe = php.ExecutablePath,
            ini_dir = iniDir
        });

        var ctx = new TemplateContext();
        ctx.PushGlobal(scriptObj);
        var content = _cmdTemplate.Render(ctx);

        await File.WriteAllTextAsync(shimPath, content, ct);
        _logger.LogInformation("Created Windows PHP shim: {Path}", shimPath);
        return shimPath;
    }

    private async Task<string> CreateUnixShimAsync(
        PhpInstallation php,
        string shimDir,
        string tag,
        string iniDir,
        CancellationToken ct)
    {
        var shimPath = Path.Combine(shimDir, $"php{tag}");

        var content = $"""
            #!/bin/sh
            # NKS WDC PHP {php.Version} shim
            PHPRC="{iniDir}" exec "{php.ExecutablePath}" "$@"
            """;

        await File.WriteAllTextAsync(shimPath, content, ct);

        // Make executable
        var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "chmod",
            Arguments = $"+x \"{shimPath}\"",
            UseShellExecute = false
        });
        if (chmod is not null)
            await chmod.WaitForExitAsync(ct);

        _logger.LogInformation("Created Unix PHP shim: {Path}", shimPath);
        return shimPath;
    }

    /// <summary>
    /// Creates shims for all detected PHP installations.
    /// Returns a mapping of alias name → shim path.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> CreateAllShimsAsync(
        IReadOnlyList<PhpInstallation> installations,
        string shimDirectory,
        string iniDirectory,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        foreach (var php in installations)
        {
            var shimPath = await CreateShimAsync(php, shimDirectory, iniDirectory, ct);
            result[$"php{php.MajorMinor.Replace(".", "")}"] = shimPath;
        }

        if (OperatingSystem.IsWindows())
            EnsureOnWindowsPath(shimDirectory);

        return result;
    }

    private void EnsureOnWindowsPath(string shimDir)
    {
        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        if (!userPath.Contains(shimDir, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                $"{shimDir}{Path.PathSeparator}{userPath}",
                EnvironmentVariableTarget.User);
            _logger.LogInformation("Added {Dir} to user PATH for PHP shims", shimDir);
        }
    }

    private static Template LoadTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{asm.GetName().Name}.Templates.php-shim.cmd.scriban";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        var tpl = Template.Parse(reader.ReadToEnd());
        if (tpl.HasErrors)
            throw new InvalidOperationException(string.Join("; ", tpl.Messages));
        return tpl;
    }
}
