using System.Reflection;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;

namespace NKS.WebDevConsole.Plugin.PHP;

public enum PhpIniProfile { Development, Production }
public enum PhpIniMode { Cli, Web }

public record PhpIniOptions(
    string Version,
    PhpIniProfile Profile,
    PhpIniMode Mode,
    string ExtDir,
    string ErrorLog,
    string TmpDir,
    string Timezone = "Europe/Prague",
    bool XdebugEnabled = false,
    string? XdebugSo = null,
    string XdebugMode = "debug",
    int XdebugPort = 9003,
    bool OpcacheEnabled = true,
    IReadOnlyList<(string Name, bool Enabled)>? Extensions = null
);

/// <summary>
/// Generates php.ini files from the embedded Scriban template for each PHP version and mode.
/// </summary>
public sealed class PhpIniManager
{
    private readonly ILogger<PhpIniManager> _logger;
    private readonly Template _template;

    public PhpIniManager(ILogger<PhpIniManager> logger)
    {
        _logger = logger;
        _template = LoadTemplate();
    }

    public string Render(PhpIniOptions opts)
    {
        bool isDev = opts.Profile == PhpIniProfile.Development;

        var exts = opts.Extensions?.Select(e => new { name = e.Name, enabled = e.Enabled }).ToArray()
            ?? [];

        var scriptObj = new ScriptObject();
        scriptObj.Import(new
        {
            version = opts.Version,
            version_tag = opts.Version.Replace(".", ""),
            profile = opts.Profile.ToString().ToLowerInvariant(),
            mode = opts.Mode.ToString().ToLowerInvariant(),
            ext_dir = opts.ExtDir.Replace('\\', '/'),
            error_log = opts.ErrorLog.Replace('\\', '/'),
            max_execution_time = opts.Mode == PhpIniMode.Cli ? 0 : 30,
            memory_limit = opts.Mode == PhpIniMode.Cli ? "512M" : "256M",
            error_reporting = isDev ? "E_ALL" : "E_ALL & ~E_DEPRECATED & ~E_STRICT",
            display_errors = isDev ? "On" : "Off",
            display_startup_errors = isDev ? "On" : "Off",
            post_max_size = "64M",
            upload_max_filesize = "32M",
            // Forward slashes so the template renders the same way the rest
            // of the Windows paths in php.ini do; PHP accepts either
            // separator on Windows, but mixing them in one file reads badly.
            temp_dir = Path.GetTempPath().TrimEnd('\\', '/').Replace('\\', '/'),
            timezone = opts.Timezone,
            xdebug_enabled = opts.XdebugEnabled,
            xdebug_so = opts.XdebugSo?.Replace('\\', '/') ?? string.Empty,
            xdebug_mode = opts.XdebugMode,
            xdebug_port = opts.XdebugPort,
            xdebug_log = $"/tmp/xdebug-php{opts.Version.Replace(".", "")}.log",
            opcache_enabled = opts.OpcacheEnabled && !opts.XdebugEnabled,
            opcache_revalidate_freq = isDev ? 0 : 60,
            opcache_validate_timestamps = isDev ? 1 : 0,
            extensions = exts
        });

        var ctx = new TemplateContext();
        ctx.PushGlobal(scriptObj);
        return _template.Render(ctx);
    }

    public async Task WriteAsync(PhpIniOptions opts, string outputPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var content = Render(opts);
        var tmp = outputPath + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, outputPath, overwrite: true);
        _logger.LogInformation("Wrote php.ini ({Mode}/{Profile}) to {Path}",
            opts.Mode, opts.Profile, outputPath);
    }

    /// <summary>
    /// Generates both CLI and Web php.ini files for the given installation into the config directory.
    /// Returns the path to the Web ini (used by FPM/CGI).
    /// </summary>
    public async Task<string> WriteAllVariantsAsync(
        PhpInstallation php,
        string configBaseDir,
        PhpIniOptions baseOpts,
        CancellationToken ct = default)
    {
        var versionDir = Path.Combine(configBaseDir, php.MajorMinor);
        Directory.CreateDirectory(versionDir);

        var extDir = Path.Combine(Path.GetDirectoryName(php.ExecutablePath)!, "ext");
        var logDir = Path.Combine(configBaseDir, "logs");
        Directory.CreateDirectory(logDir);

        var webOpts = baseOpts with
        {
            Mode = PhpIniMode.Web,
            ExtDir = extDir,
            ErrorLog = Path.Combine(logDir, $"php{php.MajorMinor.Replace(".", "")}-web-errors.log")
        };
        var cliOpts = baseOpts with
        {
            Mode = PhpIniMode.Cli,
            ExtDir = extDir,
            ErrorLog = Path.Combine(logDir, $"php{php.MajorMinor.Replace(".", "")}-cli-errors.log"),
            OpcacheEnabled = false  // OPcache off for CLI
        };

        var webPath = Path.Combine(versionDir, "php.ini");
        var cliPath = Path.Combine(versionDir, "php-cli.ini");

        await WriteAsync(webOpts, webPath, ct);
        await WriteAsync(cliOpts, cliPath, ct);

        return webPath;
    }

    private static Template LoadTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = $"{asm.GetName().Name}.Templates.php.ini.scriban";
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded resource not found: {name}");
        using var reader = new StreamReader(stream);
        var tpl = Template.Parse(reader.ReadToEnd());
        if (tpl.HasErrors)
            throw new InvalidOperationException(string.Join("; ", tpl.Messages));
        return tpl;
    }
}
