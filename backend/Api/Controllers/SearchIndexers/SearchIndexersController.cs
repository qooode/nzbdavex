using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.SearchIndexers;

[ApiController]
[Route("api/search-indexers")]
public class SearchIndexersController(ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new SearchIndexersRequest(HttpContext);
        var indexers = configManager.GetIndexerConfig().Indexers.Where(x => x.Enabled).ToList();
        var ct = HttpContext.RequestAborted;

        var perIndexer = await Task.WhenAll(indexers.Select(async x =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var client = new NewznabClient(x.Url, x.ApiKey);
                var items = await client.SearchAsync(request.Query, request.Limit, ct).ConfigureAwait(false);
                var mapped = items.Select(i => new SearchIndexersResponse.Result
                {
                    Indexer = x.Name,
                    Title = i.Title,
                    NzbUrl = i.NzbUrl,
                    Size = i.Size,
                    Posted = i.Posted,
                }).ToList();
                return (Status: new SearchIndexersResponse.IndexerStatus
                {
                    Name = x.Name,
                    Ok = true,
                    ResultCount = mapped.Count,
                    ElapsedMs = sw.ElapsedMilliseconds,
                }, Results: mapped);
            }
            catch (Exception e)
            {
                return (Status: new SearchIndexersResponse.IndexerStatus
                {
                    Name = x.Name,
                    Ok = false,
                    Error = e.Message,
                    ElapsedMs = sw.ElapsedMilliseconds,
                }, Results: new List<SearchIndexersResponse.Result>());
            }
        }).ToList()).ConfigureAwait(false);

        return Ok(new SearchIndexersResponse
        {
            Results = perIndexer.SelectMany(x => x.Results)
                .GroupBy(x => x.NzbUrl)
                .Select(g => g.First())
                .OrderByDescending(x => x.Posted ?? DateTimeOffset.MinValue)
                .ToList(),
            Indexers = perIndexer.Select(x => x.Status).ToList(),
        });
    }
}
