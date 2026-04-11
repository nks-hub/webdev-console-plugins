using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Plugin.Cloudflare;

/// <summary>
/// Thin wrapper around a subset of the Cloudflare REST API needed for managing
/// tunnels, zones, and DNS records. Every method returns raw
/// <see cref="JsonElement"/> blobs so the plugin can forward the API response
/// to the frontend without a hand-written DTO for every Cloudflare field
/// — the UI just consumes <c>result[].id</c> / <c>result[].name</c> etc.
///
/// All calls fail fast with <see cref="InvalidOperationException"/> if
/// <c>apiToken</c> is missing so the UI can render a "setup required" prompt
/// instead of silently 401-ing.
/// </summary>
public sealed class CloudflareApi
{
    private const string BaseUrl = "https://api.cloudflare.com/client/v4";

    private readonly CloudflareConfig _config;
    private readonly ILogger<CloudflareApi> _logger;
    private readonly HttpClient _http;

    public CloudflareApi(CloudflareConfig config, ILogger<CloudflareApi> logger)
    {
        _config = config;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NKS-WebDevConsole/1.0 (+cloudflare-plugin)");
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiToken))
            throw new InvalidOperationException("Cloudflare API token is not configured.");
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiToken);
        return req;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Cloudflare API {res.StatusCode}: {text}");
        }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ── Verify token ────────────────────────────────────────────────────

    /// <summary>
    /// GET /user/tokens/verify — used by the settings UI to test whether
    /// the entered token is valid before persisting it.
    /// </summary>
    public async Task<JsonElement> VerifyTokenAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/user/tokens/verify");
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    // ── Zones ───────────────────────────────────────────────────────────

    /// <summary>List all zones accessible with the configured token.</summary>
    public async Task<JsonElement> ListZonesAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/zones?per_page=50");
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    /// <summary>Get details for a specific zone by ID.</summary>
    public async Task<JsonElement> GetZoneAsync(string zoneId, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, $"/zones/{zoneId}");
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    // ── DNS records ─────────────────────────────────────────────────────

    /// <summary>List DNS records for a zone (all types).</summary>
    public async Task<JsonElement> ListDnsRecordsAsync(string zoneId, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, $"/zones/{zoneId}/dns_records?per_page=100");
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    /// <summary>
    /// Create a new DNS record. <paramref name="type"/> e.g. "CNAME", "A",
    /// <paramref name="content"/> is the target (hostname or IP). Proxied
    /// defaults to true for CNAME records pointing at a tunnel so the
    /// tunnel routing actually works.
    /// </summary>
    public async Task<JsonElement> CreateDnsRecordAsync(
        string zoneId,
        string type,
        string name,
        string content,
        bool proxied = true,
        int ttl = 1,
        CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Post, $"/zones/{zoneId}/dns_records");
        req.Content = JsonContent.Create(new
        {
            type,
            name,
            content,
            ttl,
            proxied,
        });
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    /// <summary>Delete a DNS record by ID.</summary>
    public async Task<JsonElement> DeleteDnsRecordAsync(string zoneId, string recordId, CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Delete, $"/zones/{zoneId}/dns_records/{recordId}");
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    // ── Tunnels ─────────────────────────────────────────────────────────

    /// <summary>List all tunnels for the configured account.</summary>
    public async Task<JsonElement> ListTunnelsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.AccountId))
            throw new InvalidOperationException("Cloudflare account ID is not configured.");
        using var req = BuildRequest(HttpMethod.Get,
            $"/accounts/{_config.AccountId}/cfd_tunnel?is_deleted=false");
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    /// <summary>Get configuration (ingress rules) for a specific tunnel.</summary>
    public async Task<JsonElement> GetTunnelConfigurationAsync(string tunnelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.AccountId))
            throw new InvalidOperationException("Cloudflare account ID is not configured.");
        using var req = BuildRequest(HttpMethod.Get,
            $"/accounts/{_config.AccountId}/cfd_tunnel/{tunnelId}/configurations");
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    /// <summary>
    /// Bind a hostname to a local service in the tunnel's ingress config. The
    /// caller passes a list of <c>{ hostname, service }</c> pairs (service can be
    /// e.g. <c>http://localhost:80</c>). Existing rules are replaced.
    /// </summary>
    public async Task<JsonElement> UpdateTunnelIngressAsync(
        string tunnelId,
        IEnumerable<TunnelIngressRule> rules,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.AccountId))
            throw new InvalidOperationException("Cloudflare account ID is not configured.");
        using var req = BuildRequest(HttpMethod.Put,
            $"/accounts/{_config.AccountId}/cfd_tunnel/{tunnelId}/configurations");
        req.Content = JsonContent.Create(new
        {
            config = new
            {
                ingress = rules
                    .Select(r => new { hostname = r.Hostname, service = r.Service })
                    .Append(new { hostname = (string?)null, service = "http_status:404" })
                    .ToArray(),
            },
        });
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }
}

public sealed record TunnelIngressRule(string Hostname, string Service);
