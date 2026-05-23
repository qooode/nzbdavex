using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class SearchProfileService(
    ConfigManager configManager,
    NzbResolutionCache cache,
    NewznabRateLimiter rateLimiter,
    TvdbIdResolver tvdbResolver,
    PreflightOrchestrator preflightOrchestrator)
{
    public ProfileConfig.Profile? GetProfile(string token)
        => configManager.GetProfileConfig().Profiles.FirstOrDefault(x => x.Token == token);

    public bool IsAdapterEnabled(string profileToken, string adapter)
    {
        var profile = GetProfile(profileToken);
        if (profile is null) return false;
        if (profile.EnabledAdapters is null || profile.EnabledAdapters.Count == 0) return true;
        return profile.EnabledAdapters.Contains(adapter, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, string>?> BuildImdbQueryAsync(
        string type, string id, CancellationToken ct)
    {
        if (type == "movie")
        {
            var imdb = StripImdbPrefix(id);
            if (imdb is null) return null;
            return new Dictionary<string, string>
            {
                ["t"] = "movie",
                ["imdbid"] = imdb,
                ["cat"] = "2000",
                ["limit"] = "200",
            };
        }
        if (type == "series")
        {
            var parts = id.Split(':');
            if (parts.Length < 3) return null;
            var imdb = StripImdbPrefix(parts[0]);
            if (imdb is null) return null;
            if (!int.TryParse(parts[1], out var season)) return null;
            if (!int.TryParse(parts[2], out var episode)) return null;
            var dict = new Dictionary<string, string>
            {
                ["t"] = "tvsearch",
                ["season"] = season.ToString(),
                ["ep"] = episode.ToString(),
                ["cat"] = "5000",
                ["limit"] = "200",
            };
            var tvdb = await tvdbResolver.GetTvdbIdAsync(imdb, ct).ConfigureAwait(false);
            if (tvdb.HasValue) dict["tvdbid"] = tvdb.Value.ToString();
            else dict["imdbid"] = imdb;
            return dict;
        }
        return null;
    }

    public async Task<SearchResult?> SearchByImdbAsync(
        string profileToken, string type, string id, CancellationToken ct)
    {
        var queryParams = await BuildImdbQueryAsync(type, id, ct).ConfigureAwait(false);
        if (queryParams is null) return Empty(profileToken, type, id);
        return await SearchAsync(profileToken, type, id, queryParams, ct).ConfigureAwait(false);
    }

    public async Task<SearchResult?> SearchAsync(
        string profileToken,
        string type,
        string id,
        IReadOnlyDictionary<string, string> queryParams,
        CancellationToken ct)
    {
        var profile = GetProfile(profileToken);
        if (profile is null) return null;

        var indexerConfig = configManager.GetIndexerConfig();
        var allIndexers = indexerConfig.Indexers.Where(x => x.Enabled).ToList();
        var indexers = profile.IndexerNames.Count == 0
            ? allIndexers
            : allIndexers.Where(x => profile.IndexerNames.Contains(x.Name)).ToList();
        var globalProxy = indexerConfig.ProxyUrl;

        if (indexers.Count == 0) return Empty(profileToken, type, id);

        var now = DateTimeOffset.UtcNow;
        var perIndexer = await Task.WhenAll(indexers.Select(async x =>
        {
            try
            {
                var ua = string.IsNullOrWhiteSpace(x.UserAgent) ? configManager.GetUserAgent() : x.UserAgent;
                var proxy = string.IsNullOrWhiteSpace(x.ProxyUrl) ? globalProxy : x.ProxyUrl;
                var timeout = indexerConfig.GetEffectiveTimeoutSeconds(x);
                await rateLimiter.WaitAsync(x.Name, x.MaxRequestsPerMinute, ct).ConfigureAwait(false);
                var client = new NewznabClient(x.Url, x.ApiKey, ua, proxy, timeout);
                var items = await client.QueryAsync(queryParams, ct).ConfigureAwait(false);
                var filtered = IndexerResultFilter.Apply(items, x.Filter, now);
                return filtered.Select(i => new IndexerHit(x.Name, ua, i));
            }
            catch (Exception e)
            {
                if (!e.IsCancellationException())
                    Log.Warning("Indexer {Indexer} search failed: {Message}", x.Name, e.Message);
                return [];
            }
        })).ConfigureAwait(false);

        var anyPreferDownloaded = indexers.Any(x => x.Filter is { Enabled: true, PreferDownloaded: true });

        var dedupedQuery = perIndexer
            .SelectMany(x => x)
            .Where(x => !string.IsNullOrWhiteSpace(x.Item.NzbUrl))
            .GroupBy(x => x.Item.NzbUrl)
            .Select(g => g.First());

        var deduped = (anyPreferDownloaded
                ? dedupedQuery.OrderByDescending(x => x.Item.Grabs ?? -1)
                              .ThenByDescending(x => x.Item.Size)
                              .ThenByDescending(x => x.Item.Posted ?? DateTimeOffset.MinValue)
                : dedupedQuery.OrderByDescending(x => x.Item.Size)
                              .ThenByDescending(x => x.Item.Posted ?? DateTimeOffset.MinValue))
            .ToList();

        var strictIndexers = indexers
            .Where(x => x.EnableStrictMatching)
            .Select(x => x.Name)
            .ToHashSet();

        if (strictIndexers.Count > 0 && deduped.Count >= 2)
        {
            var withHead = deduped
                .Select(x => new { Entry = x, Head = FilenameMatcher.HeadTokens(x.Item.Title) })
                .ToList();

            var consensus = withHead
                .Where(x => x.Head.Length > 0)
                .GroupBy(x => string.Join(' ', x.Head))
                .Select(g => new { g.First().Head, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .FirstOrDefault();

            if (consensus is { Count: >= 2 })
            {
                deduped = withHead
                    .Where(x => !strictIndexers.Contains(x.Entry.IndexerName)
                                || FilenameMatcher.TokensEqual(x.Head, consensus.Head))
                    .Select(x => x.Entry)
                    .ToList();
            }
        }

        if (deduped.Count == 0) return Empty(profileToken, type, id);

        var candidates = deduped
            .Select(x => new NzbResolutionCache.Candidate
            {
                IndexerName = x.IndexerName,
                IndexerUserAgent = x.IndexerUserAgent,
                SourceIndexerName = x.Item.SourceIndexerName,
                SourceIndexerHost = x.Item.SourceIndexerHost,
                NzbUrl = x.Item.NzbUrl,
                Title = x.Item.Title,
                Size = x.Item.Size,
                Posted = x.Item.Posted,
                UsenetDate = x.Item.UsenetDate,
                Grabs = x.Item.Grabs,
                Password = x.Item.Password,
            })
            .ToList();

        var tokens = cache.AddGroup(candidates, type, profileToken, id);
        preflightOrchestrator.Start(profileToken, type, id, candidates);

        return new SearchResult
        {
            ProfileToken = profileToken,
            Type = type,
            Id = id,
            Candidates = candidates,
            PlayTokens = tokens,
        };
    }

    private static SearchResult Empty(string profileToken, string type, string id) =>
        new()
        {
            ProfileToken = profileToken,
            Type = type,
            Id = id,
            Candidates = Array.Empty<NzbResolutionCache.Candidate>(),
            PlayTokens = Array.Empty<string>(),
        };

    private static string? StripImdbPrefix(string id)
    {
        if (!id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = id[2..];
        return digits.All(char.IsDigit) ? digits : null;
    }

    private record IndexerHit(string IndexerName, string IndexerUserAgent, NewznabClient.NewznabItem Item);

    public class SearchResult
    {
        public required string ProfileToken { get; init; }
        public required string Type { get; init; }
        public required string Id { get; init; }
        public required IReadOnlyList<NzbResolutionCache.Candidate> Candidates { get; init; }
        public required string[] PlayTokens { get; init; }
    }
}
