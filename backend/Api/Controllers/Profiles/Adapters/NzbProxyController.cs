using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Api.Controllers.Profiles.Adapters;

[ApiController]
[Route("api/search/{token}/nzb/{playToken}.nzb")]
public class NzbProxyController(
    SearchProfileService searchService,
    NzbResolutionCache cache
) : ControllerBase
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    [HttpGet]
    public async Task<IActionResult> Get(string token, string playToken)
    {
        var profile = searchService.GetProfile(token);
        if (profile is null) return NotFound();
        if (!searchService.IsAdapterEnabled(token, "json")
            && !searchService.IsAdapterEnabled(token, "newznab"))
        {
            return NotFound();
        }

        var entry = cache.Get(playToken);
        if (entry is null) return NotFound("Link expired. Re-search to refresh.");
        if (entry.ProfileToken != token) return NotFound();

        var candidate = entry.Primary;
        if (string.IsNullOrWhiteSpace(candidate.NzbUrl)) return NotFound();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, candidate.NzbUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", candidate.IndexerUserAgent);
            var client = ProxyHttpClientPool.GetClient(candidate.ProxyUrl);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(FetchTimeout);
            using var resp = await client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return StatusCode((int)resp.StatusCode, "Source indexer failed to return the NZB.");
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            Response.Headers["Content-Disposition"] =
                $"attachment; filename=\"{SanitizeFileName(candidate.Title)}.nzb\"";
            return File(bytes, "application/x-nzb");
        }
        catch (OperationCanceledException)
        {
            return new EmptyResult();
        }
        catch (Exception e)
        {
            Log.Debug(e, "NZB proxy fetch failed for {Url}", candidate.NzbUrl);
            return StatusCode(502, "Failed to fetch NZB from source indexer.");
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "untitled" : clean;
    }
}
