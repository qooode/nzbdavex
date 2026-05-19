using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Streams;

public class DavMultipartFileStream : Stream
{
    private readonly DavMultipartFile _mpf;
    private readonly INntpClient _usenetClient;
    private readonly int _articleBufferSize;
    private readonly LazyRarResolver? _resolver;
    private readonly long _length;

    private long _position;
    private CombinedStream? _innerStream;
    private bool _disposed;

    public DavMultipartFileStream(
        DavMultipartFile mpf,
        INntpClient usenetClient,
        int articleBufferSize,
        LazyRarResolver? resolver)
    {
        _mpf = mpf;
        _usenetClient = usenetClient;
        _articleBufferSize = articleBufferSize;
        _resolver = resolver;
        _length = ComputeLength(mpf.Metadata);
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _innerStream ??= await GetFileStreamAsync(_position, cancellationToken).ConfigureAwait(false);
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset) return _position;
        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new InvalidOperationException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new InvalidOperationException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    // Walks resolved FileParts + pending estimates so HEAD/Length-aware
    // clients see the stable inner-file size from the moment of mount. The
    // estimates are adjusted at import time so this matches the real
    // uncompressed size byte-exact.
    // Old MemoryPack blobs predate the lazy fields, so PendingParts can be
    // null after deserialization despite the property initializer. Guard
    // every iteration with ?? [] to stay safe.
    private static long ComputeLength(DavMultipartFile.Meta meta)
    {
        var sum = 0L;
        foreach (var p in meta.FileParts ?? []) sum += p.FilePartByteRange.Count;
        foreach (var p in meta.PendingParts ?? []) sum += p.EstimatedDataSize;
        return sum;
    }

    private (int filePartIndex, long filePartOffset) SeekFilePart(
        DavMultipartFile.Meta meta,
        long byteOffset)
    {
        long offset = 0;
        var fileParts = meta.FileParts ?? [];
        for (var i = 0; i < fileParts.Length; i++)
        {
            var filePart = fileParts[i];
            var nextOffset = offset + filePart.FilePartByteRange.Count;
            if (byteOffset < nextOffset)
                return (i, offset);
            offset = nextOffset;
        }

        throw new SeekPositionNotFoundException($"Corrupt file. Cannot seek to byte position {byteOffset}.");
    }

    private async Task<CombinedStream> GetFileStreamAsync(long rangeStart, CancellationToken ct)
    {
        var meta = await EnsureCoveringAsync(rangeStart, ct).ConfigureAwait(false);

        if (rangeStart == 0)
            return new CombinedStream(EnumerateFromPart(0, 0, ct));

        var (filePartIndex, filePartOffset) = SeekFilePart(meta, rangeStart);
        return new CombinedStream(EnumerateFromPart(filePartIndex, rangeStart - filePartOffset, ct));
    }

    // Resolve trailing volumes up to (and including) the one that contains
    // `byteOffset` so SeekFilePart can map the offset to an exact slot.
    // No-op for non-lazy archives.
    private async Task<DavMultipartFile.Meta> EnsureCoveringAsync(long byteOffset, CancellationToken ct)
    {
        if (_resolver is null || !_mpf.Metadata.IsLazy) return _mpf.Metadata;
        return await _resolver.EnsureResolvedThroughAsync(_mpf, byteOffset, ct).ConfigureAwait(false);
    }

    // Lazy iterator over the file's volume sequence. Each yielded Task opens
    // one volume's segment range. When we run out of resolved FileParts but
    // PendingParts remain, the next yield triggers lazy resolution before
    // opening — so the player keeps streaming across volume boundaries
    // without having paid for them at mount time.
    private IEnumerable<Task<Stream>> EnumerateFromPart(int firstFilePartIndex, long firstOffset, CancellationToken ct)
    {
        var i = firstFilePartIndex;
        while (true)
        {
            var meta = _mpf.Metadata;
            var fileParts = meta.FileParts ?? [];
            if (i < fileParts.Length)
            {
                var part = fileParts[i];
                var extraOffset = (i == firstFilePartIndex) ? firstOffset : 0;
                yield return Task.FromResult(OpenPart(part, extraOffset));
                i++;
                continue;
            }

            if (_resolver != null && meta.IsLazy && (meta.PendingParts?.Length ?? 0) > 0)
            {
                yield return ResolveAndOpenAsync(i, ct);
                i++;
                continue;
            }

            yield break;
        }
    }

    private Stream OpenPart(DavMultipartFile.FilePart part, long extraOffset)
    {
        var stream = _usenetClient.GetFileStream(part.SegmentIds, part.SegmentIdByteRange.Count, _articleBufferSize);
        stream.Seek(part.FilePartByteRange.StartInclusive + extraOffset, SeekOrigin.Begin);
        return stream.LimitLength(part.FilePartByteRange.Count - extraOffset);
    }

    private async Task<Stream> ResolveAndOpenAsync(int targetIndex, CancellationToken ct)
    {
        await _resolver!.ResolveNextAsync(_mpf, ct).ConfigureAwait(false);
        var meta = _mpf.Metadata;
        if (targetIndex >= meta.FileParts.Length)
        {
            // Resolver should always grow FileParts when there were pending
            // parts. If we land here, treat as EOF — CombinedStream advances
            // to the next yield (which will hit yield break).
            return new MemoryStream(Array.Empty<byte>(), writable: false);
        }
        return OpenPart(meta.FileParts[targetIndex], 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _innerStream?.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
