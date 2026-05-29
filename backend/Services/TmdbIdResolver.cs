using System.Text.Json;

namespace NzbWebDAV.Services;

public class TmdbIdResolver
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<string?> GetImdbIdAsync(string type, string tmdbId, CancellationToken ct)
    {
        var property = type switch
        {
            "movie" => "P4947",
            "series" => "P4983",
            _ => null,
        };
        if (property is null) return null;
        if (!ulong.TryParse(tmdbId, out _)) return null;

        try
        {
            var query = $"SELECT ?imdb WHERE {{ ?item wdt:{property} \"{tmdbId}\" . ?item wdt:P345 ?imdb . }} LIMIT 1";
            var url = $"https://query.wikidata.org/sparql?query={Uri.EscapeDataString(query)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/sparql-results+json");
            req.Headers.UserAgent.ParseAdd("NzbDav (https://github.com/nzbdav-dev/nzbdav)");
            using var resp = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var bindings = doc.RootElement.GetProperty("results").GetProperty("bindings");
            if (bindings.GetArrayLength() == 0) return null;
            var imdb = bindings[0].GetProperty("imdb").GetProperty("value").GetString();
            if (string.IsNullOrWhiteSpace(imdb)) return null;
            return imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdb : $"tt{imdb}";
        }
        catch
        {
            return null;
        }
    }
}
