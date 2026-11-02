using System.Net;

namespace NKS.WebDevConsole.Plugin.Cloudflare;

/// <summary>
/// Retries a request that timed out, failed to connect, or got 429/5xx, up to
/// two more times with a short back-off. Each attempt has its own time limit.
/// Request bodies here are small in-memory JSON, so resending them is safe.
/// </summary>
internal sealed class TransientRetryHandler : DelegatingHandler
{
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    public TransientRetryHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(AttemptTimeout);
            try
            {
                var res = await base.SendAsync(request, attemptCts.Token);
                if (attempt < Backoff.Length && IsTransient(res.StatusCode))
                {
                    res.Dispose();
                    await Task.Delay(Backoff[attempt], ct);
                    continue;
                }
                return res;
            }
            catch (Exception ex) when (attempt < Backoff.Length && !ct.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(Backoff[attempt], ct);
            }
        }
    }

    internal static bool IsTransient(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500;
}
