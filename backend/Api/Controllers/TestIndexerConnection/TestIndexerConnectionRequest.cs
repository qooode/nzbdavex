using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestIndexerConnection;

public class TestIndexerConnectionRequest
{
    public string Url { get; init; }
    public string ApiKey { get; init; }
    public string? UserAgent { get; init; }
    public string? ProxyUrl { get; init; }

    public TestIndexerConnectionRequest(HttpContext context)
    {
        Url = context.Request.Form["url"].FirstOrDefault()
              ?? throw new BadHttpRequestException("Indexer url is required");

        ApiKey = context.Request.Form["apiKey"].FirstOrDefault()
                 ?? throw new BadHttpRequestException("Indexer apiKey is required");

        UserAgent = context.Request.Form["userAgent"].FirstOrDefault();
        ProxyUrl = context.Request.Form["proxyUrl"].FirstOrDefault();
    }
}
