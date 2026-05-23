using System.Collections.Concurrent;
using System.Net;

namespace NzbWebDAV.Utils;

/// <summary>
/// One shared HttpClient per distinct proxy URL (or "" for direct). Returned clients
/// have an infinite Timeout because they're shared across callers with different
/// budgets; each caller enforces its own per-request timeout via CancellationToken.
/// </summary>
public static class ProxyHttpClientPool
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    public static HttpClient GetClient(string? proxyUrl)
    {
        var key = Normalize(proxyUrl) ?? "";
        return Clients.GetOrAdd(key, k =>
        {
            var handler = new HttpClientHandler();
            if (k.Length > 0 && Uri.TryCreate(k, UriKind.Absolute, out var uri))
            {
                handler.Proxy = new WebProxy(uri) { BypassProxyOnLocal = false };
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }
            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        });
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.ToString();
    }
}
