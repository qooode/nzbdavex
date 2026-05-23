using System.Xml.Linq;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.Indexers;

public class NewznabClient(
    string baseUrl,
    string apiKey,
    string userAgent = "NzbDav",
    string? proxyUrl = null,
    int timeoutSeconds = 30)
{
    private static readonly XNamespace Newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    private readonly string _baseUrl = baseUrl.TrimEnd('/');
    private readonly HttpClient _http = ProxyHttpClientPool.GetClient(proxyUrl);
    private readonly int _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;

    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await work(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Indexer request timed out after {_timeoutSeconds}s.");
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(userAgent);
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    public Task<bool> TestAsync(CancellationToken ct = default)
    {
        return WithTimeoutAsync(async token =>
        {
            var url = $"{_baseUrl}/api?t=caps&apikey={Uri.EscapeDataString(apiKey)}";
            using var resp = await GetAsync(url, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return body.Contains("<caps", StringComparison.OrdinalIgnoreCase);
        }, ct);
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

    public Task<List<NewznabItem>> QueryAsync(IReadOnlyDictionary<string, string> queryParams, CancellationToken ct = default)
    {
        return WithTimeoutAsync(async token =>
        {
            var parts = new List<string>
            {
                $"apikey={Uri.EscapeDataString(apiKey)}",
                "extended=1",
            };
            foreach (var (k, v) in queryParams)
                parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
            var url = $"{_baseUrl}/api?{string.Join("&", parts)}";
            using var resp = await GetAsync(url, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, token).ConfigureAwait(false);
            if (doc.Root?.Name.LocalName == "error")
            {
                var code = doc.Root.Attribute("code")?.Value;
                var desc = doc.Root.Attribute("description")?.Value ?? "Indexer returned an error.";
                throw new Exception(code is null ? desc : $"[{code}] {desc}");
            }
            var items = doc.Root?.Element("channel")?.Elements("item") ?? [];
            return items.Select(ParseItem).ToList();
        }, ct);
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
