using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Services;

/// <summary>
/// In-memory list of currently active WebDAV stream sessions, used to surface
/// "what's being read right now and from which backbone" in the UI. No persistence:
/// entries live only while a player is actively pulling bytes.
/// </summary>
public class ActiveStreamRegistry
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public Guid GetOrCreate(string path, string clientKey, string fileName, long? fileSize)
    {
        var id = DeriveId(path, clientKey);
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            id,
            _ => new Entry
            {
                Id = id,
                Path = path,
                FileName = fileName,
                FileSize = fileSize,
                ClientKey = clientKey,
                StartedAt = now,
                LastActivityAt = now,
            },
            (_, existing) =>
            {
                existing.LastActivityAt = now;
                if (fileSize is { } size) existing.FileSize = size;
                return existing;
            });
        return id;
    }

    public void Touch(Guid id, long bytesRead)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.LastActivityAt = DateTimeOffset.UtcNow;
            if (bytesRead > 0) Interlocked.Add(ref entry.BytesRead, bytesRead);
        }
    }

    /// <summary>
    /// Update the user-facing metadata on an existing session. Used once the
    /// real filename/size are resolved from the dav store (the path passed to
    /// GetOrCreate is usually an opaque GUID for .ids/-style paths).
    /// </summary>
    public void UpdateInfo(Guid id, string? fileName, long? fileSize)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        if (!string.IsNullOrWhiteSpace(fileName)) entry.FileName = fileName;
        if (fileSize is { } size) entry.FileSize = size;
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - ActivityWindow;
        return _entries.Values
            .Where(e => e.LastActivityAt >= cutoff)
            .OrderBy(e => e.StartedAt)
            .ToList();
    }

    /// <summary>
    /// Remove entries that haven't been touched within the activity window.
    /// Returns ids that were pruned so callers can clear external bookkeeping.
    /// </summary>
    public IReadOnlyList<Guid> PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - ActivityWindow;
        var expired = _entries
            .Where(kv => kv.Value.LastActivityAt < cutoff)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in expired) _entries.TryRemove(id, out _);
        return expired;
    }

    public int Count => _entries.Count;

    // (path, clientKey) -> stable Guid so successive range requests from the
    // same player on the same file share a single session id.
    private static Guid DeriveId(string path, string clientKey)
    {
        var bytes = Encoding.UTF8.GetBytes($"{path}\n{clientKey}");
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(bytes, hash);
        return new Guid(hash);
    }

    public sealed class Entry
    {
        public Guid Id { get; init; }
        public string Path { get; init; } = "";
        public string FileName { get; set; } = "";
        public long? FileSize { get; set; }
        public string ClientKey { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastActivityAt { get; set; }
        public long BytesRead;
    }
}
