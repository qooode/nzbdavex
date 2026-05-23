using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.AddUrl;

public class AddUrlRequest() : AddFileRequest
{
    private const int MaxAutomaticRedirections = 10;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    public static async Task<AddUrlRequest> New(HttpContext context, ConfigManager configManager)
    {
        var nzbUrl = context.GetRequestParam("name");
        var nzbName = context.GetRequestParam("nzbname");
        var userAgent = configManager.GetUserAgent();
        var proxyUrl = configManager.GetIndexerConfig().ProxyUrl;
        var nzbFile = await GetNzbFile(nzbUrl, nzbName, userAgent, proxyUrl).ConfigureAwait(false);
        return new AddUrlRequest()
        {
            FileName = nzbFile.FileName,
            ContentType = nzbFile.ContentType,
            NzbFileStream = nzbFile.FileStream,
            Category = context.GetRequestParam("cat") ?? configManager.GetManualUploadCategory(),
            Priority = MapPriorityOption(context.GetRequestParam("priority")),
            PostProcessing = MapPostProcessingOption(context.GetRequestParam("pp")),
            CancellationToken = context.RequestAborted
        };
    }

    private static async Task<NzbFileResponse> GetNzbFile(string? url, string? nzbName, string userAgent, string? proxyUrl)
    {
        try
        {
            // validate url
            if (string.IsNullOrWhiteSpace(url))
                throw new Exception($"The url is invalid.");

            // fetch url
            var response = await GetAsync(url, userAgent, proxyUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Received status code {response.StatusCode}.");

            // read the content type
            var contentType = response.Content.Headers.ContentType?.MediaType;

            // determine the filename
            var fileName = AddNzbExtension(nzbName)
                           ?? GetFilenameFromResponseHeader(response)
                           ?? GetFilenameFromUrl(url)
                           ?? throw new Exception("Nzb filename could not be determined.");

            // read the file contents
            var fileStream = await response.Content.ReadAsStreamAsync();

            // return response
            return new NzbFileResponse
            {
                FileName = fileName,
                ContentType = contentType,
                FileStream = fileStream
            };
        }
        catch (Exception ex)
        {
            throw new BadHttpRequestException($"Failed to fetch nzb-file url `{url}`: {ex.Message}");
        }
    }

    private static string? AddNzbExtension(string? nzbName)
    {
        return nzbName == null ? null
            : nzbName.ToLower().EndsWith("nzb") ? nzbName
            : $"{nzbName}.nzb";
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, string userAgent, string? proxyUrl)
    {
        var httpClient = ProxyHttpClientPool.GetClient(proxyUrl);
        using var cts = new CancellationTokenSource(FetchTimeout);
        var response = await SendGetAsync(httpClient, url, userAgent, cts.Token).ConfigureAwait(false);
        var remainingRedirects = MaxAutomaticRedirections;
        while
        (
            (int)response.StatusCode is >= 300 and < 400
            && remainingRedirects > 0
            && response.Headers.Location is not null
            && EnvironmentUtil.IsVariableTrue("ALLOW_HTTPS_TO_HTTP_REDIRECTS")
        )
        {
            var redirect = response.Headers.Location;
            var redirectUri = redirect.IsAbsoluteUri ? redirect : new Uri(new Uri(url), redirect);
            response = await SendGetAsync(httpClient, redirectUri.ToString(), userAgent, cts.Token).ConfigureAwait(false);
            remainingRedirects--;
        }

        return response;
    }

    private static Task<HttpResponseMessage> SendGetAsync(HttpClient client, string url, string userAgent, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return client.SendAsync(req, ct);
    }

    private static string? GetFilenameFromResponseHeader(HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var filename = contentDisposition?.FileName?.Trim('"');
        return StringUtil.EmptyToNull(filename);
    }

    private static string? GetFilenameFromUrl(string url)
    {
        try
        {
            var filename = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(filename)) return null;
            filename = Uri.UnescapeDataString(filename);
            filename = AddNzbExtension(filename);
            return filename;
        }
        catch
        {
            return null;
        }
    }

    private class NzbFileResponse
    {
        public required string FileName { get; init; }
        public required string? ContentType { get; init; }
        public required Stream FileStream { get; init; }
    }
}