using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public class WardenStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    private readonly string _path = Path.Join(DavDatabaseContext.ConfigPath, "warden.json");
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _saveLock = new();
    private int _saveScheduled;

    public WardenStore()
    {
        Load();
    }

    public int Count => _entries.Count;

    public void MarkDead(string? fp, string? backbone)
    {
        if (string.IsNullOrEmpty(fp)) return;
        var bk = string.IsNullOrWhiteSpace(backbone) ? "unknown" : backbone;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _entries.AddOrUpdate(fp,
            _ => new Entry(fp, now, 1, new[] { bk }),
            (_, old) => new Entry(fp, now, old.Count + 1, MergeBackbones(old.Backbones, new[] { bk })));
        ScheduleSave();
    }

    public bool IsDeadAnywhere(string? fp)
    {
        if (string.IsNullOrEmpty(fp)) return false;
        if (!_entries.TryGetValue(fp, out var e)) return false;
        return e.DeadAt >= FreshnessCutoff();
    }

    public bool IsDead(string? fp, string? backbone)
    {
        if (string.IsNullOrEmpty(fp)) return false;
        if (!_entries.TryGetValue(fp, out var e)) return false;
        if (e.DeadAt < FreshnessCutoff()) return false;
        var bk = string.IsNullOrWhiteSpace(backbone) ? "unknown" : backbone;
        return Array.IndexOf(e.Backbones, bk) >= 0;
    }

    public WardenFile Export()
    {
        var cutoff = FreshnessCutoff();
        return new WardenFile
        {
            Version = 1,
            Entries = _entries.Values
                .Where(e => e.DeadAt >= cutoff)
                .Select(e => new WardenRecord
                {
                    Fp = e.Fp,
                    Backbones = e.Backbones,
                    DeadAt = e.DeadAt,
                    Count = e.Count,
                })
                .ToList(),
        };
    }

    public int Import(WardenFile model)
    {
        if (model.Entries is null || model.Entries.Count == 0) return 0;
        var added = 0;
        foreach (var r in model.Entries)
        {
            if (string.IsNullOrEmpty(r.Fp)) continue;
            var incoming = MergeBackbones(Array.Empty<string>(), r.Backbones ?? Array.Empty<string>());
            if (incoming.Length == 0) incoming = new[] { "unknown" };
            var n = r.Count <= 0 ? 1 : r.Count;
            if (!_entries.ContainsKey(r.Fp)) added++;
            _entries.AddOrUpdate(r.Fp,
                _ => new Entry(r.Fp, r.DeadAt, n, incoming),
                (_, old) => new Entry(r.Fp, Math.Max(old.DeadAt, r.DeadAt), old.Count + n, MergeBackbones(old.Backbones, incoming)));
        }
        ScheduleSave();
        return added;
    }

    public int Clear()
    {
        var n = _entries.Count;
        _entries.Clear();
        ScheduleSave();
        return n;
    }

    private static long FreshnessCutoff()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)Ttl.TotalSeconds;

    private static string[] MergeBackbones(string[] a, string[] b)
        => a.Concat(b)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var model = JsonSerializer.Deserialize<WardenFile>(json, JsonOptions);
            if (model?.Entries is null) return;
            foreach (var r in model.Entries)
            {
                if (string.IsNullOrEmpty(r.Fp)) continue;
                var bk = MergeBackbones(Array.Empty<string>(), r.Backbones ?? Array.Empty<string>());
                if (bk.Length == 0) bk = new[] { "unknown" };
                _entries[r.Fp] = new Entry(r.Fp, r.DeadAt, r.Count <= 0 ? 1 : r.Count, bk);
            }
            Log.Information("Warden: loaded {Count} fingerprint(s) from {Path}", _entries.Count, _path);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Warden: failed to load {Path}", _path);
        }
    }

    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _saveScheduled, 0); }
            Save();
        });
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Export(), JsonOptions);
            lock (_saveLock)
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "Warden: failed to save {Path}", _path);
        }
    }

    private sealed record Entry(string Fp, long DeadAt, int Count, string[] Backbones);
}

public class WardenFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("entries")] public List<WardenRecord> Entries { get; set; } = new();
}

public class WardenRecord
{
    [JsonPropertyName("fp")] public string Fp { get; set; } = "";
    [JsonPropertyName("bk")] public string[]? Backbones { get; set; }
    [JsonPropertyName("deadAt")] public long DeadAt { get; set; }
    [JsonPropertyName("n")] public int Count { get; set; }
}
