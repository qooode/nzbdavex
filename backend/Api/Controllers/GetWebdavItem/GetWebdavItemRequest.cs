using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetWebdavItem;

public class GetWebdavItemRequest
{
    public string Item { get; init; }
    public string? RangeHeader { get; init; }
    public bool ShouldDownload { get; init; }

    public GetWebdavItemRequest(HttpContext context)
    {
        // normalize path
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/")) path = path[1..];
        if (path.StartsWith("view")) path = path[4..];
        if (path.StartsWith("/")) path = path[1..];
        Item = path;

        // determine whether to download
        ShouldDownload = context.GetQueryParam("download")?.ToLower() == "true";

        // authenticate the downloadKey
        var downloadKey = context.Request.Query["downloadKey"];
        var configManager = (ConfigManager)context.Items["configManager"]!;
        if (!VerifyDownloadKey(downloadKey, Item, configManager))
            throw new UnauthorizedAccessException("Invalid download key");

        RangeHeader = context.Request.Headers["Range"].FirstOrDefault();
    }

    private static bool VerifyDownloadKey(string? downloadKey, string path, ConfigManager configManager)
    {
        if (path.StartsWith(".ids"))
        {
            // strm streams link items by id and use a different download key
            var strmKey = configManager.GetStrmKey();
            var expectedDownloadKey = GenerateDownloadKey(strmKey, path);
            if (downloadKey == expectedDownloadKey)
                return true;
        }

        var apiKey = EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
        return downloadKey == GenerateDownloadKey(apiKey, path);
    }

    public static string GenerateDownloadKey(string apiKey, string path)
    {
        var input = $"{path}_{apiKey}";
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(inputBytes);
        var hash = Convert.ToHexStringLower(hashBytes);
        return hash;
    }
}
