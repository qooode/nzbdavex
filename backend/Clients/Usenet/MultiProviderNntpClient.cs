using System.Diagnostics;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models.Metrics;
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
    ProviderBytesTracker? bytesTracker = null
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
        var orderedProviders = GetOrderedProviders();
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
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
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
                    usageTracker.RecordSuccess(provider.Host);
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, i);
                    result = WrapStreamForByteCounting(result, provider.Host);
                }
                else
                {
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                }

                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                RecordFetch(provider.Host, ClassifyException(e), stopwatch.ElapsedMilliseconds, i);
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

    private List<MultiConnectionNntpClient> GetOrderedProviders()
    {
        var enabled = providers
            .Where(x => x.ProviderType != ProviderType.Disabled)
            .Where(x => !IsOverLimit(x))
            .OrderBy(x => x.ProviderType)
            .ThenByDescending(x => x.AvailableConnections)
            .ToList();

        var healthy = enabled.Where(x => !x.IsTripped).ToList();

        // Always return at least one provider so cooldown probes can fire.
        // Note we intentionally do NOT relax the over-limit filter here:
        // exhausting a paid block must be a hard stop, not a soft preference.
        return healthy.Count > 0 ? healthy : enabled;
    }

    private bool IsOverLimit(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return false;
        var used = bytesTracker.GetLifetime(client.Host) + client.BytesUsedOffset;
        return used >= limit.Value;
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}