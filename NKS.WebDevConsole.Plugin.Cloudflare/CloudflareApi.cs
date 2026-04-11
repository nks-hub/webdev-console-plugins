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

    // ── Accounts (used by auto-setup to skip manual account ID entry) ──

    /// <summary>List all accounts accessible with the configured token.</summary>
    public async Task<JsonElement> ListAccountsAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/accounts?per_page=20");
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }

    // ── Tunnel discovery / creation ─────────────────────────────────────

    /// <summary>
    /// Finds a tunnel by name OR creates one if missing. Mirrors FlyEnv's
    /// <c>fetchTunnel</c>: deterministic name based on md5(apiToken)[..12]
    /// ensures the same token always maps to the same tunnel.
    /// </summary>
    public async Task<JsonElement> FindOrCreateTunnelAsync(
        string tunnelName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.AccountId))
            throw new InvalidOperationException("Cloudflare account ID is not configured.");

        // 1. Search for existing tunnel by exact name
        using (var searchReq = BuildRequest(HttpMethod.Get,
            $"/accounts/{_config.AccountId}/cfd_tunnel?name={Uri.EscapeDataString(tunnelName)}&is_deleted=false"))
        {
            using var searchRes = await _http.SendAsync(searchReq, ct);
            var searchJson = await ReadJsonAsync(searchRes, ct);
            if (searchJson.TryGetProperty("result", out var arr) &&
                arr.ValueKind == JsonValueKind.Array &&
                arr.GetArrayLength() > 0)
            {
                return arr[0].Clone();
            }
        }

        // 2. Create — server-side random secret, remote config source
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using var createReq = BuildRequest(HttpMethod.Post,
            $"/accounts/{_config.AccountId}/cfd_tunnel");
        createReq.Content = JsonContent.Create(new
        {
            name = tunnelName,
            tunnel_secret = secret,
            config_src = "cloudflare",
        });
        using var createRes = await _http.SendAsync(createReq, ct);
        var createJson = await ReadJsonAsync(createRes, ct);
        if (createJson.TryGetProperty("result", out var result))
            return result.Clone();
        throw new InvalidOperationException("Cloudflare did not return a tunnel on create");
    }

    /// <summary>
    /// Fetches the tunnel JWT token used by <c>cloudflared run --token</c>.
    /// Must be called after FindOrCreateTunnelAsync — the list / create
    /// endpoints do not return the token inline.
    /// </summary>
    public async Task<string?> GetTunnelTokenAsync(string tunnelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.AccountId))
            throw new InvalidOperationException("Cloudflare account ID is not configured.");
        using var req = BuildRequest(HttpMethod.Get,
            $"/accounts/{_config.AccountId}/cfd_tunnel/{tunnelId}/token");
        using var res = await _http.SendAsync(req, ct);
        var json = await ReadJsonAsync(res, ct);
        return json.TryGetProperty("result", out var result) ? result.GetString() : null;
    }

    // ── Single-record DNS helpers (used by per-site sync) ───────────────

    /// <summary>
    /// Upsert a CNAME record that points <paramref name="fullName"/> at the
    /// tunnel hostname, replacing any existing record with the same name.
    /// Matches FlyEnv's initDNSRecords behaviour.
    /// </summary>
    public async Task UpsertCnameToTunnelAsync(
        string zoneId,
        string fullName,
        string tunnelId,
        CancellationToken ct = default)
    {
        var target = $"{tunnelId}.cfargotunnel.com";

        // 1. Look up existing CNAME
        using (var searchReq = BuildRequest(HttpMethod.Get,
            $"/zones/{zoneId}/dns_records?type=CNAME&name={Uri.EscapeDataString(fullName)}"))
        {
            using var searchRes = await _http.SendAsync(searchReq, ct);
            var json = await ReadJsonAsync(searchRes, ct);
            if (json.TryGetProperty("result", out var arr) &&
                arr.ValueKind == JsonValueKind.Array &&
                arr.GetArrayLength() > 0)
            {
                var existing = arr[0];
                var existingId = existing.GetProperty("id").GetString();
                var existingContent = existing.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (existingContent == target)
                    return; // already pointing correctly — no-op

                using var updateReq = BuildRequest(HttpMethod.Put,
                    $"/zones/{zoneId}/dns_records/{existingId}");
                updateReq.Content = JsonContent.Create(new
                {
                    type = "CNAME",
                    name = fullName,
                    content = target,
                    proxied = true,
                });
                using var updateRes = await _http.SendAsync(updateReq, ct);
                await ReadJsonAsync(updateRes, ct);
                return;
            }
        }

        // 2. No existing record — create new proxied CNAME
        using var createReq = BuildRequest(HttpMethod.Post, $"/zones/{zoneId}/dns_records");
        createReq.Content = JsonContent.Create(new
        {
            type = "CNAME",
            name = fullName,
            content = target,
            proxied = true,
            ttl = 1,
        });
        using var createRes = await _http.SendAsync(createReq, ct);
        await ReadJsonAsync(createRes, ct);
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
    /// Bind a hostname to a local service in the tunnel's ingress config.
    /// Caller passes <c>{ hostname, service, httpHostHeader? }</c> tuples.
    /// <c>httpHostHeader</c> overrides the Host header cloudflared sends
    /// to the local origin — critical so Apache matches the LOCAL vhost
    /// (e.g. <c>blog.loc</c>) instead of the public name the visitor used
    /// (<c>blog.nks-dev.cz</c>). The mandatory catch-all 404 rule is
    /// appended automatically.
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

        var ingress = new List<object>();
        foreach (var r in rules)
        {
            if (!string.IsNullOrEmpty(r.HttpHostHeader))
            {
                ingress.Add(new
                {
                    hostname = r.Hostname,
                    service = r.Service,
                    originRequest = new { httpHostHeader = r.HttpHostHeader },
                });
            }
            else
            {
                ingress.Add(new { hostname = r.Hostname, service = r.Service });
            }
        }
        ingress.Add(new { service = "http_status:404" });

        req.Content = JsonContent.Create(new { config = new { ingress } });
        using var res = await _http.SendAsync(req, ct);
        return await ReadJsonAsync(res, ct);
    }
}

public sealed record TunnelIngressRule(string Hostname, string Service, string? HttpHostHeader = null);
