using System.Reflection;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;

namespace NKS.WebDevConsole.Plugin.Apache;

public record VhostModel(
    string Domain,
    string Root,
    int Port,
    bool Ssl,
    string? CertPath,
    string? KeyPath,
    bool PhpEnabled,
    string? PhpVersion,
    int PhpFcgiPort,
    string[]? Aliases,
    IReadOnlyList<RedirectRule>? Redirects
);

public record RedirectRule(string From, string To, string Code = "301");

public record HttpdConfModel(
    string ServerRoot,
    string LogDir,
    string VhostsDir,
    string ServerAdmin,
    string ServerName,
    int HttpPort,
    int HttpsPort,
    bool SslEnabled,
    bool PhpEnabled,
    string GeneratedAt
);

/// <summary>
/// Renders Apache configuration files from embedded Scriban templates.
/// </summary>
public sealed class ApacheConfigGenerator
{
    private readonly ILogger<ApacheConfigGenerator> _logger;
    private readonly Template _vhostTemplate;
    private readonly Template _httpdTemplate;

    public ApacheConfigGenerator(ILogger<ApacheConfigGenerator> logger)
    {
        _logger = logger;
        _vhostTemplate = LoadEmbeddedTemplate("Templates.vhost.conf.scriban");
        _httpdTemplate = LoadEmbeddedTemplate("Templates.httpd.conf.scriban");
    }

    public string RenderVhost(VhostModel model)
    {
        var scriptObj = new ScriptObject();
        scriptObj.Import(new
        {
            site = new
            {
                domain = model.Domain,
                root = model.Root,
                ssl = model.Ssl,
                cert_path = model.CertPath,
                key_path = model.KeyPath,
                php_enabled = model.PhpEnabled,
                php_version = model.PhpVersion,
                php_fcgi_port = model.PhpFcgiPort,
                aliases = model.Aliases ?? [],
                redirects = model.Redirects?.Select(r => new { from = r.From, to = r.To, code = r.Code }).ToArray() ?? []
            },
            port = model.Port,
            is_windows = OperatingSystem.IsWindows()
        });

        var ctx = new TemplateContext();
        ctx.PushGlobal(scriptObj);

        var rendered = _vhostTemplate.Render(ctx);
        _logger.LogDebug("Rendered vhost config for {Domain} ({Length} chars)", model.Domain, rendered.Length);
        return rendered;
    }

    public string RenderHttpdConf(HttpdConfModel model)
    {
        var scriptObj = new ScriptObject();
        scriptObj.Import(new
        {
            server_root = model.ServerRoot,
            log_dir = model.LogDir,
            vhosts_dir = model.VhostsDir,
            server_admin = model.ServerAdmin,
            server_name = model.ServerName,
            http_port = model.HttpPort,
            https_port = model.HttpsPort,
            ssl_enabled = model.SslEnabled,
            php_enabled = model.PhpEnabled,
            generated_at = model.GeneratedAt
        });

        var ctx = new TemplateContext();
        ctx.PushGlobal(scriptObj);
        return _httpdTemplate.Render(ctx);
    }

    public async Task WriteVhostAsync(
        VhostModel model,
        string vhostsDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(vhostsDirectory);
        var rendered = RenderVhost(model);
        var path = Path.Combine(vhostsDirectory, $"{model.Domain}.conf");

        // Atomic write: write to temp, rename
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, rendered, ct);
        File.Move(temp, path, overwrite: true);

        _logger.LogInformation("Wrote vhost config: {Path}", path);
    }

    public async Task WriteHttpdConfAsync(
        HttpdConfModel model,
        string outputPath,
        CancellationToken ct = default)
    {
        var rendered = RenderHttpdConf(model);
        var temp = outputPath + ".tmp";
        await File.WriteAllTextAsync(temp, rendered, ct);
        File.Move(temp, outputPath, overwrite: true);
        _logger.LogInformation("Wrote httpd.conf: {Path}", outputPath);
    }

    private static Template LoadEmbeddedTemplate(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullName = $"{assembly.GetName().Name}.{resourceName}";

        using var stream = assembly.GetManifestResourceStream(fullName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {fullName}");

        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        var tpl = Template.Parse(text);
        if (tpl.HasErrors)
            throw new InvalidOperationException(
                $"Template parse error in {resourceName}: {string.Join("; ", tpl.Messages)}");

        return tpl;
    }
}
