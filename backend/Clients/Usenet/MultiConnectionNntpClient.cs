using System.Diagnostics.CodeAnalysis;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for delegating NNTP commands to a connection pool.
///   * The connection pool enforces a maximum number of allowed connections
///   * When a connection is available, the NNTP command executes immediately
///   * When a connection is not available, the NNTP command waits until a connection becomes available.
///   * When multiple commands are awaiting a connection,
///     then BODY/ARTICLE commands have higher priority than STAT/HEAD/DATE commands.
/// </summary>
/// <param name="connectionPool"></param>
/// <param name="type"></param>
/// <param name="circuitBreaker"></param>
[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
public class MultiConnectionNntpClient(
    ConnectionPool<INntpClient> connectionPool,
    ProviderType type,
    ProviderCircuitBreaker circuitBreaker,
    string host,
    long? byteLimit,
    long bytesUsedOffset
) : NntpClient
{
    public ProviderType ProviderType { get; } = type;
    public string Host { get; } = host;
    // null or non-positive = uncapped. Routing reads these to decide whether
    // this provider should be skipped when it has exhausted its block.
    public long? ByteLimit { get; } = byteLimit;
    public long BytesUsedOffset { get; } = bytesUsedOffset;
    public bool IsTripped => circuitBreaker.IsTripped;
    public int LiveConnections => connectionPool.LiveConnections;
    public int IdleConnections => connectionPool.IdleConnections;
    public int ActiveConnections => connectionPool.ActiveConnections;
    public int AvailableConnections => connectionPool.AvailableConnections;

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "STAT",
            SemaphorePriority.Low,
            (connection, _, effectiveCt) => connection.StatAsync(segmentId, effectiveCt),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "HEAD",
            SemaphorePriority.Low,
            (connection, _, effectiveCt) => connection.HeadAsync(segmentId, effectiveCt),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "BODY",
            SemaphorePriority.High,
            (connection, onDone, effectiveCt) => connection.DecodedBodyAsync(segmentId, onDone, effectiveCt),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            SemaphorePriority.High,
            (connection, onDone, effectiveCt) => connection.DecodedArticleAsync(segmentId, onDone, effectiveCt),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken ct)
    {
        return RunWithConnection(
            "DATE",
            SemaphorePriority.Low,
            (connection, _, effectiveCt) => connection.DateAsync(effectiveCt),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "BODY",
            SemaphorePriority.High,
            (connection, onDone, effectiveCt) => connection.DecodedBodyAsync(segmentId, onDone, effectiveCt),
            onConnectionReadyAgain,
            ct
        );
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            SemaphorePriority.High,
            (connection, onDone, effectiveCt) => connection.DecodedArticleAsync(segmentId, onDone, effectiveCt),
            onConnectionReadyAgain,
            ct
        );
    }

    // Cap on how long we'll wait for a BODY/ARTICLE provider to send its
    // initial response line. Healthy providers reply in well under a second;
    // anything past a few seconds is almost certainly a mid-command stall.
    // Body bytes that follow the initial response stream under the caller's
    // original cancellation token — this deadline only governs first-response
    // latency.
    private static readonly TimeSpan FirstResponseDeadline = TimeSpan.FromSeconds(5);

    private async Task<T> RunWithConnection<T>
    (
        string name,
        SemaphorePriority priority,
        Func<INntpClient, Action<ArticleBodyResult>, CancellationToken, Task<T>> command,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct,
        int retryCount = 1
    ) where T : UsenetResponse
    {
        while (retryCount >= 0)
        {
            ConnectionLock<INntpClient>? connectionLock = null;
            CancellationTokenSource? startDeadlineCts = null;
            try
            {
                connectionLock = await connectionPool.GetConnectionLockAsync(priority, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e.IsCancellationException())
            {
                LogException(() => connectionLock?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e)
            {
                circuitBreaker.RecordFailure();
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                if (retryCount > 0)
                {
                    Log.Debug(e, "Error getting connection-lock. Retrying with a new connection.");
                    retryCount--;
                    continue;
                }

                Log.Warning(e, "Error getting connection-lock.");
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }

            // Wrap BODY/ARTICLE in a tight first-response deadline. The linked
            // CTS preserves outer cancellation; only the deadline component is
            // disarmed once `command` returns (see CancelAfter(Infinite) below).
            var effectiveCt = ct;
            if (name is "BODY" or "ARTICLE")
            {
                startDeadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startDeadlineCts.CancelAfter(FirstResponseDeadline);
                effectiveCt = startDeadlineCts.Token;
            }

            T? result;
            try
            {
                result = await command(connectionLock.Connection, OnConnectionReadyAgain, effectiveCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (startDeadlineCts is not null
                                                      && startDeadlineCts.IsCancellationRequested
                                                      && !ct.IsCancellationRequested)
            {
                // Start-deadline elapsed before the provider produced an initial
                // response. Trip the breaker hard so subsequent requests skip
                // this provider — repeating the same N×deadline tax on every
                // user click is exactly what we're trying to avoid.
                Log.Warning(
                    "Start-deadline ({Deadline}s) expired on {Name} via {Host} — tripping provider and rotating",
                    FirstResponseDeadline.TotalSeconds, name, host);
                circuitBreaker.RecordHardFailure("BODY/ARTICLE start-deadline");
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                LogException(() => startDeadlineCts?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw new TimeoutException(
                    $"No initial response from {name} via {host} within {FirstResponseDeadline.TotalSeconds:0}s.");
            }
            catch (Exception e) when (e.IsCancellationException())
            {
                // cancelled BODY/ARTICLE leaves unread response bytes on the socket;
                // pooling would poison the next request with a misparsed 'not found'.
                if (name is "BODY" or "ARTICLE")
                    LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                LogException(() => startDeadlineCts?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException _))
            {
                // a 'not found' on BODY/ARTICLE may be misparsed leftover bytes from
                // an earlier poisoned connection; destroy and retry once on a fresh socket.
                if (name is "BODY" or "ARTICLE")
                {
                    LogException(() => connectionLock?.Replace());
                    LogException(() => connectionLock?.Dispose());
                    LogException(() => startDeadlineCts?.Dispose());
                    if (retryCount > 0)
                    {
                        Log.Debug(e, $"Got 'article not found' on nntp {name}. Retrying once with a fresh connection.");
                        retryCount--;
                        continue;
                    }
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                    throw;
                }
                LogException(() => connectionLock?.Dispose());
                LogException(() => startDeadlineCts?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e) when (name is "BODY" or "ARTICLE" && e.TryGetCausingException(out TimeoutException _))
            {
                // Read-timeout on BODY/ARTICLE means the provider stopped responding
                // mid-command. A fresh socket to the same provider is unlikely to fare
                // any better, and burning another timeout retrying here just doubles
                // the wait before MultiProviderNntpClient can fall over to the next
                // provider. Trip the breaker immediately (one stall is enough — we
                // don't want the next user click to pay the same tax), replace the
                // socket (read may have left partial bytes on the wire), and
                // propagate so the outer provider loop moves on.
                Log.Warning(e, "Read timeout on {Name} via {Host} — tripping provider and rotating", name, host);
                circuitBreaker.RecordHardFailure("BODY/ARTICLE read timeout");
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                LogException(() => startDeadlineCts?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }
            catch (Exception e)
            {
                circuitBreaker.RecordFailure();
                LogException(() => connectionLock?.Replace());
                LogException(() => connectionLock?.Dispose());
                LogException(() => startDeadlineCts?.Dispose());
                if (retryCount > 0)
                {
                    Log.Debug(e, $"Error executing nntp {name} command. Retrying with a new connection.");
                    retryCount--;
                    continue;
                }

                Log.Warning(e, $"Error executing nntp {name} command.");
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                throw;
            }

            // Initial response arrived — disarm the start-deadline so subsequent
            // body bytes stream under the caller's original cancellation token.
            startDeadlineCts?.CancelAfter(Timeout.InfiniteTimeSpan);

            circuitBreaker.RecordSuccess();

            // stat, head, and date
            if (name is "STAT" or "HEAD" or "DATE")
            {
                LogException(() => connectionLock?.Dispose());
                LogException(() => startDeadlineCts?.Dispose());
            }

            // body and article
            else if ((result?.Success ?? false) == false)
            {
                LogException(() => connectionLock?.Dispose());
                LogException(() => startDeadlineCts?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
            }

            return result!;

            void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
            {
                if (articleBodyResult != ArticleBodyResult.Retrieved) return;

                LogException(() => connectionLock?.Dispose());
                LogException(() => startDeadlineCts?.Dispose());
                LogException(() => onConnectionReadyAgain?.Invoke(articleBodyResult));
            }
        }

        Log.Error("Unreachable code reached");
        throw new InvalidOperationException("Unreachable code ");
    }

    private static void LogException(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Unhandled exception");
        }
    }

    public override void Dispose()
    {
        connectionPool.Dispose();
        GC.SuppressFinalize(this);
    }
}