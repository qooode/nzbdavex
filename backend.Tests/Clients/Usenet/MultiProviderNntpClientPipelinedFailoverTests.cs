using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using UsenetSharp.Models;
using UsenetSharp.Streams;
using Xunit;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class MultiProviderNntpClientPipelinedFailoverTests
{
    [Fact]
    public async Task DecodedBodiesPipelinedAsync_RescuesPrimaryMissFromBackupProvider()
    {
        var primary = new FakeNntpClient
        {
            PipelinedBodies =
            [
                new PipelinedBodyResult { SegmentId = "seg-1", Found = false },
                BodyResult("seg-2", "primary")
            ],
        };
        var backup = new FakeNntpClient();
        backup.BodyResponses["seg-1"] = BodyResponse("seg-1", "backup");

        using var client = CreateClient(
            Provider("primary.example", primary),
            Provider("backup.example", backup));

        var results = await CollectAsync(client.DecodedBodiesPipelinedAsync(
            ["seg-1", "seg-2"], depth: 8, CancellationToken.None));

        Assert.Equal(["seg-1", "seg-2"], results.Select(r => r.SegmentId).ToArray());
        Assert.All(results, r => Assert.True(r.Found));
        Assert.Equal(["seg-1"], backup.BodyCalls);
        Assert.Empty(primary.BodyCalls);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_RescuesRemainingSegmentsWhenPrimaryPipelineThrows()
    {
        var primary = new FakeNntpClient
        {
            PipelinedBodies =
            [
                BodyResult("seg-1", "primary"),
            ],
            ThrowAfterPipelinedBodyResults = true,
        };
        var backup = new FakeNntpClient();
        backup.BodyResponses["seg-2"] = BodyResponse("seg-2", "backup");
        backup.BodyResponses["seg-3"] = BodyResponse("seg-3", "backup");

        using var client = CreateClient(
            Provider("primary.example", primary),
            Provider("backup.example", backup));

        var results = await CollectAsync(client.DecodedBodiesPipelinedAsync(
            ["seg-1", "seg-2", "seg-3"], depth: 8, CancellationToken.None));

        Assert.Equal(["seg-1", "seg-2", "seg-3"], results.Select(r => r.SegmentId).ToArray());
        Assert.All(results, r => Assert.True(r.Found));
        Assert.Equal(["seg-2", "seg-3"], backup.BodyCalls);
        Assert.Empty(primary.BodyCalls);
    }

    [Fact]
    public async Task StatsPipelinedAsync_RescuesPrimaryMissFromBackupProvider()
    {
        var primary = new FakeNntpClient
        {
            PipelinedStats =
            [
                new PipelinedStatResult { SegmentId = "seg-1", Exists = false },
            ],
        };
        var backup = new FakeNntpClient();
        backup.StatResponses["seg-1"] = StatResponse(exists: true);

        using var client = CreateClient(
            Provider("primary.example", primary),
            Provider("backup.example", backup));

        var results = await CollectAsync(client.StatsPipelinedAsync(
            ["seg-1"], depth: 8, CancellationToken.None));

        var result = Assert.Single(results);
        Assert.Equal("seg-1", result.SegmentId);
        Assert.True(result.Exists);
        Assert.Equal(["seg-1"], backup.StatCalls);
        Assert.Empty(primary.StatCalls);
    }

    [Fact]
    public async Task DecodedArticlesPipelinedAsync_RescuesPrimaryMissFromBackupProvider()
    {
        var primary = new FakeNntpClient
        {
            PipelinedArticles =
            [
                new PipelinedArticleResult { SegmentId = "seg-1", Found = false },
            ],
        };
        var backup = new FakeNntpClient();
        backup.ArticleResponses["seg-1"] = ArticleResponse("seg-1", "backup");

        using var client = CreateClient(
            Provider("primary.example", primary),
            Provider("backup.example", backup));

        var results = await CollectAsync(client.DecodedArticlesPipelinedAsync(
            ["seg-1"], depth: 8, CancellationToken.None));

        var result = Assert.Single(results);
        Assert.Equal("seg-1", result.SegmentId);
        Assert.True(result.Found);
        Assert.NotNull(result.ArticleHeaders);
        Assert.Equal(["seg-1"], backup.ArticleCalls);
        Assert.Empty(primary.ArticleCalls);
    }

    [Fact]
    public async Task DecodedBodiesPipelinedAsync_SkipsOnlyTheOverLimitDuplicateHostProvider()
    {
        var overLimit = new FakeNntpClient
        {
            PipelinedBodies =
            [
                BodyResult("seg-1", "over-limit"),
            ],
        };
        var available = new FakeNntpClient
        {
            PipelinedBodies =
            [
                BodyResult("seg-1", "available"),
            ],
        };
        var bytesTracker = new ProviderBytesTracker();
        var providerAKey = UsenetProviderConfig.ConnectionDetails.BuildProviderKey("news.example", 563, "user-a");
        var providerBKey = UsenetProviderConfig.ConnectionDetails.BuildProviderKey("news.example", 563, "user-b");
        bytesTracker.SetLifetime(providerAKey, 100);

        using var client = CreateClient(
            bytesTracker,
            Provider("news.example", overLimit, user: "user-a", byteLimit: 100),
            Provider("news.example", available, user: "user-b", byteLimit: 100));

        var results = await CollectAsync(client.DecodedBodiesPipelinedAsync(
            ["seg-1"], depth: 8, CancellationToken.None));

        var result = Assert.Single(results);
        Assert.True(result.Found);
        Assert.Empty(overLimit.PipelinedBodiesServed);
        Assert.Equal(["seg-1"], available.PipelinedBodiesServed);
        Assert.Equal(0, bytesTracker.GetLifetime(providerBKey));
    }

    private static MultiProviderNntpClient CreateClient(params MultiConnectionNntpClient[] providers)
        => new(providers.ToList(), new ProviderUsageTracker());

    private static MultiProviderNntpClient CreateClient(
        ProviderBytesTracker bytesTracker,
        params MultiConnectionNntpClient[] providers)
        => new(providers.ToList(), new ProviderUsageTracker(), bytesTracker: bytesTracker);

    private static MultiConnectionNntpClient Provider(
        string host,
        FakeNntpClient fake,
        string user = "user",
        long? byteLimit = null)
        => new(
            new ConnectionPool<INntpClient>(
                maxConnections: 1,
                connectionFactory: _ => ValueTask.FromResult<INntpClient>(fake),
                idleTimeout: TimeSpan.FromMinutes(5)),
            ProviderType.Pooled,
            new ProviderCircuitBreaker(host),
            host,
            byteLimit,
            bytesUsedOffset: 0,
            priority: 0,
            pipeliningDepth: null,
            providerKey: UsenetProviderConfig.ConnectionDetails.BuildProviderKey(host, 563, user));

    private static PipelinedBodyResult BodyResult(string segmentId, string payload)
        => new()
        {
            SegmentId = segmentId,
            Found = true,
            Stream = Yenc(payload),
        };

    private static UsenetDecodedBodyResponse BodyResponse(string segmentId, string payload)
        => new()
        {
            SegmentId = segmentId,
            Stream = Yenc(payload),
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
            ResponseMessage = "body follows",
        };

    private static UsenetDecodedArticleResponse ArticleResponse(string segmentId, string payload)
        => new()
        {
            SegmentId = segmentId,
            Stream = Yenc(payload),
            ArticleHeaders = new UsenetArticleHeader { Headers = new Dictionary<string, string>() },
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            ResponseMessage = "article follows",
        };

    private static UsenetStatResponse StatResponse(bool exists)
        => new()
        {
            ArticleExists = exists,
            ResponseCode = exists
                ? (int)UsenetResponseType.ArticleExists
                : (int)UsenetResponseType.NoArticleWithThatMessageId,
            ResponseMessage = exists ? "article exists" : "no article",
        };

    private static YencStream Yenc(string payload)
    {
        var text = $"=ybegin line=128 size={payload.Length} name=test.bin\r\n{payload}\r\n=yend size={payload.Length}\r\n";
        return new YencStream(new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text)));
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (var item in source)
            results.Add(item);
        return results;
    }

    private sealed class FakeNntpClient : NntpClient
    {
        public List<PipelinedBodyResult> PipelinedBodies { get; init; } = [];
        public List<string> PipelinedBodiesServed { get; } = [];
        public List<PipelinedArticleResult> PipelinedArticles { get; init; } = [];
        public List<PipelinedStatResult> PipelinedStats { get; init; } = [];
        public bool ThrowAfterPipelinedBodyResults { get; init; }
        public Dictionary<string, UsenetDecodedBodyResponse> BodyResponses { get; } = new();
        public Dictionary<string, UsenetDecodedArticleResponse> ArticleResponses { get; } = new();
        public Dictionary<string, UsenetStatResponse> StatResponses { get; } = new();
        public List<string> BodyCalls { get; } = [];
        public List<string> ArticleCalls { get; } = [];
        public List<string> StatCalls { get; } = [];

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user,
            string pass,
            CancellationToken cancellationToken)
            => Task.FromResult<UsenetResponse>(new UsenetResponse
            {
                ResponseCode = (int)UsenetResponseType.AuthenticationAccepted,
                ResponseMessage = "accepted",
            });

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => Task.FromException<UsenetHeadResponse>(new NotSupportedException());

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => Task.FromException<UsenetDateResponse>(new NotSupportedException());

        public override void Dispose()
        {
        }

        public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var result in PipelinedBodies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PipelinedBodiesServed.Add(result.SegmentId);
                yield return result;
                await Task.Yield();
            }

            if (ThrowAfterPipelinedBodyResults)
                throw new IOException("primary pipeline failed");
        }

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var result in PipelinedStats)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return result;
                await Task.Yield();
            }
        }

        public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var result in PipelinedArticles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return result;
                await Task.Yield();
            }
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            var key = segmentId.ToString();
            BodyCalls.Add(key);
            return BodyResponses.TryGetValue(key, out var response)
                ? Task.FromResult(response)
                : Task.FromException<UsenetDecodedBodyResponse>(new UsenetArticleNotFoundException(key));
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var result = DecodedBodyAsync(segmentId, cancellationToken);
            if (result.IsCompletedSuccessfully) onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return result;
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            var key = segmentId.ToString();
            ArticleCalls.Add(key);
            return ArticleResponses.TryGetValue(key, out var response)
                ? Task.FromResult(response)
                : Task.FromException<UsenetDecodedArticleResponse>(new UsenetArticleNotFoundException(key));
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var result = DecodedArticleAsync(segmentId, cancellationToken);
            if (result.IsCompletedSuccessfully) onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return result;
        }

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            var key = segmentId.ToString();
            StatCalls.Add(key);
            return StatResponses.TryGetValue(key, out var response)
                ? Task.FromResult(response)
                : Task.FromResult(StatResponse(exists: false));
        }
    }
}
