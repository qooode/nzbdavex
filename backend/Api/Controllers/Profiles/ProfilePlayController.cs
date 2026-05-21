using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
    PlaybackAttemptLog attemptLog,
    NewznabRateLimiter rateLimiter,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    QueueItemSourceTracker sourceTracker,
    LazyRarResolver lazyRarResolver,
    PreflightCache preflightCache,
    PreflightSessionRegistry preflightSessions,
    VariantResolver variantResolver
) : ControllerBase
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // Tracks the last time a Play click touched a watchdog-created queue item.
    // Used by ScheduleOrphanCleanup to remove items the player has stopped polling.
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> _playLastSeen = new();
    private static readonly TimeSpan OrphanIdleThreshold = TimeSpan.FromMinutes(5);

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

        preflightSessions.Cancel(entry.ProfileToken, entry.Type, entry.Id);

        var skipLegacyMatch = false;
        if (variantResolver.IsEnabled)
        {
            var decision = await variantResolver.ResolveAsync(dbClient.Ctx, entry, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (decision.ReuseMatch is not null)
            {
                var variantRedirect = await BuildRedirectForHistoryItemAsync(
                    decision.ReuseMatch.Row.HistoryItemId, entry, HttpContext.RequestAborted).ConfigureAwait(false);
                if (variantRedirect is not null) return variantRedirect;
            }
            skipLegacyMatch = decision.GroupHasMembers;
        }

        if (!skipLegacyMatch)
        {
            var existingResolved = await TryResolveExistingAsync(entry, HttpContext.RequestAborted).ConfigureAwait(false);
            if (existingResolved is not null) return existingResolved;
        }

        if (variantResolver.IsEnabled)
        {
            var inFlight = await variantResolver.FindInFlightAsync(dbClient.Ctx, entry, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (inFlight.HasValue)
            {
                var waitRedirect = await WaitForInFlightAsync(inFlight.Value, entry, HttpContext.RequestAborted)
                    .ConfigureAwait(false);
                if (waitRedirect is not null) return waitRedirect;
            }
        }

        var totalBudget = TimeSpan.FromSeconds(configManager.GetPlayTotalBudgetSeconds());
        var hedgeDelay = TimeSpan.FromSeconds(configManager.GetPlayHedgeDelaySeconds());
        var maxCandidates = configManager.GetPlayMaxCandidates();
        var maxAttempts = configManager.GetPlayMaxAttempts();
        var verifyMode = configManager.GetPlayVerifyMode();
        var watchdogEnabled = configManager.IsPlaybackWatchdogEnabled();
        var excludePatterns = configManager.GetPlayExcludePatterns();

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        totalCts.CancelAfter(totalBudget);
        var deadline = DateTimeOffset.UtcNow + totalBudget;

        var clickId = Guid.NewGuid();
        var requestedTitle = entry.Primary.Title;
        var contentType = entry.Type;
        var startsAt = new Dictionary<string, DateTimeOffset>();

        // Watchdog OFF → simple single-candidate flow (no auto-fallback, no pre-verify, no negative cache).
        if (!watchdogEnabled)
        {
            var primaryExcludeMatch = MatchExcludePattern(entry.Primary.Title, excludePatterns);
            if (primaryExcludeMatch != null)
            {
                startsAt[entry.Primary.NzbUrl] = DateTimeOffset.UtcNow;
                RecordAttempt(clickId, entry.Primary, contentType, requestedTitle, 0,
                    PlaybackAttemptLog.Outcome.ExcludedByPattern,
                    $"Matched exclude pattern: {primaryExcludeMatch}", startsAt, isWinner: false);
                return await ResolveExistingOrErrorAsync(entry, 503,
                    "Release excluded by your filter. Adjust patterns in Settings → Watchdog.",
                    60, HttpContext.RequestAborted).ConfigureAwait(false);
            }
            startsAt[entry.Primary.NzbUrl] = DateTimeOffset.UtcNow;
            var nzbBytes = await FetchNzbBytesAsync(entry.Primary, totalCts.Token).ConfigureAwait(false);
            if (nzbBytes is null)
            {
                RecordAttempt(clickId, entry.Primary, contentType, requestedTitle, 0,
                    PlaybackAttemptLog.Outcome.EnqueueFailed, "Indexer NZB fetch failed", startsAt, isWinner: false);
                return await ResolveExistingOrErrorAsync(entry, 502,
                    "Failed to fetch NZB from indexer.", 10, HttpContext.RequestAborted).ConfigureAwait(false);
            }
            var single = new PreVerifyResult(entry.Primary, nzbBytes, PlaybackFastVerifier.Verdict.Available, null);
            var (result, reason, newNzoId) = await CommitAsync(nzbToken, single, deadline, totalCts.Token).ConfigureAwait(false);
            RecordAttempt(clickId, entry.Primary, contentType, requestedTitle, 0,
                MapCommitReason(reason), CommitReasonToMessage(reason), startsAt, isWinner: reason == CommitReason.Completed);
            if (reason == CommitReason.BudgetTimeout && newNzoId.HasValue)
                ScheduleOrphanCleanup(newNzoId.Value);
            if (result is not null) return result;
            return await ResolveExistingOrErrorAsync(entry, 503,
                "Still processing. Retry the link in a few seconds.", 5, HttpContext.RequestAborted).ConfigureAwait(false);
        }

        // Batch retry loop: try up to maxCandidates candidates in parallel per batch;
        // if all in a batch fail, advance to the next batch — until a winner, budget elapses,
        // total attempts (maxAttempts) are exhausted, or we run out of cached candidates.
        var fallbackQueue = BuildFallbackQueue(entry);
        var rankIndex = new Dictionary<string, int>();
        var displayRank = 0;
        var queueIndex = 0;
        var attemptsUsed = 0;
        var sawAnyBatch = false;
        var excludedCount = 0;

        while (attemptsUsed < maxAttempts && queueIndex < fallbackQueue.Count)
        {
            if (totalCts.IsCancellationRequested) break;
            if (DateTimeOffset.UtcNow >= deadline) break;

            var batchBudget = Math.Min(maxCandidates, maxAttempts - attemptsUsed);
            var pool = new List<NzbResolutionCache.Candidate>();
            while (queueIndex < fallbackQueue.Count && pool.Count < batchBudget)
            {
                var c = fallbackQueue[queueIndex];
                queueIndex++;
                if (negativeCache.IsFailed(c.NzbUrl)) continue;
                var excludeMatch = MatchExcludePattern(c.Title, excludePatterns);
                if (excludeMatch != null)
                {
                    var excludedRank = displayRank++;
                    rankIndex[c.NzbUrl] = excludedRank;
                    startsAt[c.NzbUrl] = DateTimeOffset.UtcNow;
                    RecordAttempt(clickId, c, contentType, requestedTitle, excludedRank,
                        PlaybackAttemptLog.Outcome.ExcludedByPattern,
                        $"Matched exclude pattern: {excludeMatch}", startsAt, isWinner: false);
                    excludedCount++;
                    continue;
                }
                rankIndex[c.NzbUrl] = displayRank++;
                pool.Add(c);
            }
            if (pool.Count == 0) break;

            sawAnyBatch = true;
            attemptsUsed += pool.Count;

            var batch = await RunBatchAsync(pool, rankIndex, nzbToken, contentType, requestedTitle,
                clickId, startsAt, verifyMode, hedgeDelay, deadline, totalCts).ConfigureAwait(false);

            switch (batch.Outcome)
            {
                case BatchOutcome.Winner:
                case BatchOutcome.Cancelled:
                    return batch.Action!;
                case BatchOutcome.BudgetTimeout:
                    return await ResolveExistingOrErrorAsync(entry, 503,
                        "Still processing. Retry the link in a few seconds.", 5,
                        HttpContext.RequestAborted).ConfigureAwait(false);
                case BatchOutcome.AllFailed:
                    break; // try next batch
            }
        }

        if (!sawAnyBatch)
        {
            var msg = excludedCount > 0
                ? "All candidates excluded by your filters. Adjust patterns in Settings → Watchdog."
                : "All ranked candidates recently failed; try again shortly.";
            return await ResolveExistingOrErrorAsync(entry, 503, msg, 5,
                HttpContext.RequestAborted).ConfigureAwait(false);
        }

        return await ResolveExistingOrErrorAsync(entry, 503,
            "All tried candidates failed. Retry in a few seconds.", 5,
            HttpContext.RequestAborted).ConfigureAwait(false);
    }

    private static List<NzbResolutionCache.Candidate> BuildFallbackQueue(NzbResolutionCache.Entry entry)
    {
        var primary = entry.Primary;
        var queue = new List<NzbResolutionCache.Candidate>(entry.Candidates.Count) { primary };

        var others = entry.Candidates
            .Where((_, i) => i != entry.StartIndex)
            .ToList();

        if (primary.Size <= 0)
        {
            queue.AddRange(others.OrderByDescending(c => c.Size));
            return queue;
        }

        queue.AddRange(others.Where(c => c.Size <= primary.Size).OrderByDescending(c => c.Size));
        queue.AddRange(others.Where(c => c.Size > primary.Size).OrderBy(c => c.Size));

        return queue;
    }

    private static string? MatchExcludePattern(string title, IReadOnlyList<Regex> patterns)
    {
        if (patterns.Count == 0 || string.IsNullOrEmpty(title)) return null;
        foreach (var p in patterns)
        {
            try
            {
                if (p.IsMatch(title)) return p.ToString();
            }
            catch (RegexMatchTimeoutException)
            {
                // Pathological input; skip this pattern but don't block playback.
                Log.Warning("Exclude pattern {Pattern} timed out matching title {Title}", p, title);
            }
        }
        return null;
    }

    private enum BatchOutcome { Winner, AllFailed, Cancelled, BudgetTimeout }

    private async Task<(BatchOutcome Outcome, IActionResult? Action)> RunBatchAsync(
        List<NzbResolutionCache.Candidate> pool,
        Dictionary<string, int> rankIndex,
        string nzbToken,
        string contentType,
        string requestedTitle,
        Guid clickId,
        Dictionary<string, DateTimeOffset> startsAt,
        string verifyMode,
        TimeSpan hedgeDelay,
        DateTimeOffset deadline,
        CancellationTokenSource totalCts)
    {
        foreach (var c in pool) startsAt[c.NzbUrl] = DateTimeOffset.UtcNow;

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
                {
                    startsAt[pool[i].NzbUrl] = DateTimeOffset.UtcNow;
                    preVerifies.Add(PreVerifyAsync(pool[i], verifyMode, totalCts.Token));
                }
            }
        }

        // Phase 2 — commit. Take pre-verified candidates in their original ranking order,
        // try to commit each (enqueue + poll), first one to complete wins.
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
                        RecordAttempt(clickId, r.Candidate, contentType, requestedTitle,
                            rankIndex[r.Candidate.NzbUrl],
                            PlaybackAttemptLog.Outcome.PreVerifyDead,
                            "STAT/HEAD reported article missing on every provider", startsAt, isWinner: false,
                            providerHost: AllConfiguredProvidersDisplay());
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
                var (action, reason, newNzoId) = await CommitAsync(nzbToken, best, deadline, totalCts.Token).ConfigureAwait(false);
                RecordAttempt(clickId, best.Candidate, contentType, requestedTitle,
                    rankIndex[best.Candidate.NzbUrl],
                    MapCommitReason(reason), CommitReasonToMessage(reason), startsAt,
                    isWinner: reason == CommitReason.Completed,
                    providerHost: best.ResponderHost);
                if (action is not null)
                {
                    // Winner found. Cancel in-flight losers so they stop holding
                    // indexer/NNTP connections, and log them in /watchdog as Cancelled.
                    CancelRemainingAndRecord(clickId, contentType, requestedTitle,
                        rankIndex, startsAt, remaining, ready, totalCts,
                        "Winner found; loser cancelled to free provider connections");
                    return (BatchOutcome.Winner, action);
                }
                if (reason == CommitReason.QueueFailed || reason == CommitReason.EnqueueFailed)
                    negativeCache.MarkFailed(best.Candidate.NzbUrl);
                if (reason == CommitReason.Cancelled)
                {
                    CancelRemainingAndRecord(clickId, contentType, requestedTitle,
                        rankIndex, startsAt, remaining, ready, totalCts,
                        "Client disconnected; loser cancelled");
                    return (BatchOutcome.Cancelled, new EmptyResult());
                }
                if (reason == CommitReason.BudgetTimeout)
                {
                    // The queue item is still processing. Schedule cleanup so we
                    // don't keep downloading a UHD release that the player gave up on.
                    if (newNzoId.HasValue) ScheduleOrphanCleanup(newNzoId.Value);
                    CancelRemainingAndRecord(clickId, contentType, requestedTitle,
                        rankIndex, startsAt, remaining, ready, totalCts,
                        "Budget exhausted; loser cancelled");
                    // HandleAsync converts this to a 503 + Retry-After (or a 302 if
                    // another click finished the same group in the meantime).
                    return (BatchOutcome.BudgetTimeout, null);
                }
                continue;
            }

            if (remaining.Count == 0) break;
            if (DateTimeOffset.UtcNow >= deadline) break;
            await Task.WhenAny(remaining).ConfigureAwait(false);
        }

        // All candidates in this batch failed (dead or commit-failed). Caller may try another batch.
        return (BatchOutcome.AllFailed, null);
    }

    // Signal in-flight pre-verify tasks to abort (freeing indexer/NNTP connections),
    // and record both pre-verified-but-unused candidates and in-flight ones as Cancelled
    // so they show up in /watchdog instead of vanishing silently.
    private void CancelRemainingAndRecord(
        Guid clickId,
        string contentType,
        string requestedTitle,
        Dictionary<string, int> rankIndex,
        Dictionary<string, DateTimeOffset> startsAt,
        List<Task<PreVerifyResult>> remaining,
        SortedList<int, PreVerifyResult> ready,
        CancellationTokenSource totalCts,
        string reason)
    {
        if (!totalCts.IsCancellationRequested)
        {
            try { totalCts.Cancel(); }
            catch (ObjectDisposedException) { /* race with using disposal — already done */ }
        }

        // Already-verified but unused — no connections to release, just visibility.
        foreach (var r in ready.Values)
        {
            RecordAttempt(clickId, r.Candidate, contentType, requestedTitle,
                rankIndex[r.Candidate.NzbUrl],
                PlaybackAttemptLog.Outcome.Cancelled, reason, startsAt, isWinner: false);
        }

        // Still in flight — record their cancellation in the background once they observe it.
        // Don't await synchronously; the response should return immediately.
        if (remaining.Count == 0) return;
        var pending = remaining.ToList();
        var localStarts = new Dictionary<string, DateTimeOffset>(startsAt);
        var localRanks = new Dictionary<string, int>(rankIndex);
        _ = Task.Run(async () =>
        {
            foreach (var t in pending)
            {
                try
                {
                    var r = await t.ConfigureAwait(false);
                    RecordAttempt(clickId, r.Candidate, contentType, requestedTitle,
                        localRanks[r.Candidate.NzbUrl],
                        PlaybackAttemptLog.Outcome.Cancelled, reason, localStarts, isWinner: false);
                }
                catch
                {
                    // Task didn't produce a result we can identify — skip silently.
                }
            }
        });
    }

    // After a BudgetTimeout the queue item keeps processing in case the player re-clicks.
    // If no click touches it for OrphanIdleThreshold, remove it so we don't burn provider
    // bandwidth downloading a release the player has clearly given up on.
    private void ScheduleOrphanCleanup(Guid nzoId)
    {
        var manager = queueManager;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(OrphanIdleThreshold).ConfigureAwait(false);

                if (!_playLastSeen.TryGetValue(nzoId, out var lastSeen))
                    return; // already cleaned up (success path / earlier cleanup)

                var idle = DateTimeOffset.UtcNow - lastSeen;
                if (idle < OrphanIdleThreshold)
                {
                    // Re-clicked recently — keep the item, check again later.
                    ScheduleOrphanCleanup(nzoId);
                    return;
                }

                await using var ctx = new DavDatabaseContext();
                var freshDbClient = new DavDatabaseClient(ctx);
                var stillPending = await ctx.QueueItems.AsNoTracking()
                    .AnyAsync(q => q.Id == nzoId).ConfigureAwait(false);
                if (!stillPending)
                {
                    _playLastSeen.TryRemove(nzoId, out _);
                    return;
                }

                Log.Information(
                    "Removing orphan play-driven queue item {NzoId} after {IdleSeconds}s with no re-click",
                    nzoId, (int)idle.TotalSeconds);
                await manager.RemoveQueueItemsAsync(new List<Guid> { nzoId }, freshDbClient, CancellationToken.None)
                    .ConfigureAwait(false);
                _playLastSeen.TryRemove(nzoId, out _);
            }
            catch (Exception e)
            {
                Log.Debug(e, "Orphan cleanup for {NzoId} failed", nzoId);
                _playLastSeen.TryRemove(nzoId, out _);
            }
        });
    }

    private void RecordAttempt(
        Guid clickId,
        NzbResolutionCache.Candidate c,
        string contentType,
        string requestedTitle,
        int rankIndex,
        PlaybackAttemptLog.Outcome outcome,
        string? failReason,
        Dictionary<string, DateTimeOffset> startsAt,
        bool isWinner,
        string? providerHost = null)
    {
        var attemptedAt = startsAt.GetValueOrDefault(c.NzbUrl, DateTimeOffset.UtcNow);
        attemptLog.Record(new PlaybackAttemptLog.Attempt
        {
            ClickId = clickId,
            AttemptedAt = attemptedAt,
            ContentType = contentType,
            RequestedTitle = requestedTitle,
            CandidateTitle = c.Title,
            IndexerName = c.IndexerName,
            Size = c.Size,
            RankIndex = rankIndex,
            Result = outcome,
            FailReason = failReason,
            DurationMs = (int)Math.Max(0, (DateTimeOffset.UtcNow - attemptedAt).TotalMilliseconds),
            IsWinner = isWinner,
            ProviderHost = providerHost,
        });
    }

    private string AllConfiguredProvidersDisplay()
    {
        var hosts = configManager.GetUsenetProviderConfig().Providers
            .Select(p => p.Host)
            .Where(h => !string.IsNullOrEmpty(h))
            .Distinct()
            .ToList();
        return hosts.Count == 0 ? "—" : string.Join(", ", hosts);
    }

    private static PlaybackAttemptLog.Outcome MapCommitReason(CommitReason r) => r switch
    {
        CommitReason.Completed => PlaybackAttemptLog.Outcome.QueueCompleted,
        CommitReason.QueueFailed => PlaybackAttemptLog.Outcome.QueueFailed,
        CommitReason.EnqueueFailed => PlaybackAttemptLog.Outcome.EnqueueFailed,
        CommitReason.BudgetTimeout => PlaybackAttemptLog.Outcome.BudgetTimeout,
        CommitReason.Cancelled => PlaybackAttemptLog.Outcome.Cancelled,
        _ => PlaybackAttemptLog.Outcome.QueueFailed,
    };

    private static string? CommitReasonToMessage(CommitReason r) => r switch
    {
        CommitReason.Completed => null,
        CommitReason.QueueFailed => "Queue processing marked the item as Failed",
        CommitReason.EnqueueFailed => "Could not enqueue the NZB (DB or fetch error)",
        CommitReason.BudgetTimeout => "Queue still processing when total budget elapsed",
        CommitReason.Cancelled => "Client disconnected or request cancelled",
        _ => null,
    };

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
            var preflighted = preflightCache.Get(candidate.NzbUrl);
            if (preflighted is { Verdict: PlaybackFastVerifier.Verdict.Available, NzbBytes: { } cachedBytes })
            {
                return new PreVerifyResult(candidate, cachedBytes, preflighted.Verdict, preflighted.ResponderHost);
            }

            var nzbBytes = await FetchNzbBytesAsync(candidate, ct).ConfigureAwait(false);
            if (nzbBytes is null)
                return new PreVerifyResult(candidate, null, PlaybackFastVerifier.Verdict.Dead, null);

            using var verifyStream = new MemoryStream(nzbBytes, writable: false);
            var outcome = await fastVerifier.VerifyAsync(verifyStream, verifyMode, ct).ConfigureAwait(false);
            return new PreVerifyResult(candidate, nzbBytes, outcome.Verdict, outcome.ResponderHost);
        }
        catch (OperationCanceledException)
        {
            return new PreVerifyResult(candidate, null, PlaybackFastVerifier.Verdict.Timeout, null);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug(e, "Pre-verify failed for {Url}", candidate.NzbUrl);
            return new PreVerifyResult(candidate, null, PlaybackFastVerifier.Verdict.Dead, null);
        }
    }

    private async Task<byte[]?> FetchNzbBytesAsync(NzbResolutionCache.Candidate c, CancellationToken ct)
    {
        try
        {
            var preflighted = preflightCache.Get(c.NzbUrl);
            if (preflighted?.NzbBytes is { } cachedBytes) return cachedBytes;

            // Throttle NZB downloads to respect each indexer's configured rate limit
            // (MaxRequestsPerMinute). Candidates from a saturated indexer wait their turn while
            // candidates from other indexers in the same batch proceed in parallel.
            var indexer = configManager.GetIndexerConfig().Indexers
                .FirstOrDefault(x => x.Name == c.IndexerName);
            if (indexer is not null)
                await rateLimiter.WaitAsync(c.IndexerName, indexer.MaxRequestsPerMinute, ct).ConfigureAwait(false);

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

    private async Task<(IActionResult? Result, CommitReason Reason, Guid? NewlyEnqueuedNzoId)> CommitAsync(
        string nzbToken,
        PreVerifyResult preVerify,
        DateTimeOffset deadline,
        CancellationToken ct)
    {
        var c = preVerify.Candidate;
        var nzbBytes = preVerify.NzbBytes!;
        var safeTitle = SanitizeFileName(c.Title);
        var fileName = $"{safeTitle}.nzb";

        var cacheEntry = cache.Get(nzbToken);
        var category = StringUtil.EmptyToNull(cacheEntry?.Type)
                       ?? configManager.GetManualUploadCategory();
        var contentGroupKey = cacheEntry is null ? null : VariantResolver.BuildContentGroupKey(cacheEntry);

        Guid nzoId;
        Guid? newlyEnqueuedNzoId = null;
        try
        {
            var existingQueue = await dbClient.Ctx.QueueItems.AsNoTracking()
                .Where(q =>
                    (q.FileName == fileName && q.Category == category)
                    || (contentGroupKey != null && q.ContentGroupKey == contentGroupKey))
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
                    IndexerName = c.IndexerName,
                    ContentGroupKey = contentGroupKey,
                    CancellationToken = ct,
                };
                var addFileController = new AddFileController(HttpContext, dbClient, queueManager, configManager, websocketManager);
                var addResponse = await addFileController.AddFileAsync(addFileRequest).ConfigureAwait(false);
                if (addResponse.NzoIds.Count == 0) return (null, CommitReason.EnqueueFailed, null);
                nzoId = Guid.Parse(addResponse.NzoIds[0]);
                newlyEnqueuedNzoId = nzoId;
                // Tag this queue item as profile-flow so the queue processor doesn't
                // emit a redundant watchdog completion entry — we already log every
                // attempt explicitly via RecordAttempt above.
                sourceTracker.MarkAsProfileFlow(nzoId);
                // Mark this queue item as play-owned so orphan cleanup can identify it.
                _playLastSeen[nzoId] = DateTimeOffset.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            return (null, CommitReason.Cancelled, null);
        }
        catch (Exception e)
        {
            Log.Debug(e, "Enqueue failed for {Url}", c.NzbUrl);
            return (null, CommitReason.EnqueueFailed, null);
        }

        // If this click joined an existing play-owned queue item, refresh its
        // last-seen timestamp so orphan cleanup waits for our polling to finish.
        if (newlyEnqueuedNzoId is null && _playLastSeen.ContainsKey(nzoId))
            _playLastSeen[nzoId] = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return (null, CommitReason.Cancelled, newlyEnqueuedNzoId);

            HistoryItem? history;
            try
            {
                history = await dbClient.Ctx.HistoryItems.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.Id == nzoId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return (null, CommitReason.Cancelled, newlyEnqueuedNzoId); }

            if (history is not null)
            {
                _playLastSeen.TryRemove(nzoId, out _);
                if (history.DownloadStatus != HistoryItem.DownloadStatusOption.Completed)
                {
                    Log.Debug("Candidate {Url} processing failed: {Msg}", c.NzbUrl, history.FailMessage);
                    return (null, CommitReason.QueueFailed, newlyEnqueuedNzoId);
                }

                var redirect = await BuildRedirectForHistoryItemAsync(nzoId, cacheEntry, ct).ConfigureAwait(false);
                if (redirect is null) return (null, CommitReason.QueueFailed, newlyEnqueuedNzoId);

                if (newlyEnqueuedNzoId.HasValue && contentGroupKey is not null && variantResolver.IsEnabled)
                {
                    var keyCopy = contentGroupKey;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await using var bgCtx = new DavDatabaseContext();
                            var bgClient = new DavDatabaseClient(bgCtx);
                            await variantResolver.EnforceCapAsync(bgClient, websocketManager, keyCopy, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            Log.Debug(e, "Variants: background cap enforcement failed for group {Group}", keyCopy);
                        }
                    });
                }

                return (redirect, CommitReason.Completed, newlyEnqueuedNzoId);
            }

            // Refresh while actively polling so cleanup doesn't kill an item we're waiting on.
            if (_playLastSeen.ContainsKey(nzoId))
                _playLastSeen[nzoId] = DateTimeOffset.UtcNow;

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return (null, CommitReason.Cancelled, newlyEnqueuedNzoId); }
        }

        // Budget exhausted — caller is expected to schedule orphan cleanup so the
        // queue item doesn't keep downloading a release the player gave up on.
        return (null, CommitReason.BudgetTimeout, newlyEnqueuedNzoId);
    }

    // Before returning a transient error, re-check whether ANY prior or concurrent download
    // completed for any candidate in this group. Catches the race where another click — or a
    // sonarr/radarr backfill — finished while we were still processing this one. Falls back
    // to a structured error response with Retry-After so clients like Infuse retry instead
    // of surfacing "demux instantly error" on the first failed read.
    private async Task<IActionResult> ResolveExistingOrErrorAsync(
        NzbResolutionCache.Entry entry,
        int statusCode,
        string message,
        int retryAfterSeconds,
        CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
        {
            try
            {
                var existing = await TryResolveExistingAsync(entry, ct).ConfigureAwait(false);
                if (existing is not null) return existing;

                if (variantResolver.IsEnabled)
                {
                    var fallback = await variantResolver.TryFallbackAfterFailureAsync(dbClient.Ctx, entry, ct)
                        .ConfigureAwait(false);
                    if (fallback is not null)
                    {
                        var redirect = await BuildRedirectForHistoryItemAsync(fallback.Row.HistoryItemId, entry, ct)
                            .ConfigureAwait(false);
                        if (redirect is not null)
                        {
                            Response.Headers["X-Variants-Fallback"] = "1";
                            return redirect;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* fall through to error */ }
        }
        Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        return StatusCode(statusCode, message);
    }

    // Match against ANY candidate in this group — the winning fallback can land on a
    // release at any position in the ranked list, so matching only by Primary.Title would
    // miss prior clicks that resolved to a different release variant.
    private async Task<IActionResult?> TryResolveExistingAsync(NzbResolutionCache.Entry entry, CancellationToken ct)
    {
        var fileNames = entry.Candidates
            .Select(c => $"{SanitizeFileName(c.Title)}.nzb")
            .Distinct()
            .ToList();
        if (fileNames.Count == 0) return null;

        var existing = await dbClient.Ctx.HistoryItems.AsNoTracking()
            .Where(h => fileNames.Contains(h.FileName) && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
            .OrderByDescending(h => h.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (existing is null) return null;
        return await BuildRedirectForHistoryItemAsync(existing.Id, entry, ct).ConfigureAwait(false);
    }

    private async Task<IActionResult?> BuildRedirectForHistoryItemAsync(
        Guid historyItemId, NzbResolutionCache.Entry? entry, CancellationToken ct)
    {
        var video = await FindBestVideoAsync(historyItemId, entry, ct).ConfigureAwait(false);
        if (video is null) return null;
        var ext = Path.GetExtension(video.Name).TrimStart('.').ToLowerInvariant();
        var redirect = await BuildRedirectAsync(video.Id, ext, ct).ConfigureAwait(false);
        _ = variantResolver.MarkPlayedAsync(historyItemId, CancellationToken.None);
        return redirect;
    }

    private async Task<IActionResult?> WaitForInFlightAsync(Guid nzoId, NzbResolutionCache.Entry entry, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(configManager.GetPlayTotalBudgetSeconds());
        _playLastSeen[nzoId] = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return null;
            HistoryItem? history;
            try
            {
                history = await dbClient.Ctx.HistoryItems.AsNoTracking()
                    .FirstOrDefaultAsync(h => h.Id == nzoId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }

            if (history is not null)
            {
                _playLastSeen.TryRemove(nzoId, out _);
                if (history.DownloadStatus != HistoryItem.DownloadStatusOption.Completed) return null;
                return await BuildRedirectForHistoryItemAsync(nzoId, entry, ct).ConfigureAwait(false);
            }

            if (_playLastSeen.ContainsKey(nzoId))
                _playLastSeen[nzoId] = DateTimeOffset.UtcNow;
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private async Task<DavItem?> FindLargestVideoAsync(Guid historyItemId, CancellationToken ct)
        => await FindBestVideoAsync(historyItemId, entry: null, ct).ConfigureAwait(false);

    private static readonly char[] TokenSeparators = ['.', '_', '-', ' ', '(', ')', '[', ']', '{', '}', '+'];

    private async Task<DavItem?> FindBestVideoAsync(Guid historyItemId, NzbResolutionCache.Entry? entry, CancellationToken ct)
    {
        var files = await dbClient.Ctx.Items.AsNoTracking()
            .Where(x => x.HistoryItemId == historyItemId)
            .Where(x => x.Type == DavItem.ItemType.UsenetFile)
            .ToListAsync(ct).ConfigureAwait(false);

        var videos = files
            .Where(x => ContentTypeUtil.GetContentType(x.Name).StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (videos.Count == 0) return null;
        if (videos.Count == 1) return videos[0];

        if (entry is not null)
        {
            var clickTokens = TokenizeName(entry.Primary.Title);
            if (clickTokens.Count > 0)
            {
                DavItem? best = null;
                int bestScore = 0;
                long bestSize = -1;
                foreach (var v in videos)
                {
                    if (v.Name.Contains("sample", StringComparison.OrdinalIgnoreCase)) continue;
                    var fileTokens = TokenizeName(Path.GetFileNameWithoutExtension(v.Name));
                    if (fileTokens.Count == 0) continue;
                    var score = fileTokens.Count(t => clickTokens.Contains(t));
                    var size = v.FileSize ?? 0;
                    if (score > bestScore || (score == bestScore && score > 0 && size > bestSize))
                    {
                        best = v;
                        bestScore = score;
                        bestSize = size;
                    }
                }
                if (best is not null && bestScore > 0) return best;
            }
        }

        return videos.OrderByDescending(x => x.FileSize ?? 0).First();
    }

    private static HashSet<string> TokenizeName(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return s.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "untitled" : clean;
    }

    private async Task<IActionResult> BuildRedirectAsync(Guid davItemId, string extension, CancellationToken ct)
    {
        var baseUrl = HttpContext.GetPublicBaseUrl(configManager.GetBaseUrl());
        var path = DatabaseStoreSymlinkFile.GetTargetPath(davItemId, "", '/').TrimStart('/');
        var dlKey = GetWebdavItemRequest.GenerateDownloadKey(configManager.GetStrmKey(), path);

        // Block on full lazy resolution before handing the player a URL. The
        // resolved Meta is persisted by LazyRarResolver, so this one-time cost
        // covers every future open and every seek — players never race the
        // resolver after this returns.
        await PreWarmLazyResolutionAsync(davItemId, ct).ConfigureAwait(false);

        return Redirect($"{baseUrl}/view/{path}?downloadKey={dlKey}&extension={extension}");
    }

    private async Task PreWarmLazyResolutionAsync(Guid davItemId, CancellationToken ct)
    {
        try
        {
            await using var ctx = new DavDatabaseContext();
            var davItem = await ctx.Items.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == davItemId, ct)
                .ConfigureAwait(false);
            if (davItem is null || davItem.SubType != DavItem.ItemSubType.MultipartFile) return;

            var freshClient = new DavDatabaseClient(ctx);
            var mpf = await freshClient.GetDavMultipartFileAsync(davItem).ConfigureAwait(false);
            if (mpf is null) return;
            if (!mpf.Metadata.IsLazy) return;
            if ((mpf.Metadata.PendingParts?.Length ?? 0) == 0) return;

            await lazyRarResolver
                .EnsureResolvedThroughAsync(mpf, long.MaxValue, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Debug(e,
                "Pre-redirect lazy RAR pre-warm failed for {DavItemId}; on-demand resolver will pick it up",
                davItemId);
        }
    }

    private readonly record struct PreVerifyResult(
        NzbResolutionCache.Candidate Candidate,
        byte[]? NzbBytes,
        PlaybackFastVerifier.Verdict Verdict,
        string? ResponderHost);
}
