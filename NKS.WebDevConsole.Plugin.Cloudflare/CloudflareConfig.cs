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

    // ── Persistence ─────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string ConfigFilePath =>
        Path.Combine(WdcPaths.CloudflareRoot, "config.json");

    public static CloudflareConfig LoadOrDefault()
    {
        try
        {
            Directory.CreateDirectory(WdcPaths.CloudflareRoot);
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var cfg = JsonSerializer.Deserialize<CloudflareConfig>(json);
                if (cfg is not null) return cfg;
            }
        }
        catch { /* fall through to default */ }
        return new CloudflareConfig();
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
        ApiToken = string.IsNullOrEmpty(ApiToken) ? null : "••••••••" + ApiToken[^4..],
        TunnelToken = string.IsNullOrEmpty(TunnelToken) ? null : "••••••••",
    };
}
