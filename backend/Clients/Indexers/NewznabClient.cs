using System.Collections.Concurrent;
using System.Net;
using System.Xml.Linq;

namespace NzbWebDAV.Clients.Indexers;

public class NewznabClient(string baseUrl, string apiKey, string userAgent = "NzbDav", string? proxyUrl = null)
{
    // One HttpClient per unique proxy URL (or "" for no proxy). Building a fresh client
    // per request would leak sockets; sharing a single static one prevents proxy changes
    // from taking effect. The cache stays small (one entry per distinct proxy).
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();
    private static readonly XNamespace Newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    private readonly string _baseUrl = baseUrl.TrimEnd('/');
    private readonly HttpClient _http = GetClient(proxyUrl);

    private static HttpClient GetClient(string? proxyUrl)
    {
        var key = NormalizeProxy(proxyUrl) ?? "";
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
            return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        });
    }

    private static string? NormalizeProxy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.ToString();
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(userAgent);
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<bool> TestAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api?t=caps&apikey={Uri.EscapeDataString(apiKey)}";
        using var resp = await GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Contains("<caps", StringComparison.OrdinalIgnoreCase);
    }

    public Task<List<NewznabItem>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        return QueryAsync(new Dictionary<string, string>
        {
            ["t"] = "search",
            ["q"] = query,
            ["limit"] = limit.ToString(),
        }, ct);
    }

    public async Task<List<NewznabItem>> QueryAsync(IReadOnlyDictionary<string, string> queryParams, CancellationToken ct = default)
    {
        var parts = new List<string>
        {
            $"apikey={Uri.EscapeDataString(apiKey)}",
            "extended=1",
        };
        foreach (var (k, v) in queryParams)
            parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
        var url = $"{_baseUrl}/api?{string.Join("&", parts)}";
        using var resp = await GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        if (doc.Root?.Name.LocalName == "error")
        {
            var code = doc.Root.Attribute("code")?.Value;
            var desc = doc.Root.Attribute("description")?.Value ?? "Indexer returned an error.";
            throw new Exception(code is null ? desc : $"[{code}] {desc}");
        }
        var items = doc.Root?.Element("channel")?.Elements("item") ?? [];
        return items.Select(ParseItem).ToList();
    }

    private static NewznabItem ParseItem(XElement item)
    {
        var attrs = item.Elements(Newznab + "attr")
            .Where(x => x.Attribute("name")?.Value is not null)
            .GroupBy(x => x.Attribute("name")!.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Attribute("value")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "",
                StringComparer.OrdinalIgnoreCase);

        var enclosure = item.Element("enclosure");
        var sizeStr = enclosure?.Attribute("length")?.Value ?? GetAttr(attrs, "size");
        long.TryParse(sizeStr, out var size);

        var nzbUrl = enclosure?.Attribute("url")?.Value
                     ?? item.Element("link")?.Value
                     ?? "";

        DateTimeOffset? posted = null;
        if (DateTimeOffset.TryParse(item.Element("pubDate")?.Value, out var p)) posted = p;

        DateTimeOffset? usenetDate = null;
        var udRaw = GetAttr(attrs, "usenetdate");
        if (!string.IsNullOrEmpty(udRaw) && DateTimeOffset.TryParse(udRaw, out var ud))
            usenetDate = ud;

        return new NewznabItem
        {
            Title = item.Element("title")?.Value ?? "",
            Guid = item.Element("guid")?.Value ?? "",
            NzbUrl = nzbUrl,
            Size = size,
            Posted = posted,
            UsenetDate = usenetDate,
            Grabs = ParseNonNegInt(GetAttr(attrs, "grabs")),
            Comments = ParseNonNegInt(GetAttr(attrs, "comments")),
            Password = ParseNonNegInt(GetAttr(attrs, "password")),
            Files = ParseNonNegInt(GetAttr(attrs, "files")),
            Group = GetAttr(attrs, "group"),
            Poster = GetAttr(attrs, "poster"),
        };
    }

    private static string? GetAttr(Dictionary<string, string> attrs, string name) =>
        attrs.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    private static int? ParseNonNegInt(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!int.TryParse(raw, out var n)) return null;
        return n < 0 ? 0 : n;
    }

    public class NewznabItem
    {
        public required string Title { get; init; }
        public required string Guid { get; init; }
        public required string NzbUrl { get; init; }
        public long Size { get; init; }
        public DateTimeOffset? Posted { get; init; }
        public DateTimeOffset? UsenetDate { get; init; }
        public int? Grabs { get; init; }
        public int? Comments { get; init; }
        public int? Password { get; init; }
        public int? Files { get; init; }
        public string? Group { get; init; }
        public string? Poster { get; init; }
    }
}
