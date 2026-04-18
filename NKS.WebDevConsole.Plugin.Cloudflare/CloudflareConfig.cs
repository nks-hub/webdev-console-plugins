using System.Text.Json;
using System.Text.Json.Serialization;
using NKS.WebDevConsole.Core.Services;

namespace NKS.WebDevConsole.Plugin.Cloudflare;

/// <summary>
/// Persisted Cloudflare plugin settings under
/// <c>{WdcPaths.CloudflareRoot}/config.json</c>. All fields are user-owned and
/// can be edited via the settings endpoint exposed by the plugin
/// (POST /api/cloudflare/config). Secrets are never logged.
/// </summary>
public sealed class CloudflareConfig
{
    /// <summary>Absolute path to the cloudflared executable.</summary>
    [JsonPropertyName("cloudflaredPath")]
    public string? CloudflaredPath { get; set; }

    /// <summary>
    /// Tunnel token (JWT) issued by the Cloudflare dashboard when the tunnel
    /// was created — used by <c>cloudflared tunnel run --token …</c>.
    /// Distinct from the account-scoped API token.
    /// </summary>
    [JsonPropertyName("tunnelToken")]
    public string? TunnelToken { get; set; }

    /// <summary>Human-readable tunnel name (metadata only).</summary>
    [JsonPropertyName("tunnelName")]
    public string? TunnelName { get; set; }

    /// <summary>The tunnel's UUID (used when querying / deleting via API).</summary>
    [JsonPropertyName("tunnelId")]
    public string? TunnelId { get; set; }

    /// <summary>
    /// Cloudflare account API token. Needed for Zones/DNS/Tunnels management.
    /// Scopes required: Account &gt; Cloudflare Tunnel &gt; Edit,
    /// Account &gt; Account Settings &gt; Read, Zone &gt; Zone &gt; Read,
    /// Zone &gt; DNS &gt; Edit.
    /// </summary>
    [JsonPropertyName("apiToken")]
    public string? ApiToken { get; set; }

    /// <summary>Account ID (hex string) — used for tunnel-scoped API calls.</summary>
    [JsonPropertyName("accountId")]
    public string? AccountId { get; set; }

    /// <summary>Default zone ID to preselect in DNS panels.</summary>
    [JsonPropertyName("defaultZoneId")]
    public string? DefaultZoneId { get; set; }

    /// <summary>Seconds the StartAsync health check waits for cloudflared to connect.</summary>
    [JsonPropertyName("startupTimeoutSecs")]
    public int StartupTimeoutSecs { get; set; } = 20;

    /// <summary>
    /// Template used to pre-fill a site's public subdomain when the user
    /// enables the tunnel in SiteEdit. Placeholders:
    /// <c>{stem}</c> — local domain with <c>.loc</c>/<c>.local</c>/<c>.test</c>
    /// stripped (e.g. <c>myapp.loc</c> → <c>myapp</c>),
    /// <c>{hash}</c> — 6 deterministic hex chars from <c>md5(domain + InstallSalt)</c>,
    /// stable per-install so the same site always gets the same URL,
    /// <c>{user}</c> — lowercased OS username.
    /// Default <c>{stem}-{hash}</c> gives collision-free URLs like
    /// <c>myapp-bffa44</c> that don't leak predictable hostnames.
    /// The user can always edit the generated value in the Cloudflare tab.
    /// </summary>
    [JsonPropertyName("subdomainTemplate")]
    public string SubdomainTemplate { get; set; } = "{stem}-{hash}";

    /// <summary>
    /// Random per-install salt mixed into <c>{hash}</c> so that two
    /// developers exposing the same domain get different public
    /// subdomains. Auto-generated on first load when empty.
    /// </summary>
    [JsonPropertyName("installSalt")]
    public string InstallSalt { get; set; } = "";

    // ── Persistence ─────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string ConfigFilePath =>
        Path.Combine(WdcPaths.CloudflareRoot, "config.json");

    public static CloudflareConfig LoadOrDefault()
    {
        CloudflareConfig cfg;
        try
        {
            Directory.CreateDirectory(WdcPaths.CloudflareRoot);
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                cfg = JsonSerializer.Deserialize<CloudflareConfig>(json) ?? new CloudflareConfig();
            }
            else
            {
                cfg = new CloudflareConfig();
            }
        }
        catch
        {
            cfg = new CloudflareConfig();
        }

        // First-run salt generation — 16 hex chars is enough entropy to
        // keep the 6-char derived hash collision-resistant across a dev's
        // handful of exposed sites while staying short in config.json.
        if (string.IsNullOrEmpty(cfg.InstallSalt))
        {
            cfg.InstallSalt = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            try { cfg.Save(); } catch { /* best effort */ }
        }
        return cfg;
    }

    /// <summary>
    /// Returns 6 hex chars of <c>md5(domain + InstallSalt)</c>. Deterministic
    /// for a given (salt, domain) pair so the generated subdomain is stable
    /// across re-enables.
    /// </summary>
    public string DomainHash(string domain)
    {
        if (string.IsNullOrEmpty(InstallSalt)) return "";
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(domain + InstallSalt));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..6];
    }

    /// <summary>
    /// Resolves the subdomain template for a given site domain, substituting
    /// <c>{stem}</c>, <c>{hash}</c>, and <c>{user}</c> placeholders and
    /// collapsing stray separator dashes from empty substitutions.
    /// </summary>
    public string RenderSubdomain(string domain)
    {
        var stem = System.Text.RegularExpressions.Regex.Replace(
            domain, @"\.(loc|local|test)$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var user = Environment.UserName.ToLowerInvariant();
        var hash = DomainHash(domain);
        var result = (SubdomainTemplate ?? "{stem}-{hash}")
            .Replace("{stem}", stem)
            .Replace("{hash}", hash)
            .Replace("{user}", user);
        return System.Text.RegularExpressions.Regex.Replace(result, "-+", "-")
            .Trim('-');
    }

    public void Save()
    {
        Directory.CreateDirectory(WdcPaths.CloudflareRoot);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        // Atomic write: tmp → move, so a crash mid-write never corrupts settings.
        var tmp = ConfigFilePath + ".tmp";
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigFilePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Returns a copy with secret fields (api token, tunnel token) redacted
    /// to safely echo back to the UI / API responses.
    /// </summary>
    public CloudflareConfig Redacted() => new()
    {
        CloudflaredPath = CloudflaredPath,
        TunnelName = TunnelName,
        TunnelId = TunnelId,
        AccountId = AccountId,
        DefaultZoneId = DefaultZoneId,
        StartupTimeoutSecs = StartupTimeoutSecs,
        SubdomainTemplate = SubdomainTemplate,
        // Guard against tokens shorter than 4 chars (malformed / test input) —
        // [^4..] would throw on length 0–3. Show fully masked for such values so
        // the UI never echoes the raw (short) token back.
        ApiToken = string.IsNullOrEmpty(ApiToken)
            ? null
            : ApiToken.Length < 4 ? "••••••••" : "••••••••" + ApiToken[^4..],
        TunnelToken = string.IsNullOrEmpty(TunnelToken) ? null : "••••••••",
    };
}
