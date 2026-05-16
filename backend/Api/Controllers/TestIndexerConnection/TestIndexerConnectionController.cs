using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Indexers;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

[ApiController]
[Route("api/test-indexer-connection")]
public class TestIndexerConnectionController : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestIndexerConnectionRequest(HttpContext);
        try
        {
            var client = new NewznabClient(request.Url, request.ApiKey);
            var ok = await client.TestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = ok });
        }
        catch (Exception e)
        {
            return Ok(new TestIndexerConnectionResponse { Status = true, Connected = false, Error = e.Message });
        }
    }
}
