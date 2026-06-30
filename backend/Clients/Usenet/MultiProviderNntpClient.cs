using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(
    List<MultiConnectionNntpClient> providers,
    ProviderUsageTracker usageTracker,
    MetricsWriter? metricsWriter = null,
    ProviderBytesTracker? bytesTracker = null,
    Func<bool>? cascadeEnabled = null
) : NntpClient
{
    private static readonly AsyncLocal<Guid?> ReadSessionScope = new();

    /// <summary>
    /// Tag the current async flow with a read-session id so SegmentFetch rows
    /// emitted while fulfilling this read can be correlated back to the session.
    /// Disposing the returned scope restores the previous value.
    /// </summary>
    public static IDisposable BeginReadSessionScope(Guid readSessionId)
    {
        var previous = ReadSessionScope.Value;
        ReadSessionScope.Value = readSessionId;
        return new ScopeReleaser(() => ReadSessionScope.Value = previous);
    }

    private sealed class ScopeReleaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    // Per-call attribution. Caller (e.g. PlaybackFastVerifier) sets a mutable
    // holder on AttributionContext BEFORE invoking; we read it inside the call and
    // mutate Host on a non-"missing" response. AsyncLocal reliably flows the holder
    // reference DOWN to us; mutating its property is then visible to the caller via
    // their reference (which sidesteps AsyncLocal's child→parent non-propagation).
    public sealed class ResponderAttribution { public string? Host; }
    public static readonly AsyncLocal<ResponderAttribution?> AttributionContext = new();

    private readonly object _selectLock = new();

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        ExceptionDispatchInfo? lastException = null;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);
                stopwatch.Stop();

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                    (priorMisses ??= new()).Add((provider.ProviderKey, SegmentFetch.FetchStatus.Missing));
                    continue;
                }

                // attribute the response to this provider, unless it was a "missing" hit
                // from the last provider (in which case nobody actually answered).
                if (attribution != null && result.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
                    attribution.Host = provider.Host;

                // record per-queue-item attribution only for bytes-bearing responses (BODY/ARTICLE).
                if (result is UsenetDecodedBodyResponse or UsenetDecodedArticleResponse
                    && result.ResponseType is UsenetResponseType.ArticleRetrievedBodyFollows
                                          or UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
                {
                    usageTracker.RecordSuccess(provider.ProviderKey);
                    RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, i);
                    if (i > 0)
                    {
                        usageTracker.RecordFailoverSave();
                        RecordFailoverMisses(priorMisses, rescuer: provider.ProviderKey);
                    }
                    result = WrapStreamForByteCounting(result, provider.ProviderKey);
                }
                else
                {
                    RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                }

                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.ProviderKey, reason, stopwatch.ElapsedMilliseconds, i);
                (priorMisses ??= new()).Add((provider.ProviderKey, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private void RecordFetch(string host, SegmentFetch.FetchStatus status, long durationMs, int retries)
    {
        if (metricsWriter == null) return;
        metricsWriter.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = host,
            ReadSessionId = ReadSessionScope.Value,
            Bytes = 0, // bytes flow lazily through CountingYencStream → ProviderBytesTracker
            DurationMs = (int)Math.Min(int.MaxValue, durationMs),
            Status = status,
            Retries = retries,
        });
    }

    private void RecordFailoverMisses(
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses,
        string rescuer)
    {
        if (metricsWriter == null || priorMisses == null) return;
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (from, reason) in priorMisses)
        {
            metricsWriter.RecordFailoverMiss(new FailoverMiss
            {
                At = at,
                FromProvider = from,
                ToProvider = rescuer,
                Reason = reason,
            });
        }
    }

    private T WrapStreamForByteCounting<T>(T result, string host) where T : UsenetResponse
    {
        if (bytesTracker == null) return result;
        return result switch
        {
            UsenetDecodedBodyResponse b
                => (T)(object)(b with { Stream = new CountingYencStream(b.Stream, bytesTracker, host) }),
            UsenetDecodedArticleResponse a
                => (T)(object)(a with { Stream = new CountingYencStream(a.Stream, bytesTracker, host) }),
            _ => result,
        };
    }

    private static SegmentFetch.FetchStatus ClassifyException(Exception ex)
    {
        if (ex is TimeoutException) return SegmentFetch.FetchStatus.Timeout;
        if (ex is UnauthorizedAccessException) return SegmentFetch.FetchStatus.Auth;
        if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException) return SegmentFetch.FetchStatus.Network;
        return SegmentFetch.FetchStatus.Other;
    }

    private List<MultiConnectionNntpClient> SelectOrderedProviders(out MultiConnectionNntpClient? reserved)
    {
        lock (_selectLock)
        {
            var enabled = providers
                .Where(x => x.ProviderType != ProviderType.Disabled)
                .Where(x => !IsOverLimit(x))
                .ToList();

            var healthy = enabled.Where(x => !x.IsTripped).ToList();
            var pool = healthy.Count > 0 ? healthy : enabled;

            var byTier = pool.OrderBy(x => x.ProviderType);
            var prioritized = cascadeEnabled?.Invoke() == true
                ? byTier.ThenBy(EffectivePriority)
                : byTier;
            var ordered = prioritized
                .ThenByDescending(x => GetRemainingBytes(x))
                .ThenBy(EstimatedDeliveryScore)
                .ToList();

            reserved = ordered.Count > 0 ? ordered[0] : null;
            reserved?.ReservePending();
            return ordered;
        }
    }

    private static int EffectivePriority(MultiConnectionNntpClient provider)
    {
        const int saturationDemotion = 1 << 20;
        return provider.Priority + (provider.HasSpareConnection ? 0 : saturationDemotion);
    }

    private double EstimatedDeliveryScore(MultiConnectionNntpClient provider)
    {
        var inFlight = provider.ActiveConnections + provider.PendingSelections + 1;
        var bytesPerMs = bytesTracker?.GetBytesPerMs(provider.ProviderKey) ?? 0d;
        return bytesPerMs > 0 ? inFlight / bytesPerMs : inFlight;
    }

    private bool IsOverLimit(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return false;
        var used = bytesTracker.GetLifetime(client.ProviderKey) + client.BytesUsedOffset;
        // Stop at the effective cutoff (95% of cap) so in-flight fetches that
        // already passed this check can't push the actual count past the cap.
        // See ProviderUsageHelper.EffectiveLimitFraction for the rationale.
        var effective = (long)(limit.Value * ProviderUsageHelper.EffectiveLimitFraction);
        return used >= effective;
    }

    private long GetRemainingBytes(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return long.MaxValue;
        var used = bytesTracker.GetLifetime(client.ProviderKey) + client.BytesUsedOffset;
        return Math.Max(0, limit.Value - used);
    }

    private static int ResolveDepth(MultiConnectionNntpClient primary, int fallbackDepth)
    {
        return primary.ConfiguredPipeliningDepth is int d and > 0
            ? Math.Clamp(d, 1, 64)
            : fallbackDepth;
    }

    public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;
        var effectiveDepth = ResolveDepth(primary, depth);

        var index = 0;
        await using var enumerator = primary.StatsPipelinedAsync(segmentIds, effectiveDepth, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (index < segmentIds.Count)
        {
            var moved = await TryMoveNextPipelinedAsync(enumerator).ConfigureAwait(false);
            if (!moved.Succeeded)
            {
                Log.Debug(moved.Error, "Pipelined STAT failed on provider {Provider}; rescuing remaining segment(s).",
                    primary.Host);
                await foreach (var rescued in RescueStatsAsync(segmentIds, index, orderedProviders, primary, cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return rescued;
                yield break;
            }
            if (!moved.HasValue)
            {
                await foreach (var rescued in RescueStatsAsync(segmentIds, index, orderedProviders, primary, cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return rescued;
                yield break;
            }

            var result = moved.Value!;
            index++;
            if (result.Exists)
            {
                yield return result;
                continue;
            }

            yield return await RescueStatAsync(result.SegmentId, orderedProviders, primary, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;
        var effectiveDepth = ResolveDepth(primary, depth);

        var index = 0;
        await using var enumerator = primary.DecodedBodiesPipelinedAsync(segmentIds, effectiveDepth, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (index < segmentIds.Count)
        {
            var moved = await TryMoveNextPipelinedAsync(enumerator).ConfigureAwait(false);
            if (!moved.Succeeded)
            {
                Log.Debug(moved.Error, "Pipelined BODY failed on provider {Provider}; rescuing remaining segment(s).",
                    primary.Host);
                var reason = moved.Error is null ? SegmentFetch.FetchStatus.Other : ClassifyException(moved.Error);
                await foreach (var rescued in RescueBodiesAsync(segmentIds, index, orderedProviders, primary, reason,
                                       cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return rescued;
                yield break;
            }
            if (!moved.HasValue)
            {
                await foreach (var rescued in RescueBodiesAsync(segmentIds, index, orderedProviders, primary,
                                       SegmentFetch.FetchStatus.Other, cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return rescued;
                yield break;
            }

            var result = moved.Value!;
            index++;
            if (result.Found)
            {
                usageTracker.RecordSuccess(primary.ProviderKey);
                yield return WrapPipelinedBody(result, primary.ProviderKey);
            }
            else
            {
                yield return await RescueBodyAsync(result.SegmentId, orderedProviders, primary,
                    SegmentFetch.FetchStatus.Missing, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;
        var effectiveDepth = ResolveDepth(primary, depth);

        var index = 0;
        await using var enumerator = primary.DecodedArticlesPipelinedAsync(segmentIds, effectiveDepth, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (index < segmentIds.Count)
        {
            var moved = await TryMoveNextPipelinedAsync(enumerator).ConfigureAwait(false);
            if (!moved.Succeeded)
            {
                Log.Debug(moved.Error, "Pipelined ARTICLE failed on provider {Provider}; rescuing remaining segment(s).",
                    primary.Host);
                var reason = moved.Error is null ? SegmentFetch.FetchStatus.Other : ClassifyException(moved.Error);
                await foreach (var rescued in RescueArticlesAsync(segmentIds, index, orderedProviders, primary, reason,
                                       cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return rescued;
                yield break;
            }
            if (!moved.HasValue)
            {
                await foreach (var rescued in RescueArticlesAsync(segmentIds, index, orderedProviders, primary,
                                       SegmentFetch.FetchStatus.Other, cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                    yield return rescued;
                yield break;
            }

            var result = moved.Value!;
            index++;
            if (result.Found)
            {
                usageTracker.RecordSuccess(primary.ProviderKey);
                yield return WrapPipelinedArticle(result, primary.ProviderKey);
            }
            else
            {
                yield return await RescueArticleAsync(result.SegmentId, orderedProviders, primary,
                    SegmentFetch.FetchStatus.Missing, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<PipelinedMove<T>> TryMoveNextPipelinedAsync<T>(IAsyncEnumerator<T> enumerator)
    {
        try
        {
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                return new PipelinedMove<T>(Succeeded: true, HasValue: false, Value: default, Error: null);
            return new PipelinedMove<T>(Succeeded: true, HasValue: true, Value: enumerator.Current, Error: null);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            return new PipelinedMove<T>(Succeeded: false, HasValue: false, Value: default, Error: e);
        }
    }

    private readonly record struct PipelinedMove<T>(bool Succeeded, bool HasValue, T? Value, Exception? Error);

    private async IAsyncEnumerable<PipelinedStatResult> RescueStatsAsync(
        IReadOnlyList<string> segmentIds,
        int startIndex,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        MultiConnectionNntpClient primary,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = startIndex; i < segmentIds.Count; i++)
            yield return await RescueStatAsync(segmentIds[i], orderedProviders, primary, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<PipelinedStatResult> RescueStatAsync(
        string segmentId,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        MultiConnectionNntpClient primary,
        CancellationToken cancellationToken)
    {
        var retries = 1;
        foreach (var provider in orderedProviders)
        {
            if (ReferenceEquals(provider, primary)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await provider.StatAsync(segmentId, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                if (response.ResponseType == UsenetResponseType.ArticleExists)
                {
                    RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, retries);
                    return new PipelinedStatResult { SegmentId = segmentId, Exists = true };
                }

                RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, retries);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                var reason = e.TryGetCausingException(out UsenetArticleNotFoundException _)
                    ? SegmentFetch.FetchStatus.Missing
                    : ClassifyException(e);
                RecordFetch(provider.ProviderKey, reason, stopwatch.ElapsedMilliseconds, retries);
            }
            retries++;
        }

        return new PipelinedStatResult { SegmentId = segmentId, Exists = false };
    }

    private async IAsyncEnumerable<PipelinedBodyResult> RescueBodiesAsync(
        IReadOnlyList<string> segmentIds,
        int startIndex,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        MultiConnectionNntpClient primary,
        SegmentFetch.FetchStatus primaryReason,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = startIndex; i < segmentIds.Count; i++)
            yield return await RescueBodyAsync(segmentIds[i], orderedProviders, primary, primaryReason, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<PipelinedBodyResult> RescueBodyAsync(
        string segmentId,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        MultiConnectionNntpClient primary,
        SegmentFetch.FetchStatus primaryReason,
        CancellationToken cancellationToken)
    {
        var retries = 1;
        var priorMisses = new List<(string Host, SegmentFetch.FetchStatus Reason)> { (primary.ProviderKey, primaryReason) };
        foreach (var provider in orderedProviders)
        {
            if (ReferenceEquals(provider, primary)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await provider.DecodedBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                if (response.ResponseType == UsenetResponseType.ArticleRetrievedBodyFollows)
                {
                    usageTracker.RecordSuccess(provider.ProviderKey);
                    usageTracker.RecordFailoverSave();
                    RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, retries);
                    RecordFailoverMisses(priorMisses, provider.ProviderKey);
                    return WrapPipelinedBody(
                        new PipelinedBodyResult { SegmentId = segmentId, Found = true, Stream = response.Stream },
                        provider.ProviderKey);
                }

                RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, retries);
                priorMisses.Add((provider.ProviderKey, SegmentFetch.FetchStatus.Missing));
            }
            catch (UsenetArticleNotFoundException)
            {
                stopwatch.Stop();
                RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, retries);
                priorMisses.Add((provider.ProviderKey, SegmentFetch.FetchStatus.Missing));
            }
            catch (Exception e) when (!e.IsCancellationException()
                                      && e.TryGetCausingException(out UsenetArticleNotFoundException _))
            {
                stopwatch.Stop();
                RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, retries);
                priorMisses.Add((provider.ProviderKey, SegmentFetch.FetchStatus.Missing));
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.ProviderKey, reason, stopwatch.ElapsedMilliseconds, retries);
                priorMisses.Add((provider.ProviderKey, reason));
            }
            retries++;
        }

        return new PipelinedBodyResult { SegmentId = segmentId, Found = false, Stream = null };
    }

    private async IAsyncEnumerable<PipelinedArticleResult> RescueArticlesAsync(
        IReadOnlyList<string> segmentIds,
        int startIndex,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        MultiConnectionNntpClient primary,
        SegmentFetch.FetchStatus primaryReason,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = startIndex; i < segmentIds.Count; i++)
            yield return await RescueArticleAsync(segmentIds[i], orderedProviders, primary, primaryReason, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<PipelinedArticleResult> RescueArticleAsync(
        string segmentId,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        MultiConnectionNntpClient primary,
        SegmentFetch.FetchStatus primaryReason,
        CancellationToken cancellationToken)
    {
        var retries = 1;
        var priorMisses = new List<(string Host, SegmentFetch.FetchStatus Reason)> { (primary.ProviderKey, primaryReason) };
        foreach (var provider in orderedProviders)
        {
            if (ReferenceEquals(provider, primary)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var response = await provider.DecodedArticleAsync(segmentId, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                if (response.ResponseType == UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
                {
                    usageTracker.RecordSuccess(provider.ProviderKey);
                    usageTracker.RecordFailoverSave();
                    RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, retries);
                    RecordFailoverMisses(priorMisses, provider.ProviderKey);
                    return WrapPipelinedArticle(
                        new PipelinedArticleResult
                        {
                            SegmentId = segmentId,
                            Found = true,
                            Stream = response.Stream,
                            ArticleHeaders = response.ArticleHeaders,
                        },
                        provider.ProviderKey);
                }

                RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, retries);
                priorMisses.Add((provider.ProviderKey, SegmentFetch.FetchStatus.Missing));
            }
            catch (UsenetArticleNotFoundException)
            {
                stopwatch.Stop();
                RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, retries);
                priorMisses.Add((provider.ProviderKey, SegmentFetch.FetchStatus.Missing));
            }
            catch (Exception e) when (!e.IsCancellationException()
                                      && e.TryGetCausingException(out UsenetArticleNotFoundException _))
            {
                stopwatch.Stop();
                RecordFetch(provider.ProviderKey, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, retries);
                priorMisses.Add((provider.ProviderKey, SegmentFetch.FetchStatus.Missing));
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.ProviderKey, reason, stopwatch.ElapsedMilliseconds, retries);
                priorMisses.Add((provider.ProviderKey, reason));
            }
            retries++;
        }

        return new PipelinedArticleResult { SegmentId = segmentId, Found = false };
    }

    private PipelinedBodyResult WrapPipelinedBody(PipelinedBodyResult result, string host)
    {
        if (bytesTracker == null || result.Stream == null) return result;
        return result with { Stream = new CountingYencStream(result.Stream, bytesTracker, host) };
    }

    private PipelinedArticleResult WrapPipelinedArticle(PipelinedArticleResult result, string host)
    {
        if (bytesTracker == null || result.Stream == null) return result;
        return result with { Stream = new CountingYencStream(result.Stream, bytesTracker, host) };
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
