import { Form } from "react-bootstrap";
import { type Dispatch, type SetStateAction } from "react";
import styles from "./watchtower.module.css";

const GB = 1024 * 1024 * 1024;

type WatchtowerSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WatchtowerSettings({ config, setNewConfig }: WatchtowerSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const enabled = (config["watchtower.enabled"] ?? "false") === "true";
    const scope = config["watchtower.series-scope"] ?? "latest-season";
    const seasonBundles = (config["watchtower.season-bundles"] ?? "true") === "true";
    const bundleFallback = (config["watchtower.season-bundle-fallback"] ?? "false") === "true";
    const bundleFallbackScope = config["watchtower.season-bundle-fallback-scope"] ?? "latest-season";
    const bytesToGb = (b?: string) => { const n = Number(b ?? ""); return n > 0 ? String(+(n / GB).toFixed(2)) : ""; };
    const setGb = (key: string, gb: string) => { const n = Number(gb); set(key, n > 0 ? String(Math.round(n * GB)) : "0"); };

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionTitle}>Watchtower</div>
                <div className={styles.sectionDescription}>
                    Keeps the titles on your lists pre-resolved to a healthy release and re-verified
                    over time, so each is found and ready before you need it. Pointer-only and
                    safe-by-default: it stores segment maps (kilobytes), never video, and respects your
                    indexer caps. Manage your lists on the <b>Watchtower</b> page; tune the engine here.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Check
                    type="switch"
                    id="watchtower-enabled"
                    label="Enable Watchtower"
                    checked={enabled}
                    onChange={e => set("watchtower.enabled", String(e.target.checked))} />
                <p className={styles.hint}>
                    When on, the background engine syncs your lists, resolves the biggest healthy
                    release for each item, and keeps it verified over time. When off, nothing runs.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Search profile token (optional)</Form.Label>
                <Form.Control className={styles.input} type="text"
                    placeholder="empty = use the first configured profile"
                    disabled={!enabled}
                    value={config["watchtower.profile-token"] ?? ""}
                    onChange={e => set("watchtower.profile-token", e.target.value)} />
                <p className={styles.hint}>Which Search Profile the resolver uses. Leave blank to use the first one.</p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Release selection</Form.Label>
                <Form.Select className={styles.input}
                    disabled={!enabled}
                    value={config["watchtower.ranking"] ?? "watchdog"}
                    onChange={e => set("watchtower.ranking", e.target.value)}>
                    <option value="watchdog">Match the watchdog's pick</option>
                    <option value="largest">Largest healthy release</option>
                </Form.Select>
                <p className={styles.hint}>
                    <b>Match the watchdog</b> uses the same rank order as the watchdog, so the release
                    Watchtower readies is exactly the one the watchdog would select. <b>Largest</b>
                    always prefers the biggest healthy release (it may differ from what the watchdog
                    would have chosen).
                </p>
            </Form.Group>

            <div className={styles.section}>
                <div className={styles.sectionTitle}>Series expansion</div>
                <div className={styles.sectionDescription}>
                    How much of a TV show or anime is warmed when a whole series is tracked. Each series
                    is expanded into its seasons/episodes via TVmaze (TV) or Kitsu (anime), keyless, then
                    resolved and kept fresh like any other item.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Label>Series scope</Form.Label>
                <Form.Select className={styles.input}
                    disabled={!enabled}
                    value={scope}
                    onChange={e => set("watchtower.series-scope", e.target.value)}>
                    <option value="latest-season">Latest season only</option>
                    <option value="first-season">First season only</option>
                    <option value="all-aired">All aired seasons</option>
                    <option value="recent">Most recent episodes</option>
                    <option value="off">Off — don't expand series</option>
                </Form.Select>
                <p className={styles.hint}>
                    <b>Latest season</b> warms only the newest season (default). <b>First season</b>
                    warms only season one — handy for watchlists you may start later. <b>All aired</b>
                    backfills every released season. <b>Recent</b> keeps just the last few episodes
                    across the whole series. <b>Off</b> stops series from expanding into episodes.
                    Each list can override this on the Watchtower page.
                </p>
            </Form.Group>

            {scope === "recent" && (
                <Form.Group className={styles.section}>
                    <Form.Label>Recent episode count</Form.Label>
                    <Form.Control className={styles.input} type="number" min={1} max={100}
                        disabled={!enabled}
                        value={config["watchtower.series-recent-count"] ?? "3"}
                        onChange={e => set("watchtower.series-recent-count", e.target.value)} />
                    <p className={styles.hint}>How many of the most recent episodes to keep ready. Default 3.</p>
                </Form.Group>
            )}

            {(scope === "latest-season" || scope === "first-season" || scope === "all-aired") && (
                <>
                    <Form.Group className={styles.section}>
                        <Form.Check
                            type="switch"
                            id="watchtower-season-bundles"
                            label="Prefer season bundles for finished seasons"
                            disabled={!enabled}
                            checked={seasonBundles}
                            onChange={e => set("watchtower.season-bundles", String(e.target.checked))} />
                        <p className={styles.hint}>
                            Warm one season bundle per completed season, a single release that covers the
                            whole season and plays per episode, instead of every episode. Still-airing
                            seasons always use single episodes. Default on.
                        </p>
                    </Form.Group>

                    {seasonBundles && (
                        <Form.Group className={styles.section}>
                            <Form.Check
                                type="switch"
                                id="watchtower-season-bundle-fallback"
                                label="Fall back to episodes when no season bundle is found"
                                disabled={!enabled}
                                checked={bundleFallback}
                                onChange={e => set("watchtower.season-bundle-fallback", String(e.target.checked))} />
                            <p className={styles.hint}>
                                When a finished season has no healthy bundle, warm its individual episodes
                                instead so the season is still covered. The bundle is parked and stops being
                                searched, so this will not keep hitting your indexers. Use "check now" on a
                                parked pack to try for it again. Off by default.
                            </p>
                        </Form.Group>
                    )}

                    {seasonBundles && bundleFallback && (
                        <>
                            <Form.Group className={styles.section}>
                                <Form.Label>Fallback scope</Form.Label>
                                <Form.Select className={styles.input}
                                    disabled={!enabled}
                                    value={bundleFallbackScope}
                                    onChange={e => set("watchtower.season-bundle-fallback-scope", e.target.value)}>
                                    <option value="latest-season">Latest season only</option>
                                    <option value="recent">Recent seasons</option>
                                    <option value="all">All seasons</option>
                                </Form.Select>
                                <p className={styles.hint}>
                                    Which finished seasons fall back to episodes when their bundle is missing.
                                    <b> Latest season</b> covers only the newest season, the one most likely
                                    being watched. <b>Recent seasons</b> covers the last few. <b>All seasons</b>
                                    covers every season. Seasons left out stay a bundle-only search and keep retrying.
                                </p>
                            </Form.Group>

                            {bundleFallbackScope === "recent" && (
                                <Form.Group className={styles.section}>
                                    <Form.Label>Recent season count</Form.Label>
                                    <Form.Control className={styles.input} type="number" min={1} max={100}
                                        disabled={!enabled}
                                        value={config["watchtower.season-bundle-fallback-recent-count"] ?? "2"}
                                        onChange={e => set("watchtower.season-bundle-fallback-recent-count", e.target.value)} />
                                    <p className={styles.hint}>How many of the most recent seasons fall back to episodes. Default 2.</p>
                                </Form.Group>
                            )}

                            <Form.Group className={styles.section}>
                                <Form.Label>Max fallback episodes per season</Form.Label>
                                <Form.Control className={styles.input} type="number" min={1} max={1000}
                                    disabled={!enabled}
                                    value={config["watchtower.season-bundle-fallback-max-episodes"] ?? "50"}
                                    onChange={e => set("watchtower.season-bundle-fallback-max-episodes", e.target.value)} />
                                <p className={styles.hint}>
                                    Caps how many episodes are warmed when a season falls back, so a long
                                    season does not fan out too far. Default 50.
                                </p>
                            </Form.Group>
                        </>
                    )}

                    <Form.Group className={styles.section}>
                        <Form.Label>Max episodes per series</Form.Label>
                        <Form.Control className={styles.input} type="number" min={1} max={1000}
                            disabled={!enabled}
                            value={config["watchtower.series-max-episodes"] ?? "50"}
                            onChange={e => set("watchtower.series-max-episodes", e.target.value)} />
                        <p className={styles.hint}>
                            Caps how many individual episodes are warmed for seasons that aren't bundled
                            (such as the currently-airing one). Season bundles don't count toward this. Default 50.
                        </p>
                    </Form.Group>
                </>
            )}

            <Form.Group className={styles.section}>
                <Form.Label>Junk floor (GB)</Form.Label>
                <Form.Control className={styles.input} type="number" min={0} step={0.1}
                    disabled={!enabled}
                    value={bytesToGb(config["watchtower.size-floor-bytes"])}
                    onChange={e => setGb("watchtower.size-floor-bytes", e.target.value)} />
                <p className={styles.hint}>Ignore releases smaller than this. Default 0.5 GB.</p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Bandwidth ceiling (GB)</Form.Label>
                <Form.Control className={styles.input} type="number" min={0} step={0.5}
                    disabled={!enabled}
                    placeholder="empty = no ceiling"
                    value={bytesToGb(config["watchtower.size-ceiling-bytes"])}
                    onChange={e => setGb("watchtower.size-ceiling-bytes", e.target.value)} />
                <p className={styles.hint}>Ignore releases larger than this. Empty / 0 = no ceiling.</p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Active warm-set cap</Form.Label>
                <Form.Control className={styles.input} type="number" min={1} max={100000}
                    disabled={!enabled}
                    value={config["watchtower.active-set-cap"] ?? "100"}
                    onChange={e => set("watchtower.active-set-cap", e.target.value)} />
                <p className={styles.hint}>
                    How many items the engine keeps actively ready. Beyond this, items are listed but
                    parked until they bubble up. This is what bounds load no matter how big your lists get. Default 100.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Daily resolve budget</Form.Label>
                <Form.Control className={styles.input} type="number" min={0}
                    disabled={!enabled}
                    value={config["watchtower.daily-resolve-budget"] ?? "60"}
                    onChange={e => set("watchtower.daily-resolve-budget", e.target.value)} />
                <p className={styles.hint}>
                    Soft cap on new resolves per day (0 = unlimited; your per-indexer caps always apply).
                    Drips the backlog instead of hammering indexers. Default 60.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Shortlist depth</Form.Label>
                <Form.Control className={styles.input} type="number" min={1} max={5}
                    disabled={!enabled}
                    value={config["watchtower.shortlist-depth"] ?? "2"}
                    onChange={e => set("watchtower.shortlist-depth", e.target.value)} />
                <p className={styles.hint}>One live winner + backups kept per item, for instant failover. Default 2.</p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Grab cap per resolve</Form.Label>
                <Form.Control className={styles.input} type="number" min={1} max={10}
                    disabled={!enabled}
                    value={config["watchtower.grab-cap-per-resolve"] ?? "3"}
                    onChange={e => set("watchtower.grab-cap-per-resolve", e.target.value)} />
                <p className={styles.hint}>
                    Max NZB fetches (the scarce indexer bucket) per item per pass. Keeps resolves grab-thrifty. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>List sync interval (seconds)</Form.Label>
                <Form.Control className={styles.input} type="number" min={60} max={86400}
                    disabled={!enabled}
                    value={config["watchtower.sync-interval-seconds"] ?? "3600"}
                    onChange={e => set("watchtower.sync-interval-seconds", e.target.value)} />
                <p className={styles.hint}>How often remote lists are re-fetched to catch additions/removals. Default 3600.</p>
            </Form.Group>
        </div>
    );
}

export function isWatchtowerSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return [
        "watchtower.enabled",
        "watchtower.profile-token",
        "watchtower.ranking",
        "watchtower.size-floor-bytes",
        "watchtower.size-ceiling-bytes",
        "watchtower.shortlist-depth",
        "watchtower.grab-cap-per-resolve",
        "watchtower.active-set-cap",
        "watchtower.daily-resolve-budget",
        "watchtower.sync-interval-seconds",
        "watchtower.series-scope",
        "watchtower.season-bundles",
        "watchtower.series-max-episodes",
        "watchtower.series-recent-count",
        "watchtower.season-bundle-fallback",
        "watchtower.season-bundle-fallback-scope",
        "watchtower.season-bundle-fallback-recent-count",
        "watchtower.season-bundle-fallback-max-episodes",
    ].some(k => config[k] !== newConfig[k]);
}
