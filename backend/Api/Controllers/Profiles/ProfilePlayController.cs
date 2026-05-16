using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetWebdavItem;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Api.Controllers.Profiles;

[ApiController]
[Route("p/{token}/play/{nzbToken}.mkv")]
public class ProfilePlayController(
    ConfigManager configManager,
    NzbResolutionCache cache,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : ControllerBase
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly TimeSpan ProcessingTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    [HttpGet]
    public async Task<IActionResult> Get(string token, string nzbToken)
    {
        try
        {
            return await HandleAsync(token, nzbToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Error(e, "Play handler crashed for token {Token} / nzbToken {NzbToken}", token, nzbToken);
            if (HttpContext.Response.HasStarted) return new EmptyResult();
            return StatusCode(500, $"Internal error: {e.GetType().Name}: {e.Message}");
        }
    }

    private async Task<IActionResult> HandleAsync(string token, string nzbToken)
    {
        var profile = configManager.GetProfileConfig().Profiles.FirstOrDefault(x => x.Token == token);
        if (profile is null) return NotFound();

        var entry = cache.Get(nzbToken);
        if (entry is null) return NotFound("Stream link expired. Re-search in your player.");

        if (entry.DavItemId.HasValue && !string.IsNullOrEmpty(entry.VideoExtension))
            return BuildRedirect(entry.DavItemId.Value, entry.VideoExtension);

        var ct = HttpContext.RequestAborted;
        var safeTitle = SanitizeFileName(entry.Title);
        var fileName = $"{safeTitle}.nzb";

        var existing = await dbClient.Ctx.HistoryItems.AsNoTracking()
            .Where(h => h.FileName == fileName && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (existing is not null)
        {
            var existingVideo = await FindLargestVideoAsync(existing.Id, ct).ConfigureAwait(false);
            if (existingVideo is not null)
            {
                var existingExt = Path.GetExtension(existingVideo.Name).TrimStart('.').ToLowerInvariant();
                cache.UpdateResolved(nzbToken, existingVideo.Id, existingExt);
                return BuildRedirect(existingVideo.Id, existingExt);
            }
        }

        var buffer = new MemoryStream();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, entry.NzbUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", entry.IndexerUserAgent);
            using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return StatusCode(502, $"Indexer returned {(int)resp.StatusCode}.");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return StatusCode(502, $"Failed to fetch NZB: {e.Message}");
        }
        buffer.Position = 0;

        Guid nzoId;
        try
        {
            var addFileRequest = new AddFileRequest
            {
                FileName = fileName,
                ContentType = "application/x-nzb",
                NzbFileStream = buffer,
                Category = configManager.GetManualUploadCategory(),
                Priority = QueueItem.PriorityOption.Force,
                PostProcessing = QueueItem.PostProcessingOption.None,
                CancellationToken = ct,
            };
            var addFileController = new AddFileController(HttpContext, dbClient, queueManager, configManager, websocketManager);
            var addResponse = await addFileController.AddFileAsync(addFileRequest).ConfigureAwait(false);
            if (addResponse.NzoIds.Count == 0) return StatusCode(500, "Queueing failed.");
            nzoId = Guid.Parse(addResponse.NzoIds[0]);
        }
        catch (Exception e)
        {
            return StatusCode(500, $"Queueing failed: {e.GetType().Name}: {e.Message}");
        }

        var deadline = DateTime.UtcNow + ProcessingTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return new EmptyResult();

            var history = await dbClient.Ctx.HistoryItems.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == nzoId, ct).ConfigureAwait(false);

            if (history is not null)
            {
                if (history.DownloadStatus != HistoryItem.DownloadStatusOption.Completed)
                    return StatusCode(502, history.FailMessage ?? "Processing failed.");

                var video = await FindLargestVideoAsync(nzoId, ct).ConfigureAwait(false);
                if (video is null) return NotFound("No playable video found in NZB.");

                var ext = Path.GetExtension(video.Name).TrimStart('.').ToLowerInvariant();
                cache.UpdateResolved(nzbToken, video.Id, ext);
                return BuildRedirect(video.Id, ext);
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return StatusCode(504, "Timed out waiting for NZB processing.");
    }

    private async Task<DavItem?> FindLargestVideoAsync(Guid historyItemId, CancellationToken ct)
    {
        var files = await dbClient.Ctx.Items.AsNoTracking()
            .Where(x => x.HistoryItemId == historyItemId)
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .ToListAsync(ct).ConfigureAwait(false);

        return files
            .Where(x => FilenameUtil.IsVideoFile(x.Name))
            .OrderByDescending(x => x.FileSize ?? 0)
            .FirstOrDefault();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "stream" : clean;
    }

    private IActionResult BuildRedirect(Guid davItemId, string extension)
    {
        var baseUrl = HttpContext.GetPublicBaseUrl(configManager.GetBaseUrl());
        var path = DatabaseStoreSymlinkFile.GetTargetPath(davItemId, "", '/').TrimStart('/');
        var dlKey = GetWebdavItemRequest.GenerateDownloadKey(configManager.GetStrmKey(), path);
        return Redirect($"{baseUrl}/view/{path}?downloadKey={dlKey}&extension={extension}");
    }
}
