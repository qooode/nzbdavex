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
    CandidateNegativeCache negativeCache,
    PlaybackFastVerifier fastVerifier,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : ControllerBase
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    [HttpGet]
    public async Task<IActionResult> Get(string token, string nzbToken)
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
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

        // already-resolved (a previous click on the same token resolved it): shortcut
        if (entry.DavItemId.HasValue && !string.IsNullOrEmpty(entry.VideoExtension))
            return BuildRedirect(entry.DavItemId.Value, entry.VideoExtension);

        // already-downloaded by a prior request (same title): shortcut
        var existingResolved = await TryResolveExistingAsync(entry.Primary.Title, nzbToken, HttpContext.RequestAborted).ConfigureAwait(false);
        if (existingResolved is not null) return existingResolved;

        var totalBudget = TimeSpan.FromSeconds(configManager.GetPlayTotalBudgetSeconds());
        var hedgeDelay = TimeSpan.FromSeconds(configManager.GetPlayHedgeDelaySeconds());
        var maxCandidates = configManager.GetPlayMaxCandidates();
        var verifyMode = configManager.GetPlayVerifyMode();
        var watchdogEnabled = configManager.IsPlaybackWatchdogEnabled();

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        totalCts.CancelAfter(totalBudget);
        var deadline = DateTimeOffset.UtcNow + totalBudget;

        // Watchdog OFF → simple single-candidate flow (no auto-fallback, no pre-verify, no negative cache).
        if (!watchdogEnabled)
        {
            var nzbBytes = await FetchNzbBytesAsync(entry.Primary, totalCts.Token).ConfigureAwait(false);
            if (nzbBytes is null) return StatusCode(502, "Failed to fetch NZB from indexer.");
            var single = new PreVerifyResult(entry.Primary, nzbBytes, PlaybackFastVerifier.Verdict.Available);
            var (result, _) = await CommitAsync(nzbToken, single, deadline, totalCts.Token).ConfigureAwait(false);
            return result ?? StatusCode(504, "Still processing. Retry the link in a few seconds.");
        }

        var pool = entry.Candidates
            .Skip(entry.StartIndex)
            .Take(maxCandidates)
            .Where(c => !negativeCache.IsFailed(c.NzbUrl))
            .ToList();
        if (pool.Count == 0)
            return StatusCode(503, "All ranked candidates recently failed; try again shortly.");

        // Phase 1 — pre-verify (parallel, hedged): fetch NZB + verify first segment exists.
        var preVerifies = new List<Task<PreVerifyResult>>();
        preVerifies.Add(PreVerifyAsync(pool[0], verifyMode, totalCts.Token));

        if (pool.Count > 1)
        {
            // Give the primary a brief head start; if it hasn't passed by then, fire backups.
            var hedgeTask = Task.Delay(hedgeDelay, totalCts.Token);
            var settled = await Task.WhenAny(preVerifies[0], hedgeTask).ConfigureAwait(false);
            var primaryReady = settled == preVerifies[0]
                               && preVerifies[0].IsCompletedSuccessfully
                               && preVerifies[0].Result.Verdict == PlaybackFastVerifier.Verdict.Available;

            if (!primaryReady)
            {
                for (var i = 1; i < pool.Count; i++)
                    preVerifies.Add(PreVerifyAsync(pool[i], verifyMode, totalCts.Token));
            }
        }

        // Phase 2 — commit. Take pre-verified candidates in their original ranking order,
        // try to commit each (enqueue + poll), first one to complete wins.
        var rankIndex = new Dictionary<string, int>();
        for (var i = 0; i < pool.Count; i++) rankIndex[pool[i].NzbUrl] = i;

        var remaining = new List<Task<PreVerifyResult>>(preVerifies);
        var ready = new SortedList<int, PreVerifyResult>();

        while (remaining.Count > 0 || ready.Count > 0)
        {
            // Pull off any newly-settled pre-verifications.
            while (remaining.Count > 0)
            {
                var anyDone = remaining.FirstOrDefault(t => t.IsCompleted);
                if (anyDone == null) break;
                remaining.Remove(anyDone);
                var r = await anyDone.ConfigureAwait(false);
                switch (r.Verdict)
                {
                    case PlaybackFastVerifier.Verdict.Available:
                        ready[rankIndex[r.Candidate.NzbUrl]] = r;
                        break;
                    case PlaybackFastVerifier.Verdict.Dead:
                        negativeCache.MarkFailed(r.Candidate.NzbUrl);
                        break;
                    case PlaybackFastVerifier.Verdict.Timeout:
                        // Don't poison on timeout — provider was just slow.
                        // Try it anyway as a last resort if we run out of candidates.
                        ready[rankIndex[r.Candidate.NzbUrl] + 10000] = r;
                        break;
                }
            }

            if (ready.Count > 0)
            {
                var best = ready.Values[0];
                ready.RemoveAt(0);
                var (action, reason) = await CommitAsync(nzbToken, best, deadline, totalCts.Token).ConfigureAwait(false);
                if (action is not null) return action;
                if (reason == CommitReason.QueueFailed || reason == CommitReason.EnqueueFailed)
                    negativeCache.MarkFailed(best.Candidate.NzbUrl);
                if (reason == CommitReason.Cancelled) return new EmptyResult();
                continue;
            }

            if (remaining.Count == 0) break;
            if (DateTimeOffset.UtcNow >= deadline) break;
            await Task.WhenAny(remaining).ConfigureAwait(false);
        }

        // 504 with a hint: the queue item we enqueued may still be processing.
        // A retry from the player will pick it up via TryResolveExistingAsync.
        return StatusCode(504, "Still processing. Retry the link in a few seconds.");
    }

    private enum CommitReason
    {
        Completed,
        QueueFailed,
        EnqueueFailed,
        BudgetTimeout,
        Cancelled,
    }

    private async Task<PreVerifyResult> PreVerifyAsync(
        NzbResolutionCache.Candidate candidate,
        string verifyMode,
        CancellationToken ct)
    {
        try
        {
            var nzbBytes = await FetchNzbBytesAsync(candidate, ct).ConfigureAwait(false);
            if (nzbBytes is null)
                return new PreVerifyResult(candidate, null, PlaybackFastVerifier.Verdict.Dead);

            using var verifyStream = new MemoryStream(nzbBytes, writable: false);
            var verdict = await fastVerifier.VerifyAsync(verifyStream, verifyMode, ct).ConfigureAwait(false);
            return new PreVerifyResult(candidate, nzbBytes, verdict);
        }
        catch (OperationCanceledException)
        {
            return new PreVerifyResult(candidate, null, PlaybackFastVerifier.Verdict.Timeout);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug(e, "Pre-verify failed for {Url}", candidate.NzbUrl);
            return new PreVerifyResult(candidate, null, PlaybackFastVerifier.Verdict.Dead);
        }
    }

    private async Task<byte[]?> FetchNzbBytesAsync(NzbResolutionCache.Candidate c, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, c.NzbUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", c.IndexerUserAgent);
            using var resp = await HttpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug("NZB fetch failed for {Url}: {Message}", c.NzbUrl, e.Message);
            return null;
        }
    }

    private async Task<(IActionResult? Result, CommitReason Reason)> CommitAsync(
        string nzbToken,
        PreVerifyResult preVerify,
        DateTimeOffset deadline,
        CancellationToken ct)
    {
        var c = preVerify.Candidate;
        var nzbBytes = preVerify.NzbBytes!;
        var safeTitle = SanitizeFileName(c.Title);
        var fileName = $"{safeTitle}.nzb";

        var category = configManager.GetManualUploadCategory();

        // If a previous click already enqueued this NZB and is still processing,
        // skip the duplicate enqueue and just poll on the existing item.
        Guid nzoId;
        try
        {
            var existingQueue = await dbClient.Ctx.QueueItems.AsNoTracking()
                .Where(q => q.FileName == fileName && q.Category == category)
                .OrderByDescending(q => q.CreatedAt)
                .Select(q => (Guid?)q.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (existingQueue.HasValue)
            {
                nzoId = existingQueue.Value;
            }
            else
            {
                using var buffer = new MemoryStream(nzbBytes, writable: false);
                var addFileRequest = new AddFileRequest
                {
                    FileName = fileName,
                    ContentType = "application/x-nzb",
                    NzbFileStream = buffer,
                    Category = category,
                    Priority = QueueItem.PriorityOption.Force,
                    PostProcessing = QueueItem.PostProcessingOption.None,
                    CancellationToken = ct,
                };
                var addFileController = new AddFileController(HttpContext, dbClient, queueManager, configManager, websocketManager);
                var addResponse = await addFileController.AddFileAsync(addFileRequest).ConfigureAwait(false);
                if (addResponse.NzoIds.Count == 0) return (null, CommitReason.EnqueueFailed);
                nzoId = Guid.Parse(addResponse.NzoIds[0]);
            }
        }
        catch (OperationCanceledException)
        {
            return (null, CommitReason.Cancelled);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Enqueue failed for {Url}", c.NzbUrl);
            return (null, CommitReason.EnqueueFailed);
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return (null, CommitReason.Cancelled);

            HistoryItem? history;
            try
            {
                history = await dbClient.Ctx.HistoryItems.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == nzoId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return (null, CommitReason.Cancelled); }

            if (history is not null)
            {
                if (history.DownloadStatus != HistoryItem.DownloadStatusOption.Completed)
                {
                    Log.Debug("Candidate {Url} processing failed: {Msg}", c.NzbUrl, history.FailMessage);
                    return (null, CommitReason.QueueFailed);
                }

                var video = await FindLargestVideoAsync(nzoId, ct).ConfigureAwait(false);
                if (video is null) return (null, CommitReason.QueueFailed);

                var ext = Path.GetExtension(video.Name).TrimStart('.').ToLowerInvariant();
                cache.UpdateResolved(nzbToken, video.Id, ext);
                return (BuildRedirect(video.Id, ext), CommitReason.Completed);
            }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (null, CommitReason.Cancelled); }
        }

        // Budget exhausted — but the queue item we enqueued keeps processing.
        // A re-click from the player will find it via TryResolveExistingAsync next time.
        return (null, CommitReason.BudgetTimeout);
    }

    private async Task<IActionResult?> TryResolveExistingAsync(string title, string nzbToken, CancellationToken ct)
    {
        var safeTitle = SanitizeFileName(title);
        var fileName = $"{safeTitle}.nzb";

        var existing = await dbClient.Ctx.HistoryItems.AsNoTracking()
            .Where(h => h.FileName == fileName && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (existing is null) return null;
        var existingVideo = await FindLargestVideoAsync(existing.Id, ct).ConfigureAwait(false);
        if (existingVideo is null) return null;
        var ext = Path.GetExtension(existingVideo.Name).TrimStart('.').ToLowerInvariant();
        cache.UpdateResolved(nzbToken, existingVideo.Id, ext);
        return BuildRedirect(existingVideo.Id, ext);
    }

    private async Task<DavItem?> FindLargestVideoAsync(Guid historyItemId, CancellationToken ct)
    {
        var files = await dbClient.Ctx.Items.AsNoTracking()
            .Where(x => x.HistoryItemId == historyItemId)
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .ToListAsync(ct).ConfigureAwait(false);

        return files
            .Where(x => ContentTypeUtil.GetContentType(x.Name).StartsWith("video/", StringComparison.OrdinalIgnoreCase))
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

    private readonly record struct PreVerifyResult(
        NzbResolutionCache.Candidate Candidate,
        byte[]? NzbBytes,
        PlaybackFastVerifier.Verdict Verdict);
}
