using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

public class PreflightOrchestrator(
    ConfigManager configManager,
    PreflightCache preflightCache,
    PlaybackFastVerifier fastVerifier,
    NewznabRateLimiter rateLimiter,
    LazyRarResolver lazyRarResolver,
    PreflightSessionRegistry sessionRegistry,
    CandidateNegativeCache negativeCache)
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    public void Start(
        string profileToken,
        string type,
        string id,
        IReadOnlyList<NzbResolutionCache.Candidate> candidates)
    {
        var mode = configManager.GetPreflightMode();
        if (mode == "off" || candidates.Count == 0) return;

        var session = sessionRegistry.BeginSession(profileToken, type, id);

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var prepared = 0;
            try
            {
                prepared = await PreflightAsync(mode, candidates, session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Debug(e, "Preflight failed for {Type}/{Id}", type, id);
            }
            finally
            {
                session.Dispose();
                if (prepared > 0)
                    Log.Debug("Preflight readied {Count} candidate(s) for {Type}/{Id} in {Ms}ms (mode={Mode})",
                        prepared, type, id, sw.ElapsedMilliseconds, mode);
            }
        });
    }

    private async Task<int> PreflightAsync(
        string mode,
        IReadOnlyList<NzbResolutionCache.Candidate> candidates,
        CancellationToken ct)
    {
        var maxAttempts = Math.Min(configManager.GetPreflightMaxAttempts(), candidates.Count);
        var maxWait = TimeSpan.FromSeconds(configManager.GetPreflightIndexerMaxWaitSeconds());
        var indexers = configManager.GetIndexerConfig().Indexers
            .ToDictionary(x => x.Name, x => x);

        for (var i = 0; i < maxAttempts; i++)
        {
            if (ct.IsCancellationRequested) return 0;
            var ok = await PreflightCandidateAsync(mode, candidates[i], indexers, maxWait, ct).ConfigureAwait(false);
            if (ok) return i + 1;
        }
        return 0;
    }

    private async Task<bool> PreflightCandidateAsync(
        string mode,
        NzbResolutionCache.Candidate candidate,
        IReadOnlyDictionary<string, IndexerConfig.ConnectionDetails> indexers,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;

        if (indexers.TryGetValue(candidate.IndexerName, out var indexer))
        {
            var reserved = await rateLimiter
                .TryWaitAsync(candidate.IndexerName, indexer.MaxRequestsPerMinute, maxWait, ct)
                .ConfigureAwait(false);
            if (!reserved) return false;
        }

        var nzbBytes = await FetchNzbBytesAsync(candidate, ct).ConfigureAwait(false);
        if (nzbBytes is null || ct.IsCancellationRequested) return false;

        PlaybackFastVerifier.VerifyOutcome outcome;
        using (var ms = new MemoryStream(nzbBytes, writable: false))
        {
            outcome = await fastVerifier.VerifyAsync(ms, "stat", ct).ConfigureAwait(false);
        }

        if (outcome.Verdict == PlaybackFastVerifier.Verdict.Dead)
        {
            negativeCache.MarkFailed(candidate.NzbUrl);
            return false;
        }

        var bytesToCache = mode == "light" ? null : nzbBytes;
        preflightCache.SetVerified(candidate.NzbUrl, bytesToCache, outcome.Verdict, outcome.ResponderHost);

        if (mode == "full" && !ct.IsCancellationRequested)
        {
            await TryPreWarmExistingAsync(candidate, ct).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<byte[]?> FetchNzbBytesAsync(
        NzbResolutionCache.Candidate c,
        CancellationToken ct)
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
            Log.Debug("Preflight NZB fetch failed for {Url}: {Message}", c.NzbUrl, e.Message);
            return null;
        }
    }

    private async Task TryPreWarmExistingAsync(NzbResolutionCache.Candidate candidate, CancellationToken ct)
    {
        try
        {
            var fileName = $"{SanitizeFileName(candidate.Title)}.nzb";
            await using var ctx = new DavDatabaseContext();

            var historyId = await ctx.HistoryItems.AsNoTracking()
                .Where(h => h.FileName == fileName
                            && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => (Guid?)h.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (historyId is null) return;

            var davItem = await ctx.Items.AsNoTracking()
                .Where(x => x.HistoryItemId == historyId.Value
                            && x.Type == DavItem.ItemType.UsenetFile
                            && x.SubType == DavItem.ItemSubType.MultipartFile)
                .OrderByDescending(x => x.FileSize ?? 0)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (davItem is null) return;

            var client = new DavDatabaseClient(ctx);
            var mpf = await client.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            if (mpf is null || !mpf.Metadata.IsLazy) return;
            if ((mpf.Metadata.PendingParts?.Length ?? 0) == 0) return;

            await lazyRarResolver.EnsureResolvedThroughAsync(mpf, long.MaxValue, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Preflight lazy pre-warm failed for {Title}", candidate.Title);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "untitled" : clean;
    }
}
