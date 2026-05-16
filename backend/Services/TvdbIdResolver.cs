using System.Text.Json;

namespace NzbWebDAV.Services;

public class TvdbIdResolver
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<int?> GetTvdbIdAsync(string imdbDigits, CancellationToken ct)
    {
        return await TryTvmazeAsync(imdbDigits, ct).ConfigureAwait(false)
               ?? await TryWikidataAsync(imdbDigits, ct).ConfigureAwait(false);
    }

    private async Task<int?> TryTvmazeAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            var url = $"https://api.tvmaze.com/lookup/shows?imdb=tt{imdbDigits}";
            using var resp = await HttpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("externals", out var externals)) return null;
            if (!externals.TryGetProperty("thetvdb", out var tvdbElement)) return null;
            if (tvdbElement.ValueKind == JsonValueKind.Number && tvdbElement.TryGetInt32(out var tvdbId))
                return tvdbId;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<int?> TryWikidataAsync(string imdbDigits, CancellationToken ct)
    {
        try
        {
            var query = $"SELECT ?tvdb WHERE {{ ?item wdt:P345 \"tt{imdbDigits}\" . ?item wdt:P4835 ?tvdb . }} LIMIT 1";
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
            var tvdbStr = bindings[0].GetProperty("tvdb").GetProperty("value").GetString();
            return int.TryParse(tvdbStr, out var tvdbId) ? tvdbId : null;
        }
        catch
        {
            return null;
        }
    }
}
