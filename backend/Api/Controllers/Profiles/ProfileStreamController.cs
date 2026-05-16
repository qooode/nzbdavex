using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Profiles;

[ApiController]
[Route("p/{token}/stream/{type}/{id}.json")]
public class ProfileStreamController(
    ConfigManager configManager,
    NzbResolutionCache cache,
    NewznabRateLimiter rateLimiter,
    TvdbIdResolver tvdbResolver
) : ControllerBase
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

        var ct = HttpContext.RequestAborted;
        var queryParams = await BuildQueryAsync(type, id, ct).ConfigureAwait(false);
        if (queryParams is null) return new JsonResult(new { streams = Array.Empty<object>() });

        var perIndexer = await Task.WhenAll(indexers.Select(async x =>
        {
            try
            {
                var ua = string.IsNullOrWhiteSpace(x.UserAgent) ? configManager.GetUserAgent() : x.UserAgent;
                await rateLimiter.WaitAsync(x.Name, x.MaxRequestsPerMinute, ct).ConfigureAwait(false);
                var client = new NewznabClient(x.Url, x.ApiKey, ua);
                var items = await client.QueryAsync(queryParams, ct).ConfigureAwait(false);
                return items.Select(i => new { indexer = x.Name, userAgent = ua, item = i });
            }
            catch
            {
                return [];
            }
        })).ConfigureAwait(false);

        var baseUrl = HttpContext.GetPublicBaseUrl(configManager.GetBaseUrl());
        var streams = perIndexer
            .SelectMany(x => x)
            .Where(x => !string.IsNullOrWhiteSpace(x.item.NzbUrl))
            .GroupBy(x => x.item.NzbUrl)
            .Select(g => g.First())
            .OrderByDescending(x => x.item.Size)
            .Select(x =>
            {
                var nzbToken = cache.Add(x.indexer, x.userAgent, x.item.NzbUrl, x.item.Title, type);
                return new
                {
                    name = x.indexer,
                    title = $"{x.item.Title}\n{FormatBytes(x.item.Size)}",
                    url = $"{baseUrl}/p/{token}/play/{nzbToken}.mkv",
                };
            })
            .ToList();

        return new JsonResult(new { streams });
    }

    private async Task<IReadOnlyDictionary<string, string>?> BuildQueryAsync(string type, string id, CancellationToken ct)
    {
        if (type == "movie")
        {
            var imdb = StripImdbPrefix(id);
            if (imdb is null) return null;
            return new Dictionary<string, string>
            {
                ["t"] = "movie",
                ["imdbid"] = imdb,
                ["cat"] = "2000",
                ["limit"] = "200",
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
            var dict = new Dictionary<string, string>
            {
                ["t"] = "tvsearch",
                ["season"] = season.ToString(),
                ["ep"] = episode.ToString(),
                ["cat"] = "5000",
                ["limit"] = "200",
            };
            var tvdb = await tvdbResolver.GetTvdbIdAsync(imdb, ct).ConfigureAwait(false);
            if (tvdb.HasValue) dict["tvdbid"] = tvdb.Value.ToString();
            else dict["imdbid"] = imdb;
            return dict;
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
