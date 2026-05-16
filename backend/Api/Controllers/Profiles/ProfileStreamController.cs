using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.Profiles;

[ApiController]
[Route("p/{token}/stream/{type}/{id}.json")]
public class ProfileStreamController(ConfigManager configManager) : ControllerBase
{
    [HttpOptions]
    public IActionResult Preflight()
    {
        ProfileManifestController.SetCors(Response);
        return NoContent();
    }

    [HttpGet]
    public async Task<IActionResult> Get(string token, string type, string id)
    {
        ProfileManifestController.SetCors(Response);

        var profile = configManager.GetProfileConfig().Profiles.FirstOrDefault(x => x.Token == token);
        if (profile is null) return NotFound();

        var allIndexers = configManager.GetIndexerConfig().Indexers.Where(x => x.Enabled).ToList();
        var indexers = profile.IndexerNames.Count == 0
            ? allIndexers
            : allIndexers.Where(x => profile.IndexerNames.Contains(x.Name)).ToList();

        if (indexers.Count == 0) return new JsonResult(new { streams = Array.Empty<object>() });

        var queryParams = BuildQuery(type, id);
        if (queryParams is null) return new JsonResult(new { streams = Array.Empty<object>() });

        var ct = HttpContext.RequestAborted;
        var perIndexer = await Task.WhenAll(indexers.Select(async x =>
        {
            try
            {
                var ua = string.IsNullOrWhiteSpace(x.UserAgent) ? configManager.GetUserAgent() : x.UserAgent;
                var client = new NewznabClient(x.Url, x.ApiKey, ua);
                var items = await client.QueryAsync(queryParams, ct).ConfigureAwait(false);
                return items.Select(i => new { indexer = x.Name, item = i });
            }
            catch
            {
                return [];
            }
        })).ConfigureAwait(false);

        var streams = perIndexer
            .SelectMany(x => x)
            .Where(x => !string.IsNullOrWhiteSpace(x.item.NzbUrl))
            .GroupBy(x => x.item.NzbUrl)
            .Select(g => g.First())
            .OrderByDescending(x => x.item.Posted ?? DateTimeOffset.MinValue)
            .Take(50)
            .Select(x => new
            {
                name = x.indexer,
                title = $"{x.item.Title}\n{FormatBytes(x.item.Size)}",
                externalUrl = x.item.NzbUrl,
                behaviorHints = new { notWebReady = true },
            })
            .ToList();

        return new JsonResult(new { streams });
    }

    private static IReadOnlyDictionary<string, string>? BuildQuery(string type, string id)
    {
        if (type == "movie")
        {
            var imdb = StripImdbPrefix(id);
            if (imdb is null) return null;
            return new Dictionary<string, string>
            {
                ["t"] = "movie",
                ["imdbid"] = imdb,
                ["limit"] = "50",
            };
        }
        if (type == "series")
        {
            var parts = id.Split(':');
            if (parts.Length < 3) return null;
            var imdb = StripImdbPrefix(parts[0]);
            if (imdb is null) return null;
            if (!int.TryParse(parts[1], out var season)) return null;
            if (!int.TryParse(parts[2], out var episode)) return null;
            return new Dictionary<string, string>
            {
                ["t"] = "tvsearch",
                ["imdbid"] = imdb,
                ["season"] = season.ToString(),
                ["ep"] = episode.ToString(),
                ["limit"] = "50",
            };
        }
        return null;
    }

    private static string? StripImdbPrefix(string id)
    {
        if (!id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = id[2..];
        return digits.All(char.IsDigit) ? digits : null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "?";
        string[] s = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        double v = bytes;
        while (v >= 1024 && i < s.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {s[i]}";
    }
}
