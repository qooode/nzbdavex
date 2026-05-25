using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class SearchProfileService(
    ConfigManager configManager,
    NzbResolutionCache cache,
    NewznabRateLimiter rateLimiter,
    IndexerHitTracker hitTracker,
    TvdbIdResolver tvdbResolver,
    ExternalIdResolver externalResolver,
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
        var externalProvider = GetExternalProvider(id);

        if (type == "movie")
        {
            if (externalProvider is not null)
                return await BuildExternalMovieQueryAsync(externalProvider, id, ct).ConfigureAwait(false);

            var imdb = StripImdbPrefix(id);
            if (imdb is null) return null;
            return new Dictionary<string, string>
            {
                ["t"] = "movie",
                ["imdbid"] = imdb,
                ["cat"] = "2000",
                ["limit"] = "100",
            };
        }
        if (type == "series")
        {
            if (externalProvider is not null)
                return await BuildExternalSeriesQueryAsync(externalProvider, id, ct).ConfigureAwait(false);

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
                ["limit"] = "100",
            };
            var tvdb = await tvdbResolver.GetTvdbIdAsync(imdb, ct).ConfigureAwait(false);
            if (tvdb.HasValue) dict["tvdbid"] = tvdb.Value.ToString();
            else dict["imdbid"] = imdb;
            return dict;
        }
        return null;
    }

    private async Task<IReadOnlyDictionary<string, string>?> BuildExternalMovieQueryAsync(string provider, string id, CancellationToken ct)
    {
        var parts = id.Split(':');
        if (parts.Length < 2) return null;
        if (!long.TryParse(parts[1], out var externalId)) return null;
        var mapping = await externalResolver.ResolveAsync(provider, externalId, ct).ConfigureAwait(false);
        var imdb = mapping?.ImdbId is { } i ? StripImdbPrefix(i) : null;
        if (imdb is null) return null;
        return new Dictionary<string, string>
        {
            ["t"] = "movie",
            ["imdbid"] = imdb,
            ["cat"] = "2000,2070",
            ["limit"] = "100",
        };
    }

    private async Task<IReadOnlyDictionary<string, string>?> BuildExternalSeriesQueryAsync(string provider, string id, CancellationToken ct)
    {
        var parts = id.Split(':');
        if (parts.Length < 2) return null;
        if (!long.TryParse(parts[1], out var externalId)) return null;
        var mapping = await externalResolver.ResolveAsync(provider, externalId, ct).ConfigureAwait(false);
        if (mapping is null) return null;

        // <provider>:ID, <provider>:ID:EP, <provider>:ID:SEASON:EP
        int season = mapping.Season;
        int? episode = null;
        if (parts.Length == 3 && int.TryParse(parts[2], out var ep))
        {
            episode = ep;
        }
        else if (parts.Length >= 4 && int.TryParse(parts[2], out var s) && int.TryParse(parts[3], out var ep2))
        {
            if (s > 0) season = s;
            episode = ep2;
        }

        // films sometimes arrive on the series endpoint — treat as movies regardless of any episode component
        if (mapping.IsMovie)
        {
            var imdb = mapping.ImdbId is { } i ? StripImdbPrefix(i) : null;
            if (imdb is null) return null;
            return new Dictionary<string, string>
            {
                ["t"] = "movie",
                ["imdbid"] = imdb,
                ["cat"] = "2000,2070",
                ["limit"] = "100",
            };
        }

        if (episode is null) return null;

        var dict = new Dictionary<string, string>
        {
            ["t"] = "tvsearch",
            ["season"] = season.ToString(),
            ["ep"] = episode.Value.ToString(),
            ["cat"] = "5000,5070",
            ["limit"] = "100",
        };
        if (mapping.TvdbId is { } tvdb) dict["tvdbid"] = tvdb.ToString();
        else if (mapping.ImdbId is { } imdb && StripImdbPrefix(imdb) is { } imdbDigits) dict["imdbid"] = imdbDigits;
        else return null;
        return dict;
    }

    private static string? GetExternalProvider(string id)
    {
        if (id.StartsWith("kitsu:", StringComparison.OrdinalIgnoreCase)) return "kitsu";
        if (id.StartsWith("mal:", StringComparison.OrdinalIgnoreCase)) return "mal";
        if (id.StartsWith("anilist:", StringComparison.OrdinalIgnoreCase)) return "anilist";
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
                var hitCheck = await hitTracker
                    .CheckAsync(x.Name, IndexerApiHit.HitType.Search, x.HitLimit, x.HitLimitResetTime, ct)
                    .ConfigureAwait(false);
                if (hitCheck is { Allowed: false })
                {
                    Log.Information("Indexer {Indexer} skipped: {Reason}",
                        x.Name, IndexerHitTracker.FormatSkipReason(hitCheck, IndexerApiHit.HitType.Search));
                    return Enumerable.Empty<IndexerHit>();
                }

                var ua = string.IsNullOrWhiteSpace(x.UserAgent) ? configManager.GetUserAgent() : x.UserAgent;
                var proxy = string.IsNullOrWhiteSpace(x.ProxyUrl) ? globalProxy : x.ProxyUrl;
                var timeout = indexerConfig.GetEffectiveTimeoutSeconds(x);
                await rateLimiter.WaitAsync(x.Name, x.MaxRequestsPerMinute, ct).ConfigureAwait(false);
                var client = new NewznabClient(x.Url, x.ApiKey, ua, proxy, timeout);
                var items = await client.QueryAsync(queryParams, ct).ConfigureAwait(false);
                _ = hitTracker.RecordAsync(x.Name, IndexerApiHit.HitType.Search, CancellationToken.None);
                var filtered = IndexerResultFilter.Apply(items, x.Filter, now);
                return filtered.Select(i => new IndexerHit(x.Name, ua, proxy, i));
            }
            catch (Exception e)
            {
                if (!e.IsCancellationException())
                    Log.Warning("Indexer {Indexer} search failed: {Message}", x.Name, e.Message);
                return [];
            }
        })).ConfigureAwait(false);

        var anyPreferDownloaded = indexers.Any(x => x.Filter is { Enabled: true, PreferDownloaded: true });

        var excludePatterns = configManager.GetSearchExcludePatterns();

        var dedupedQuery = perIndexer
            .SelectMany(x => x)
            .Where(x => !string.IsNullOrWhiteSpace(x.Item.NzbUrl))
            .Where(x => !MatchesExcludePattern(x.Item.Title, excludePatterns))
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
                NzbUrl = x.Item.NzbUrl,
                Title = x.Item.Title,
                Size = x.Item.Size,
                Posted = x.Item.Posted,
                UsenetDate = x.Item.UsenetDate,
                Grabs = x.Item.Grabs,
                Password = x.Item.Password,
                ProxyUrl = x.IndexerProxyUrl,
                SourceIndexerName = x.Item.SourceIndexerName,
                Language = x.Item.Language,
                Subs = x.Item.Subs,
                InfoHash = x.Item.InfoHash,
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

    private static bool MatchesExcludePattern(string? title, IReadOnlyList<Regex> patterns)
    {
        if (patterns.Count == 0 || string.IsNullOrEmpty(title)) return false;
        var normalized = title.Replace('_', '.');
        foreach (var p in patterns)
        {
            try
            {
                if (p.IsMatch(normalized)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                Log.Warning("Search exclude pattern {Pattern} timed out matching title {Title}", p, title);
            }
        }
        return false;
    }

    private static string? StripImdbPrefix(string id)
    {
        if (!id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return null;
        var digits = id[2..];
        return digits.All(char.IsDigit) ? digits : null;
    }

    private record IndexerHit(string IndexerName, string IndexerUserAgent, string? IndexerProxyUrl, NewznabClient.NewznabItem Item);

    public class SearchResult
    {
        public required string ProfileToken { get; init; }
        public required string Type { get; init; }
        public required string Id { get; init; }
        public required IReadOnlyList<NzbResolutionCache.Candidate> Candidates { get; init; }
        public required string[] PlayTokens { get; init; }
    }
}
