using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Profiles;

[ApiController]
[Route("adapters/addon/{token}/stream/{type}/{id}.json")]
public class ProfileReadController(
    SearchProfileService searchService,
    ConfigManager configManager
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

        var profile = searchService.GetProfile(token);
        if (profile is null) return NotFound();
        if (!searchService.IsAdapterEnabled(token, "addon")) return NotFound();

        var ct = HttpContext.RequestAborted;
        var result = await searchService.SearchByImdbAsync(token, type, id, ct).ConfigureAwait(false);
        if (result is null) return NotFound();
        if (result.Candidates.Count == 0) return new JsonResult(new { streams = Array.Empty<object>() });

        var baseUrl = HttpContext.GetPublicBaseUrl(configManager.GetBaseUrl());
        var items = result.Candidates
            .Select((c, i) =>
            {
                var description = BuildDescription(c);
                var displayIndexer = DisplayIndexer(c);
                return new
                {
                    name = $"[NZB] {displayIndexer}",
                    description,
                    title = description,
                    url = $"{baseUrl}/adapters/addon/{token}/play/{result.PlayTokens[i]}.mkv",
                    behaviorHints = new
                    {
                        filename = c.Title,
                        videoSize = c.Size,
                        bingeGroup = $"nzbdavex|{c.IndexerName}|{type}",
                        notWebReady = true,
                    },
                };
            })
            .ToList();

        return new JsonResult(new { streams = items });
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

    private static string BuildDescription(NzbResolutionCache.Candidate c)
    {
        var meta = new List<string> { $"💾 {FormatBytes(c.Size)}" };
        if (c.Posted is { } p) meta.Add($"📅 {FormatAge(DateTimeOffset.UtcNow - p)}");
        meta.Add($"🌐 {DisplayIndexer(c)}");
        return $"{c.Title}\n{string.Join(" | ", meta)}";
    }

    private static string DisplayIndexer(NzbResolutionCache.Candidate c)
    {
        if (string.IsNullOrWhiteSpace(c.SourceIndexerName)) return c.IndexerName;
        return $"{c.SourceIndexerName} (via {c.IndexerName})";
    }

    private static string FormatAge(TimeSpan a)
    {
        if (a.TotalDays >= 365) return $"{(int)(a.TotalDays / 365)}y";
        if (a.TotalDays >= 1) return $"{(int)a.TotalDays}d";
        if (a.TotalHours >= 1) return $"{(int)a.TotalHours}h";
        return $"{Math.Max(1, (int)a.TotalMinutes)}m";
    }
}
