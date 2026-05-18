using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

[ApiController]
[Route("api/test-indexer-connection")]
public class TestIndexerConnectionController(NzbWebDAV.Config.ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestIndexerConnectionRequest(HttpContext);
        try
        {
            var ua = string.IsNullOrWhiteSpace(request.UserAgent) ? configManager.GetUserAgent() : request.UserAgent;
            var proxy = string.IsNullOrWhiteSpace(request.ProxyUrl)
                ? configManager.GetIndexerConfig().ProxyUrl
                : request.ProxyUrl;
            var client = new NewznabClient(request.Url, request.ApiKey, ua, proxy);
            var ok = await client.TestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = ok });
        }
        catch (Exception e)
        {
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = false, Error = e.Message });
        }
    }
}
