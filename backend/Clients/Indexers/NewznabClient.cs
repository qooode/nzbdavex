using System.Xml.Linq;

namespace NzbWebDAV.Clients.Indexers;

public class NewznabClient(string baseUrl, string apiKey)
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly XNamespace Newznab = "http://www.newznab.com/DTD/2010/feeds/attributes/";

    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<bool> TestAsync(CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api?t=caps&apikey={Uri.EscapeDataString(apiKey)}";
        using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Contains("<caps", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<NewznabItem>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api?t=search&q={Uri.EscapeDataString(query)}&apikey={Uri.EscapeDataString(apiKey)}&extended=1&limit={limit}";
        using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);
        var items = doc.Root?.Element("channel")?.Elements("item") ?? [];
        return items.Select(ParseItem).ToList();
    }

    private static NewznabItem ParseItem(XElement item)
    {
        var enclosure = item.Element("enclosure");
        var sizeStr = enclosure?.Attribute("length")?.Value
                      ?? item.Elements(Newznab + "attr")
                             .FirstOrDefault(x => x.Attribute("name")?.Value == "size")
                             ?.Attribute("value")?.Value;
        long.TryParse(sizeStr, out var size);

        var nzbUrl = enclosure?.Attribute("url")?.Value
                     ?? item.Element("link")?.Value
                     ?? "";

        DateTimeOffset? posted = null;
        if (DateTimeOffset.TryParse(item.Element("pubDate")?.Value, out var p)) posted = p;

        return new NewznabItem
        {
            Title = item.Element("title")?.Value ?? "",
            Guid = item.Element("guid")?.Value ?? "",
            NzbUrl = nzbUrl,
            Size = size,
            Posted = posted,
        };
    }

    public class NewznabItem
    {
        public required string Title { get; init; }
        public required string Guid { get; init; }
        public required string NzbUrl { get; init; }
        public long Size { get; init; }
        public DateTimeOffset? Posted { get; init; }
    }
}
