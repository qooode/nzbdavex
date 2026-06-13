# Watchtower

Watchtower keeps the titles on your lists **ready**. Each one is pre-resolved to a healthy
release and re-verified over time, so it's found and ready before you need it, with no search
at request time. It's the **watchdog, time-shifted**: the same ranker, the same STAT health
check, the same indexer caps, run *ahead* of time instead of on demand.

**Pointer-only by design.** It stores the winning NZB's segment map (kilobytes) and a small
verified shortlist, never the video. It's a window kept clean, not a library. The one default
that defines the product is `resolve-only`: nothing is downloaded until a title is actually
requested.

## How it works

It's a source-agnostic list feeding the existing resolution engine. A background service runs
three loops:

1. **Sync.** Every enabled source enumerates into content identities (`type:id`, canonical
   imdb). They merge into one **deduped wanted-set**; an item stays wanted while at least one
   source claims it. Dropping off every list removes the row. Downloaded files are never touched.
2. **Resolve.** For items with no fresh winner: search once (cheap), filter by size
   floor/ceiling, take **biggest-first** (or the watchdog's rank, per `ranking`), then
   STAT-verify down the list. The first healthy one wins; up to `shortlist-depth` healthy
   pointers are kept as backups. Bounded by a daily resolve budget, an active warm-set cap, and
   a grab cap (NZB fetches are the scarce indexer bucket, so it's deliberately stingy).
3. **Keep-fresh.** Re-STAT the winner **grab-free** from its stored segment map on age-based
   backoff. If it died, promote a backup; if the shortlist empties, re-resolve.

**Readiness with zero hot-path surgery.** When a title is requested, `ProfilePlayController`
already consults `PreflightCache`. Watchtower simply warms that cache with the verified winner's
bytes (`WatchtowerStore.TryWarmCacheAsync`), so the pre-verify is an instant hit: no fetch, no
STAT. On a miss it falls back to exactly today's behavior.

## Sources (agnostic)

- **Manual.** Add a title by imdb id on the Watchtower page.
- **Stremio catalog.** Any catalog URL (`.../catalog/movie/xyz.json`). This transitively
  supports every list wrapped as a Stremio catalog addon (Trakt, MDBList, Letterboxd, and so on).
- **URL list.** A JSON array / `{items:[...]}` or plain newline-delimited `type:id` / imdb ids.
- **Whole series.** A list naming a show (a bare `series:tt…`) is expanded into its episodes via
  TVmaze (keyless) and tracked through the normal resolve + keep-fresh path. Scope is bounded by
  `series-scope`; only aired episodes are warmed. A finished season is warmed as a single season
  bundle (one release covers the whole season, played per episode); the still-airing season uses
  single episodes. Toggle with `season-bundles`. If a finished season has no healthy bundle,
  `season-bundle-fallback` warms its individual episodes instead and parks the bundle.

Adding a new source kind is one `switch` case in `ListSourceEnumerator`; the engine is unchanged.

## Safety

- Reuses `IndexerHitTracker` (per-indexer query + grab caps, disable-at-cap) and
  `NewznabRateLimiter`. The same discipline that keeps Sonarr/Radarr safe on the same indexers.
- **Safe defaults, opt-in escalation.** Off until enabled; conservative budget, cap, and
  resolve-only. The knobs only *raise* limits.
- **Active warm-set cap** bounds standing load no matter how large the lists get: beyond the
  cap, items are listed but parked.

## Configuration (`watchtower.*`, Settings then Watchtower)

| Key | Default | Meaning |
|-----|---------|---------|
| `enabled` | `false` | Master switch. |
| `profile-token` | `""` | Search profile to resolve with (empty = first profile). |
| `ranking` | `watchdog` | `watchdog` = the same release the watchdog would select (so the warm is reliably used); `largest` = biggest healthy release. |
| `size-floor-bytes` | `524288000` | Junk floor (~0.5 GB). |
| `size-ceiling-bytes` | `0` | Bandwidth ceiling (0 = none). |
| `min-grabs` | `0` | Optional fake filter. |
| `shortlist-depth` | `2` | Live winner + backups. |
| `grab-cap-per-resolve` | `3` | Max NZB fetches per pass (scarce bucket). |
| `verify-sample-count` | `3` | STAT sample segments. |
| `verify-timeout-seconds` | `10` | Per-segment STAT timeout; releases the connection if a provider stops responding. |
| `active-set-cap` | `100` | Items kept actively ready. |
| `daily-resolve-budget` | `60` | Soft new-resolves/day (0 = unlimited). |
| `sync-interval-seconds` | `3600` | Remote list refresh cadence. |
| `keepfresh-base/max-seconds` | `21600` / `604800` | Re-verify backoff window. |
| `unavailable-retry-seconds` | `21600` | Re-search cadence when nothing healthy found. |
| `series-scope` | `latest-season` | How much of a series to warm: `latest-season`, `all-aired`, `recent`, `off`. |
| `series-max-episodes` | `50` | Cap on episodes warmed per series. |
| `series-recent-count` | `3` | Episodes kept when `series-scope` is `recent`. |
| `season-bundles` | `true` | Warm one season bundle for finished seasons instead of every episode. |
| `season-bundle-fallback` | `false` | When a finished season has no healthy bundle, warm its episodes instead and park the bundle. |
| `season-bundle-fallback-scope` | `latest-season` | Which seasons fall back to episodes: `latest-season`, `recent`, `all`. |
| `season-bundle-fallback-recent-count` | `2` | Seasons that fall back when scope is `recent`. |
| `season-bundle-fallback-max-episodes` | `50` | Cap on episodes warmed when a season falls back. |

## Code map

- Models: `Database/Models/{ListSource,WantedItem}.cs` (plus migration and snapshot).
- Engine: `Services/Watchtower/{WatchtowerService,WatchtowerStore,ListSourceEnumerator,WatchtowerModels}.cs`.
- Config: `Config/ConfigManager.cs` (`watchtower.*` getters).
- API: `Api/Controllers/Watchtower/{GetWatchtower,WatchtowerMutate}Controller.cs`.
- Cache-warm hook: `Api/Controllers/Profiles/ProfilePlayController.cs` (one warm call).
- UI: `frontend/app/routes/watchtower/` (page) and `routes/settings/watchtower/` (tuning tab).

## Status / not yet

- Movies, explicit episodes, and whole series — imdb shows are expanded via TVmaze and anime (kitsu)
  via Kitsu, all keyless. mal/anilist catalog ids are accepted as expanders but not yet expanded.
- Dedup is exact-key; cross-namespace collapse (tmdb and imdb for the same title) is a follow-up.
- Optional later: head-prebuffer for a small "next up" set; RSS-sync matching; expose the
  wanted-set *as* a Stremio catalog.
