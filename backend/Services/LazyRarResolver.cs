using System.Collections.Concurrent;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Services;

// Resolves PendingParts of a lazy multipart RAR archive on demand.
// First reader to need part N pays the cost (~1 segment fetch + parse);
// subsequent readers reuse the resolved FilePart. The whole resolved
// archive is written back to the blob-store so restarts also reuse it.
public class LazyRarResolver(UsenetStreamingClient usenetClient)
{
    // Coalesces concurrent resolution requests for the same (file, slot).
    // Key is (DavMultipartFile.Id, absolute index in the inner file's part
    // sequence). Entries are dropped once resolution completes; future
    // readers see the resolved FilePart in FileParts and never re-key here.
    private readonly ConcurrentDictionary<(Guid, int), Task<DavMultipartFile.FilePart>> _inFlight = new();

    // Resolve PendingParts until either the cumulative inner-file byte
    // coverage strictly exceeds targetByteOffset or there's nothing left to
    // resolve. Returns the latest Meta — callers should discard their old
    // reference and read PartArray off this return value.
    public async Task<DavMultipartFile.Meta> EnsureResolvedThroughAsync(
        DavMultipartFile mpf,
        long targetByteOffset,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        if (!meta.IsLazy) return meta;

        // Old MemoryPack blobs may deserialize PendingParts as null despite
        // the property initializer; treat that as "nothing to resolve".
        while ((meta.PendingParts?.Length ?? 0) > 0)
        {
            var resolvedBytes = SumResolvedBytes(meta);
            if (resolvedBytes > targetByteOffset) break;
            meta = await ResolveNextAsync(mpf, ct).ConfigureAwait(false);
        }

        return meta;
    }

    // Resolve PendingParts[0] (if any) and merge it into FileParts.
    public async Task<DavMultipartFile.Meta> ResolveNextAsync(
        DavMultipartFile mpf,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        if (!meta.IsLazy || (meta.PendingParts?.Length ?? 0) == 0) return meta;

        var resolved = await GetOrStartResolutionAsync(mpf, ct).ConfigureAwait(false);
        return CommitResolved(mpf, resolved);
    }

    // Coalesce by absolute part index. Concurrent callers crossing the same
    // volume boundary share one resolution; later callers (after commit)
    // get a different absolute index since FileParts has grown.
    private Task<DavMultipartFile.FilePart> GetOrStartResolutionAsync(
        DavMultipartFile mpf,
        CancellationToken callerCt)
    {
        var key = (mpf.Id, mpf.Metadata.FileParts?.Length ?? 0);

        // Use CancellationToken.None for the shared work so one caller
        // bailing out doesn't kill resolution for others waiting on it.
        var shared = _inFlight.GetOrAdd(key, k =>
        {
            var task = DoResolveAsync(mpf, CancellationToken.None);
            // Drop the entry once done so the dictionary doesn't grow
            // unbounded; the result is captured in FileParts anyway.
            _ = task.ContinueWith(t => _inFlight.TryRemove(k, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        });

        return shared.WaitAsync(callerCt);
    }

    private async Task<DavMultipartFile.FilePart> DoResolveAsync(
        DavMultipartFile mpf,
        CancellationToken ct)
    {
        var meta = mpf.Metadata;
        var pendingParts = meta.PendingParts ?? [];
        if (pendingParts.Length == 0)
            throw new InvalidOperationException("Lazy RAR resolution called with no pending parts.");
        var pending = pendingParts[0];
        var pathInArchive = meta.PathInArchive
            ?? throw new InvalidOperationException("Lazy RAR meta missing PathInArchive.");

        await using var stream = usenetClient.GetFileStream(
            pending.SegmentIds, pending.SegmentIdByteRange.Count, articleBufferSize: 0);

        // Find-and-stop so SharpCompress never seeks past the matched header.
        // The seek would force NzbFileStream to fire InterpolationSearch
        // (~7 STAT calls), which is the main reason naïve full-walk
        // resolution stalls playback at every volume boundary.
        var match = await RarUtil.FindFirstFileHeaderAsync(
            stream,
            meta.ArchivePassword,
            h => h.GetFileName() == pathInArchive,
            ct).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                $"Lazy RAR resolution: continuation header for '{pathInArchive}' not found in trailing volume.");

        return new DavMultipartFile.FilePart
        {
            SegmentIds = pending.SegmentIds,
            SegmentIdByteRange = pending.SegmentIdByteRange,
            FilePartByteRange = LongRange.FromStartAndSize(
                match.GetDataStartPosition(),
                match.GetAdditionalDataSize()
            ),
        };
    }

    // Atomically appends `resolved` to FileParts and pops PendingParts[0].
    // Persists fire-and-forget — a failed write only costs us a re-resolve
    // after restart, never inconsistency.
    private DavMultipartFile.Meta CommitResolved(DavMultipartFile mpf, DavMultipartFile.FilePart resolved)
    {
        lock (mpf)
        {
            var meta = mpf.Metadata;
            var fileParts = meta.FileParts ?? [];
            var pendingParts = meta.PendingParts ?? [];

            // Another commit may have already moved this part across; bail
            // if the head of the pending queue no longer matches.
            if (pendingParts.Length == 0
                || !pendingParts[0].SegmentIds.SequenceEqual(resolved.SegmentIds))
            {
                return meta;
            }

            var newParts = new DavMultipartFile.FilePart[fileParts.Length + 1];
            Array.Copy(fileParts, newParts, fileParts.Length);
            newParts[^1] = resolved;

            var newPending = new DavMultipartFile.PendingPart[pendingParts.Length - 1];
            Array.Copy(pendingParts, 1, newPending, 0, newPending.Length);

            var newMeta = new DavMultipartFile.Meta
            {
                AesParams = meta.AesParams,
                FileParts = newParts,
                IsLazy = newPending.Length > 0,
                PathInArchive = meta.PathInArchive,
                ArchivePassword = meta.ArchivePassword,
                PendingParts = newPending,
            };

            mpf.Metadata = newMeta;
            _ = PersistAsync(mpf);
            return newMeta;
        }
    }

    private static long SumResolvedBytes(DavMultipartFile.Meta meta)
    {
        var sum = 0L;
        foreach (var p in meta.FileParts ?? []) sum += p.FilePartByteRange.Count;
        return sum;
    }

    private static async Task PersistAsync(DavMultipartFile mpf)
    {
        try
        {
            await BlobStore.WriteBlob(mpf.Id, mpf).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warning(e,
                "Failed to persist lazy-resolved RAR multipart {Id}; will re-resolve on next restart",
                mpf.Id);
        }
    }
}
