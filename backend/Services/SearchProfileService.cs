using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Indexers;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
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
    TmdbIdResolver tmdbResolver,
    ExternalIdResolver externalResolver,
    ImdbTitleResolver titleResolver,
    PreflightOrchestrator preflightOrchestrator,
    WardenStore wardenStore)
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
        if (IsTvdbId(id))
            return BuildTvdbQuery(type, id);

        if (IsTmdbId(id))
        {
            var rewritten = await RewriteTmdbToImdbAsync(type, id, ct).ConfigureAwait(false);
            if (rewritten is null) return null;
            id = rewritten;
        }

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
        if (type == "season")
        {
            var parts = id.Split(':');
            if (parts.Length < 2) return null;
            var seasonImdb = StripImdbPrefix(parts[0]);
            if (seasonImdb is null) return null;
            if (!int.TryParse(parts[1], out var seasonNum)) return null;
            var dict = new Dictionary<string, string>
            {
                ["t"] = "tvsearch",
                ["season"] = seasonNum.ToString(),
                ["cat"] = "5000",
                ["limit"] = "100",
            };
            var tvdb = await tvdbResolver.GetTvdbIdAsync(seasonImdb, ct).ConfigureAwait(false);
            if (tvdb.HasValue) dict["tvdbid"] = tvdb.Value.ToString();
            else dict["imdbid"] = seasonImdb;
            return dict;
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
        if (mapping is null) return null;

        var imdb = mapping.ImdbId is { } i ? StripImdbPrefix(i) : null;
        if (imdb is not null)
        {
            return new Dictionary<string, string>
            {
                ["t"] = "movie",
                ["imdbid"] = imdb,
                ["cat"] = "2000,2070",
                ["limit"] = "100",
            };
        }

        // last resort: no cross-id anywhere, search by the anime's title
        if (!string.IsNullOrWhiteSpace(mapping.Title))
        {
            return new Dictionary<string, string>
            {
                ["t"] = "movie",
                ["q"] = mapping.Title,
                ["cat"] = "2000,2070",
                ["limit"] = "100",
            };
        }
        return null;
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
            if (imdb is not null)
            {
                return new Dictionary<string, string>
                {
                    ["t"] = "movie",
                    ["imdbid"] = imdb,
                    ["cat"] = "2000,2070",
                    ["limit"] = "100",
                };
            }
            if (!string.IsNullOrWhiteSpace(mapping.Title))
            {
                return new Dictionary<string, string>
                {
                    ["t"] = "movie",
                    ["q"] = mapping.Title,
                    ["cat"] = "2000,2070",
                    ["limit"] = "100",
                };
            }
            return null;
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
        // last resort: no cross-id anywhere, fall back to a title query (+ season/ep). Episode
        // numbering is best-effort here — a single-cours Kitsu entry maps cleanly, but multi-cour
        // entries that use absolute numbering may land on the wrong episode.
        else if (!string.IsNullOrWhiteSpace(mapping.Title)) dict["q"] = mapping.Title;
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

    private static bool IsTvdbId(string id) =>
        id.StartsWith("tvdb-", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("tvdb:", StringComparison.OrdinalIgnoreCase);

    private static bool IsTmdbId(string id) =>
        id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("tmdb-", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string>? BuildTvdbQuery(string type, string id)
    {
        if (type != "series") return null;

        var rest = id["tvdb".Length..].TrimStart('-', ':');
        var parts = rest.Split(':');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var tvdb)) return null;
        if (!int.TryParse(parts[1], out var season)) return null;
        if (!int.TryParse(parts[2], out var episode)) return null;

        return new Dictionary<string, string>
        {
            ["t"] = "tvsearch",
            ["tvdbid"] = tvdb.ToString(),
            ["season"] = season.ToString(),
            ["ep"] = episode.ToString(),
            ["cat"] = "5000",
            ["limit"] = "100",
        };
    }

    private async Task<string?> RewriteTmdbToImdbAsync(string type, string id, CancellationToken ct)
    {
        var parts = id["tmdb".Length..].TrimStart('-', ':').Split(':');
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) return null;

        var imdb = await tmdbResolver.GetImdbIdAsync(type, parts[0], ct).ConfigureAwait(false);
        if (imdb is null) return null;

        if (type == "movie") return imdb;
        if (type == "series")
        {
            if (parts.Length < 3) return null;
            return $"{imdb}:{parts[1]}:{parts[2]}";
        }
        return null;
    }

    public async Task<SearchResult?> SearchByImdbAsync(
        string profileToken, string type, string id, CancellationToken ct,
        string? clientQuery = null, bool verifyIdentity = false)
    {
        var queryParams = await BuildImdbQueryAsync(type, id, ct).ConfigureAwait(false);
        if (queryParams is null) return Empty(profileToken, type, id);
        return await SearchAsync(profileToken, type, id, queryParams, ct, clientQuery, verifyIdentity).ConfigureAwait(false);
    }

    public async Task<SearchResult?> SearchAsync(
        string profileToken,
        string type,
        string id,
        IReadOnlyDictionary<string, string> queryParams,
        CancellationToken ct,
        string? clientQuery = null,
        bool verifyIdentity = false)
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
        var excludePatterns = configManager.GetSearchExcludePatterns();
        var anyPreferDownloaded = indexers.Any(x => x.Filter is { Enabled: true, PreferDownloaded: true });

        var perIndexer = await RunPerIndexerQueryAsync(indexers, queryParams, indexerConfig, globalProxy, now, ct)
            .ConfigureAwait(false);

        var deduped = DedupeAndSort(perIndexer, excludePatterns, anyPreferDownloaded);

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

        var fallbackMode = type == "movie" ? profile.MovieFallback : profile.TvFallback;
        var fallbackThreshold = Math.Max(1, type == "movie"
            ? profile.MovieFallbackMinResults
            : profile.TvFallbackMinResults);

        var reqSeason = queryParams.TryGetValue("season", out var seasonStr) && int.TryParse(seasonStr, out var sv)
            ? (int?)sv : null;
        var reqEpisode = queryParams.TryGetValue("ep", out var epStr) && int.TryParse(epStr, out var ev)
            ? (int?)ev : null;
        bool EpisodeOk(IndexerHit x) =>
            type != "series"
            || (reqSeason is null && reqEpisode is null)
            || FilenameMatcher.EpisodeCompatible(x.Item.Title, reqSeason, reqEpisode);

        if (fallbackMode != ProfileConfig.FallbackMode.Off && deduped.Count < fallbackThreshold)
        {
            var fallbackVariants = await BuildFallbackQueriesAsync(type, queryParams, clientQuery, fallbackMode, ct)
                .ConfigureAwait(false);
            if (fallbackVariants.Count > 0)
            {
                Log.Information(
                    "Profile {Profile} {Type}/{Id}: only {Count} result(s) (< {Threshold}); running {Mode} text-query fallback ({Variants} variant(s))",
                    profile.Name, type, id, deduped.Count, fallbackThreshold, fallbackMode, fallbackVariants.Count);

                var combinedHits = perIndexer.SelectMany(x => x).ToList();

                // Escalate through variants (targeted -> broad), stopping as soon as we clear the
                // threshold so a broad title-only query only runs when the precise one falls short.
                foreach (var variant in fallbackVariants)
                {
                    var variantPerIndexer = await RunPerIndexerQueryAsync(
                        indexers, variant, indexerConfig, globalProxy, now, ct).ConfigureAwait(false);
                    combinedHits.AddRange(variantPerIndexer.SelectMany(x => x));

                    var distinctCount = combinedHits
                        .Where(x => !string.IsNullOrWhiteSpace(x.Item.NzbUrl))
                        .Where(x => !MatchesExcludePattern(x.Item.Title, excludePatterns))
                        .Where(EpisodeOk)
                        .Select(x => x.Item.NzbUrl)
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    if (distinctCount >= fallbackThreshold) break;
                }

                var combined = combinedHits
                    .Where(x => !string.IsNullOrWhiteSpace(x.Item.NzbUrl))
                    .Where(x => !MatchesExcludePattern(x.Item.Title, excludePatterns))
                    .Where(EpisodeOk)
                    .GroupBy(x => x.Item.NzbUrl)
                    .Select(g => g.First())
                    .ToList();

                deduped = (anyPreferDownloaded
                        ? combined.OrderByDescending(x => x.Item.Grabs ?? -1)
                                  .ThenByDescending(x => x.Item.Size)
                                  .ThenByDescending(x => x.Item.Posted ?? DateTimeOffset.MinValue)
                        : combined.OrderByDescending(x => x.Item.Size)
                                  .ThenByDescending(x => x.Item.Posted ?? DateTimeOffset.MinValue))
                    .ToList();
            }
        }

        if (verifyIdentity && type is "series" or "season")
        {
            var expected = await BuildExpectedSeriesTitlesAsync(queryParams, ct).ConfigureAwait(false);
            if (expected.Count > 0)
            {
                var before = deduped.Count;
                deduped = deduped
                    .Where(EpisodeOk)
                    .Where(x => FilenameMatcher.TitleMatches(expected, x.Item.Title))
                    .ToList();
                if (deduped.Count != before)
                    Log.Information(
                        "Identity guard {Type}/{Id}: kept {Kept}/{Before} result(s) matching \"{Title}\"",
                        type, id, deduped.Count, before, string.Join("\" / \"", expected));
            }
            else
            {
                Log.Debug("Identity guard {Type}/{Id}: no canonical title resolved; not filtering", type, id);
            }
        }

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
                Poster = x.Item.Poster,
                Grabs = x.Item.Grabs,
                Password = x.Item.Password,
                ProxyUrl = x.IndexerProxyUrl,
                SourceIndexerName = x.Item.SourceIndexerName,
                Language = x.Item.Language,
                Subs = x.Item.Subs,
                InfoHash = x.Item.InfoHash,
            })
            .ToList();

        if (configManager.IsWatchtowerEnabled())
            await AddWarmedSeasonBundleAsync(candidates, type, id, ct).ConfigureAwait(false);

        if (candidates.Count == 0) return Empty(profileToken, type, id);

        if (configManager.IsWardenHideDeadEnabled())
        {
            var alive = candidates
                .Where(c => !wardenStore.IsDeadAnywhere(WardenFingerprint.Compute(c.Size, c.Poster, c.UsenetDate)))
                .ToList();
            if (alive.Count > 0) candidates = alive;
        }

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

    private async Task AddWarmedSeasonBundleAsync(
        List<NzbResolutionCache.Candidate> candidates, string type, string id, CancellationToken ct)
    {
        if (type != "series") return;
        var parts = id.Split(':');
        if (parts.Length < 3) return;
        if (!int.TryParse(parts[^2], out var season)) return;
        var imdb = parts[0];
        if (!imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            await using var ctx = new DavDatabaseContext();
            var seasonRow = await ctx.WantedItems.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Key == $"season:{imdb}:{season}" && w.State == WantedItem.StateReady, ct)
                .ConfigureAwait(false);
            if (seasonRow is null) return;

            foreach (var p in WtJson.ReadPointers(seasonRow.Shortlist))
            {
                if (p.Verdict != "available") continue;
                if (candidates.Any(c => c.NzbUrl == p.NzbUrl)) continue;
                candidates.Add(new NzbResolutionCache.Candidate
                {
                    IndexerName = p.IndexerName,
                    IndexerUserAgent = p.IndexerUserAgent,
                    NzbUrl = p.NzbUrl,
                    Title = p.Title,
                    Size = p.Size,
                    Grabs = p.Grabs,
                    Poster = p.Poster,
                    UsenetDate = p.UsenetDate,
                    ProxyUrl = p.ProxyUrl,
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Log.Debug(e, "Watchtower: season-bundle candidate augment failed for {Id}", id);
        }
    }

    private async Task<IEnumerable<IndexerHit>[]> RunPerIndexerQueryAsync(
        IReadOnlyList<IndexerConfig.ConnectionDetails> indexers,
        IReadOnlyDictionary<string, string> queryParams,
        IndexerConfig indexerConfig,
        string? globalProxy,
        DateTimeOffset now,
        CancellationToken ct)
    {
        return await Task.WhenAll(indexers.Select(async x =>
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
                var indexerQuery = ApplyIndexerCategoryOverrides(queryParams, x);
                var items = await client.QueryAsync(indexerQuery, ct).ConfigureAwait(false);
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
    }

    private static List<IndexerHit> DedupeAndSort(
        IEnumerable<IndexerHit>[] perIndexer,
        IReadOnlyList<Regex> excludePatterns,
        bool anyPreferDownloaded)
    {
        var dedupedQuery = perIndexer
            .SelectMany(x => x)
            .Where(x => !string.IsNullOrWhiteSpace(x.Item.NzbUrl))
            .Where(x => !MatchesExcludePattern(x.Item.Title, excludePatterns))
            .GroupBy(x => x.Item.NzbUrl)
            .Select(g => g.First());

        return (anyPreferDownloaded
                ? dedupedQuery.OrderByDescending(x => x.Item.Grabs ?? -1)
                              .ThenByDescending(x => x.Item.Size)
                              .ThenByDescending(x => x.Item.Posted ?? DateTimeOffset.MinValue)
                : dedupedQuery.OrderByDescending(x => x.Item.Size)
                              .ThenByDescending(x => x.Item.Posted ?? DateTimeOffset.MinValue))
            .ToList();
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> BuildFallbackQueriesAsync(
        string type,
        IReadOnlyDictionary<string, string> originalParams,
        string? clientQuery,
        ProfileConfig.FallbackMode mode,
        CancellationToken ct)
    {
        var empty = Array.Empty<IReadOnlyDictionary<string, string>>();
        var imdbDigits = originalParams.TryGetValue("imdbid", out var imdb) ? imdb : null;
        int? tvdbId = null;
        if (originalParams.TryGetValue("tvdbid", out var tvdbStr) && int.TryParse(tvdbStr, out var t)) tvdbId = t;

        var title = string.IsNullOrWhiteSpace(clientQuery)
            ? await titleResolver.GetTitleAsync(type, imdbDigits, tvdbId, ct).ConfigureAwait(false)
            : clientQuery.Trim();

        if (string.IsNullOrWhiteSpace(title)) return empty;

        if (type == "movie")
        {
            return new IReadOnlyDictionary<string, string>[]
            {
                new Dictionary<string, string>
                {
                    ["t"] = "movie",
                    ["q"] = title,
                    ["cat"] = "2000",
                    ["limit"] = "100",
                },
            };
        }

        if (type == "series")
        {
            var variants = new List<IReadOnlyDictionary<string, string>>();

            // Targeted: title + structured season/ep — precise, matches well-tagged episodes.
            var targeted = new Dictionary<string, string>
            {
                ["t"] = "tvsearch",
                ["q"] = title,
                ["cat"] = "5000",
                ["limit"] = "100",
            };
            if (originalParams.TryGetValue("season", out var s)) targeted["season"] = s;
            if (originalParams.TryGetValue("ep", out var e)) targeted["ep"] = e;
            variants.Add(targeted);

            // Broad: title only, no season/ep — catches flat/special releases that carry no
            // episode metadata (e.g. 2-part documentary specials). Reached only via escalation
            // when the targeted query falls short, so normal episodic shows aren't flooded.
            if (mode == ProfileConfig.FallbackMode.Broad
                && (targeted.ContainsKey("season") || targeted.ContainsKey("ep")))
            {
                variants.Add(new Dictionary<string, string>
                {
                    ["t"] = "tvsearch",
                    ["q"] = title,
                    ["cat"] = "5000",
                    ["limit"] = "100",
                });
            }

            return variants;
        }

        return empty;
    }

    private async Task<HashSet<string>> BuildExpectedSeriesTitlesAsync(
        IReadOnlyDictionary<string, string> queryParams, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var imdbDigits = queryParams.TryGetValue("imdbid", out var imdb) ? imdb : null;
        int? tvdbId = null;
        if (queryParams.TryGetValue("tvdbid", out var tvdbStr) && int.TryParse(tvdbStr, out var t)) tvdbId = t;
        if (imdbDigits is null && tvdbId is null) return set;

        var title = await titleResolver.GetTitleAsync("series", imdbDigits, tvdbId, ct).ConfigureAwait(false);
        var norm = FilenameMatcher.NormalizeTitle(title);
        if (norm.Length > 0) set.Add(norm);
        return set;
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

    private static IReadOnlyDictionary<string, string> ApplyIndexerCategoryOverrides(
        IReadOnlyDictionary<string, string> queryParams,
        IndexerConfig.ConnectionDetails indexer)
    {
        var needsCatChange = indexer.IgnoreCategoryFilter
            || !string.IsNullOrWhiteSpace(indexer.ExtraMovieCategories)
            || !string.IsNullOrWhiteSpace(indexer.ExtraTvCategories);
        if (!needsCatChange) return queryParams;

        var copy = new Dictionary<string, string>(queryParams, StringComparer.OrdinalIgnoreCase);

        if (indexer.IgnoreCategoryFilter)
        {
            copy.Remove("cat");
            return copy;
        }

        var t = copy.TryGetValue("t", out var tVal) ? tVal : null;
        string? extras = t switch
        {
            "movie" => indexer.ExtraMovieCategories,
            "tvsearch" => indexer.ExtraTvCategories,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(extras)) return copy;

        copy.TryGetValue("cat", out var existing);
        copy["cat"] = MergeCategoryList(existing, extras);
        return copy;
    }

    private static string MergeCategoryList(string? baseList, string extras)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        void AddAll(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var part in raw.Split(','))
            {
                var v = part.Trim();
                if (v.Length == 0) continue;
                if (seen.Add(v)) result.Add(v);
            }
        }
        AddAll(baseList);
        AddAll(extras);
        return string.Join(",", result);
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
