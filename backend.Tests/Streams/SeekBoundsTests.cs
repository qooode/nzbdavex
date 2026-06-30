using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using Xunit;

namespace NzbWebDAV.Tests.Streams;

public class SeekBoundsTests
{
    [Theory]
    [MemberData(nameof(CreateSeekableStreams))]
    public void Seek_AllowsStartMiddleEndAndSeekOriginEnd(Stream stream)
    {
        using (stream)
        {
            Assert.Equal(0, stream.Seek(0, SeekOrigin.Begin));
            Assert.Equal(50, stream.Seek(50, SeekOrigin.Begin));
            Assert.Equal(40, stream.Seek(-10, SeekOrigin.Current));
            Assert.Equal(100, stream.Seek(0, SeekOrigin.End));
            Assert.Equal(95, stream.Seek(-5, SeekOrigin.End));
            stream.Position = 25;
            Assert.Equal(25, stream.Position);
        }
    }

    [Theory]
    [MemberData(nameof(CreateSeekableStreams))]
    public void Seek_RejectsNegativeOrBeyondEndPositions(Stream stream)
    {
        using (stream)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(101, SeekOrigin.Begin));
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-101, SeekOrigin.End));
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(1, SeekOrigin.End));
            Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(0, (SeekOrigin)999));
        }
    }

    public static IEnumerable<object[]> CreateSeekableStreams()
    {
        yield return [new NzbFileStream([], 100, new ThrowingNntpClient(), articleBufferSize: 0)];
        yield return [new MultipartFileStream(
            new MultipartFile
            {
                FileParts =
                [
                    new MultipartFile.FilePart
                    {
                        NzbFile = new NzbFile { Subject = "test.bin" },
                        ByteRange = new LongRange(0, 100)
                    }
                ]
            },
            new ThrowingNntpClient())];
        yield return [new DavMultipartFileStream(
            new DavMultipartFile
            {
                Metadata = new DavMultipartFile.Meta
                {
                    FileParts =
                    [
                        new DavMultipartFile.FilePart
                        {
                            SegmentIds = [],
                            SegmentIdByteRange = new LongRange(0, 100),
                            FilePartByteRange = new LongRange(0, 100)
                        }
                    ]
                }
            },
            new ThrowingNntpClient(),
            articleBufferSize: 0,
            resolver: null)];
    }

    private sealed class ThrowingNntpClient : NntpClient
    {
        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
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
    }
}
