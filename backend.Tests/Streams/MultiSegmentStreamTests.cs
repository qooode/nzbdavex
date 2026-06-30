using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;
using Xunit;

namespace NzbWebDAV.Tests.Streams;

public class MultiSegmentStreamTests
{
    [Fact]
    public async Task UnbufferedStream_ThrowsWhenFirstSegmentMissingAndFailFastEnabled()
    {
        var client = new FakeNntpClient();
        client.Missing.Add("seg-1");
        await using var stream = MultiSegmentStream.Create(
            new[] { "seg-1", "seg-2" },
            client,
            articleBufferSize: 0,
            expectedSegmentSize: 4,
            failFastOnFirstSegment: true,
            CancellationToken.None);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(async () =>
            await stream.CopyToAsync(new MemoryStream()));
    }

    [Fact]
    public async Task UnbufferedStream_ZeroFillsFirstSegmentWhenFailFastDisabled()
    {
        var client = new FakeNntpClient();
        client.Missing.Add("seg-1");
        client.Bodies["seg-2"] = "tail";
        await using var stream = MultiSegmentStream.Create(
            new[] { "seg-1", "seg-2" },
            client,
            articleBufferSize: 0,
            expectedSegmentSize: 4,
            failFastOnFirstSegment: false,
            CancellationToken.None);

        var bytes = await ReadAllAsync(stream);

        Assert.Equal([0, 0, 0, 0, (byte)'t', (byte)'a', (byte)'i', (byte)'l'], bytes);
    }

    [Fact]
    public async Task UnbufferedStream_ZeroFillsLaterMissingSegmentsWhenFailFastEnabled()
    {
        var client = new FakeNntpClient();
        client.Bodies["seg-1"] = "head";
        client.Missing.Add("seg-2");
        await using var stream = MultiSegmentStream.Create(
            new[] { "seg-1", "seg-2" },
            client,
            articleBufferSize: 0,
            expectedSegmentSize: 4,
            failFastOnFirstSegment: true,
            CancellationToken.None);

        var bytes = await ReadAllAsync(stream);

        Assert.Equal([(byte)'h', (byte)'e', (byte)'a', (byte)'d', 0, 0, 0, 0], bytes);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    private sealed class FakeNntpClient : NntpClient
    {
        public Dictionary<string, string> Bodies { get; } = new();
        public HashSet<string> Missing { get; } = [];

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            var key = segmentId.ToString();
            if (Missing.Contains(key))
                return Task.FromException<UsenetDecodedBodyResponse>(new UsenetArticleNotFoundException(key));

            return Bodies.TryGetValue(key, out var payload)
                ? Task.FromResult(new UsenetDecodedBodyResponse
                {
                    SegmentId = key,
                    Stream = Decoded(payload),
                    ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                    ResponseMessage = "body follows",
                })
                : Task.FromException<UsenetDecodedBodyResponse>(new UsenetArticleNotFoundException(key));
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
            => DecodedBodyAsync(segmentId, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override void Dispose()
        {
        }

        private static YencStream Decoded(string payload)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(payload);
            return new CachedYencStream(
                new UsenetYencHeader
                {
                    FileName = "test.bin",
                    FileSize = bytes.Length,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    PartSize = bytes.Length,
                    PartOffset = 0,
                },
                new MemoryStream(bytes, writable: false));
        }
    }
}
