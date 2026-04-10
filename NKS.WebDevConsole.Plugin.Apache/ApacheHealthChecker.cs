using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NKS.WebDevConsole.Plugin.Apache;

/// <summary>
/// TCP port probe and HTTP HEAD health check for Apache.
/// </summary>
public sealed class ApacheHealthChecker
{
    private readonly ILogger<ApacheHealthChecker> _logger;
    private readonly HttpClient _http;

    public ApacheHealthChecker(ILogger<ApacheHealthChecker> logger)
    {
        _logger = logger;
        // Accept self-signed certs on localhost (dev env)
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert?.Issuer?.Contains("NKS", StringComparison.OrdinalIgnoreCase) == true
                || cert?.Issuer?.Contains("mkcert", StringComparison.OrdinalIgnoreCase) == true
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
    }

    /// <summary>
    /// Probes TCP port — fast check used during startup polling.
    /// </summary>
    public async Task<bool> IsTcpPortOpenAsync(int port, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Full HTTP health check — used by the 5-second monitor loop.
    /// Returns true if Apache responds with any 2xx or 3xx (redirect is still alive).
    /// </summary>
    public async Task<bool> IsHttpHealthyAsync(int port, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Head, $"http://127.0.0.1:{port}/");
            var resp = await _http.SendAsync(req, cts.Token);
            var healthy = (int)resp.StatusCode < 500;
            _logger.LogDebug("Apache HTTP probe port {Port} → {Status}", port, resp.StatusCode);
            return healthy;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Apache HTTP probe port {Port} failed: {Message}", port, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Waits for Apache to become ready after start, polling until timeout.
    /// Uses exponential backoff starting at 200ms.
    /// </summary>
    public async Task<bool> WaitForReadyAsync(
        int port,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var delay = TimeSpan.FromMilliseconds(200);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (await IsTcpPortOpenAsync(port, ct))
                return true;

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 2000));
        }

        return false;
    }
}
