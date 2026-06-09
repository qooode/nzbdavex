import type { Route } from "./+types/route";
import { useEffect, useRef, useState } from "react";
import { useFetcher, useSearchParams, useLocation } from "react-router";
import { Form, Button } from "react-bootstrap";
import styles from "./route.module.css";
import { backendClient, type WatchtowerData, type WatchtowerItem, type WatchtowerSource } from "~/clients/backend-client.server";

const POLL_INTERVAL_MS = 5000;
const PAGE_SIZE = 100;

const SCOPE_OPTIONS: { value: string; label: string }[] = [
    { value: "", label: "Default scope" },
    { value: "latest-season", label: "Latest season" },
    { value: "first-season", label: "First season" },
    { value: "all-aired", label: "All aired seasons" },
    { value: "recent", label: "Recent episodes" },
    { value: "off", label: "Don't expand" },
];

export async function loader({ request }: Route.LoaderArgs) {
    const sp = new URL(request.url).searchParams;
    return await backendClient.getWatchtower({
        state: sp.get("state") ?? undefined,
        q: sp.get("q")?.trim() || undefined,
        sort: sp.get("sort") ?? undefined,
        offset: Number(sp.get("offset")) || 0,
        limit: Number(sp.get("limit")) || PAGE_SIZE,
        statsOnly: sp.get("statsOnly") === "1",
    });
}

export async function action({ request }: Route.ActionArgs) {
    const form = await request.formData();
    const fields: Record<string, string> = {};
    for (const [k, v] of form.entries()) fields[k] = String(v);
    try {
        if (fields.action === "discover-catalogs") {
            const discovered = await backendClient.discoverStremioCatalogs(fields.url ?? "");
            return { ok: true as const, discovered };
        }
        if (fields.action === "bulk-recheck" || fields.action === "bulk-remove") {
            const sub = fields.action === "bulk-recheck" ? "recheck-items" : "remove-items";
            await backendClient.watchtowerMutate({ action: sub, keys: fields.keys ?? "" });
            return { ok: true as const };
        }
        await backendClient.watchtowerMutate(fields);
        return { ok: true as const };
    } catch (e: any) {
        return { ok: false as const, error: e?.message ?? String(e) };
    }
}

export default function Watchtower({ loaderData }: Route.ComponentProps) {
    const addFetcher = useFetcher<typeof action>();
    const discoverFetcher = useFetcher<typeof action>();
    const bulkFetcher = useFetcher<typeof action>();
    const bulkItemFetcher = useFetcher<typeof action>();
    const filterFetcher = useFetcher<typeof action>();
    const moreFetcher = useFetcher<WatchtowerData>();
    const statsFetcher = useFetcher<WatchtowerData>();
    const location = useLocation();
    const [searchParams, setSearchParams] = useSearchParams();

    const stateFilter = searchParams.get("state");
    const urlQuery = searchParams.get("q") ?? "";
    const sortKey = searchParams.get("sort") ?? "default";
    const filtering = stateFilter !== null || urlQuery !== "";

    const discovered = discoverFetcher.data?.ok && "discovered" in discoverFetcher.data
        ? discoverFetcher.data.discovered
        : undefined;
    const discoverError = discoverFetcher.data && discoverFetcher.data.ok === false
        ? discoverFetcher.data.error : undefined;

    const [selected, setSelected] = useState<Set<string>>(new Set());
    const [discoveryDismissed, setDiscoveryDismissed] = useState(false);

    const [items, setItems] = useState<WatchtowerItem[]>(loaderData.items);
    const [total, setTotal] = useState(loaderData.total);
    const [hasMore, setHasMore] = useState(loaderData.hasMore);
    const [stats, setStats] = useState(loaderData.stats);
    const [sources, setSources] = useState(loaderData.sources);
    const [enabled, setEnabled] = useState(loaderData.enabled);

    const [selectedItems, setSelectedItems] = useState<Set<string>>(new Set());
    const [expandedShows, setExpandedShows] = useState<Set<string>>(new Set());
    const selectAllRef = useRef<HTMLInputElement>(null);
    const sentinelRef = useRef<HTMLDivElement>(null);
    const lastOffsetRef = useRef(-1);

    const toggleItem = (key: string) => setSelectedItems(prev => {
        const next = new Set(prev);
        if (next.has(key)) next.delete(key); else next.add(key);
        return next;
    });
    const toggleShow = (key: string) => setExpandedShows(prev => {
        const next = new Set(prev);
        if (next.has(key)) next.delete(key); else next.add(key);
        return next;
    });

    useEffect(() => {
        setItems(loaderData.items);
        setTotal(loaderData.total);
        setHasMore(loaderData.hasMore);
        setStats(loaderData.stats);
        setSources(loaderData.sources);
        setEnabled(loaderData.enabled);
        setSelectedItems(new Set());
        lastOffsetRef.current = -1;
    }, [loaderData]);

    useEffect(() => {
        if (moreFetcher.state === "idle" && moreFetcher.data) {
            const data = moreFetcher.data;
            setItems(prev => {
                const seen = new Set(prev.map(i => i.key));
                return [...prev, ...data.items.filter(i => !seen.has(i.key))];
            });
            setTotal(data.total);
            setHasMore(data.hasMore);
        }
    }, [moreFetcher.state, moreFetcher.data]);

    useEffect(() => {
        if (statsFetcher.state === "idle" && statsFetcher.data) {
            setStats(statsFetcher.data.stats);
            setSources(statsFetcher.data.sources);
            setEnabled(statsFetcher.data.enabled);
        }
    }, [statsFetcher.state, statsFetcher.data]);

    const loadMoreRef = useRef<() => void>(() => {});
    loadMoreRef.current = () => {
        if (!hasMore || moreFetcher.state !== "idle") return;
        const offset = items.length;
        if (offset === lastOffsetRef.current) return;
        lastOffsetRef.current = offset;
        const sp = new URLSearchParams();
        if (stateFilter) sp.set("state", stateFilter);
        if (urlQuery) sp.set("q", urlQuery);
        if (sortKey !== "default") sp.set("sort", sortKey);
        sp.set("offset", String(offset));
        moreFetcher.load(`${location.pathname}?${sp.toString()}`);
    };
    const pollRef = useRef<() => void>(() => {});
    pollRef.current = () => {
        if (statsFetcher.state === "idle") statsFetcher.load(`${location.pathname}?statsOnly=1`);
    };

    useEffect(() => {
        const el = sentinelRef.current;
        if (!el) return;
        const io = new IntersectionObserver(es => { if (es[0].isIntersecting) loadMoreRef.current(); }, { rootMargin: "600px" });
        io.observe(el);
        return () => io.disconnect();
    }, []);

    useEffect(() => {
        if (moreFetcher.state !== "idle" || !hasMore) return;
        const el = sentinelRef.current;
        if (el && el.getBoundingClientRect().top < window.innerHeight) loadMoreRef.current();
    }, [items.length, hasMore, moreFetcher.state]);

    useEffect(() => {
        const t = setInterval(() => pollRef.current(), POLL_INTERVAL_MS);
        return () => clearInterval(t);
    }, []);

    const [queryInput, setQueryInput] = useState(urlQuery);
    useEffect(() => { setQueryInput(urlQuery); }, [urlQuery]);
    useEffect(() => {
        if (queryInput.trim() === urlQuery) return;
        const t = setTimeout(() => {
            setSearchParams(prev => {
                const next = new URLSearchParams(prev);
                const v = queryInput.trim();
                if (v) next.set("q", v); else next.delete("q");
                next.delete("offset");
                return next;
            }, { preventScrollReset: true });
        }, 300);
        return () => clearTimeout(t);
    }, [queryInput, urlQuery, setSearchParams]);

    const updateParams = (mut: (p: URLSearchParams) => void) => setSearchParams(prev => {
        const next = new URLSearchParams(prev);
        mut(next);
        next.delete("offset");
        return next;
    }, { preventScrollReset: true });
    const toggleState = (s: string) => updateParams(p => (stateFilter === s ? p.delete("state") : p.set("state", s)));
    const setSortKey = (s: string) => updateParams(p => (s && s !== "default" ? p.set("sort", s) : p.delete("sort")));
    const clearFilters = () => updateParams(p => { p.delete("state"); p.delete("q"); });

    useEffect(() => {
        if (discovered) {
            setSelected(new Set(discovered.catalogs.map(c => c.url)));
            setDiscoveryDismissed(false);
        }
    }, [discovered]);

    useEffect(() => {
        if (bulkFetcher.state === "idle" && bulkFetcher.data?.ok) {
            setDiscoveryDismissed(true);
        }
    }, [bulkFetcher.state, bulkFetcher.data]);

    const chosenCatalogs = (discovered?.catalogs ?? []).filter(c => selected.has(c.url));
    const sourcesJson = JSON.stringify(chosenCatalogs.map(c => ({
        url: c.url,
        name: discovered?.addonName ? `${discovered.addonName}: ${c.name}` : c.name,
    })));

    const expanders = items.filter(it => it.state === "expander");
    const expanderKeys = new Set(expanders.map(ex => ex.key));
    const childrenByExpander = new Map<string, WatchtowerItem[]>();
    for (const it of items) {
        if (!it.expanderKey || !expanderKeys.has(it.expanderKey)) continue;
        const arr = childrenByExpander.get(it.expanderKey);
        if (arr) arr.push(it); else childrenByExpander.set(it.expanderKey, [it]);
    }
    const orphans = items.filter(it =>
        it.state !== "expander" && (!it.expanderKey || !expanderKeys.has(it.expanderKey)));

    const forceOpenShows = urlQuery !== "";
    const isShowOpen = (key: string) => forceOpenShows || expandedShows.has(key);

    const allVisibleLeafKeys = [
        ...orphans.map(it => it.key),
        ...expanders.flatMap(ex => (childrenByExpander.get(ex.key) ?? []).map(k => k.key)),
    ];
    const allVisibleSelected = allVisibleLeafKeys.length > 0 && allVisibleLeafKeys.every(k => selectedItems.has(k));
    const someVisibleSelected = allVisibleLeafKeys.some(k => selectedItems.has(k));
    const toggleSelectAllVisible = () => setSelectedItems(prev => {
        const next = new Set(prev);
        if (allVisibleSelected) allVisibleLeafKeys.forEach(k => next.delete(k));
        else allVisibleLeafKeys.forEach(k => next.add(k));
        return next;
    });
    const setKeysSelected = (keys: string[], select: boolean) => setSelectedItems(prev => {
        const next = new Set(prev);
        if (select) keys.forEach(k => next.add(k)); else keys.forEach(k => next.delete(k));
        return next;
    });
    const bulkKeysValue = [...selectedItems].join("\n");

    const bulkBusy = bulkItemFetcher.state !== "idle";
    const filterBusy = filterFetcher.state !== "idle";
    const nothingShown = items.length === 0;

    useEffect(() => {
        if (selectAllRef.current) selectAllRef.current.indeterminate = someVisibleSelected && !allVisibleSelected;
    }, [someVisibleSelected, allVisibleSelected]);

    return (
        <div className={styles.page}>
            <div className={styles.header}>
                <div>
                    <h2 className={styles.title}>Watchtower</h2>
                    <div className={styles.subtitle}>
                        Keeps your lists ready. Each title is pre-resolved to a healthy release and
                        re-verified over time, so it's found and ready before you need it. Pointer-only:
                        it stores segment maps, never video.
                    </div>
                </div>
                <div className={styles.stats}>
                    <Stat label="Ready" value={stats.ready} tone="ok" active={stateFilter === "ready"} onClick={() => toggleState("ready")} />
                    <Stat label="Scouting" value={stats.scouting} tone="warn" active={stateFilter === "scouting"} onClick={() => toggleState("scouting")} />
                    <Stat label="Unavailable" value={stats.unavailable} tone="bad" active={stateFilter === "unavailable"} onClick={() => toggleState("unavailable")} />
                    {stats.parked > 0 && (
                        <Stat label="Parked" value={stats.parked} active={stateFilter === "parked"} onClick={() => toggleState("parked")} />
                    )}
                    <Stat label="Shows" value={stats.expanders} active={stateFilter === "expander"} onClick={() => toggleState("expander")} />
                    <Stat label="Total" value={stats.total} active={stateFilter === null} onClick={() => clearFilters()} />
                </div>
            </div>

            {!enabled && (
                <div className="alert alert-warning" role="alert">
                    Watchtower is off. Enable it under Settings, Watchtower to start readying these items.
                    You can still add lists and items now.
                </div>
            )}

            {addFetcher.data && addFetcher.data.ok === false && (
                <div className="alert alert-danger" role="alert">Action failed: {addFetcher.data.error}</div>
            )}

            <section className={styles.panel}>
                <div className={styles.panelHead}>
                    <div className={styles.panelTitle}>Lists</div>
                    <div className={styles.panelHint}>
                        Any list that yields content ids: a Stremio catalog URL, a plain list URL, or
                        manual additions. They merge into one deduped wanted-set.
                    </div>
                </div>

                {sources.length === 0
                    ? <div className={styles.empty}>No lists yet. Add one below.</div>
                    : <div className={styles.list}>{sources.map(s => <SourceRow key={s.id} source={s} />)}</div>}

                <addFetcher.Form method="post" className={styles.addRow}>
                    <input type="hidden" name="action" value="add-source" />
                    <Form.Select name="kind" defaultValue="stremio-catalog" className={styles.selectSm}>
                        <option value="stremio-catalog">Stremio catalog</option>
                        <option value="url-list">URL list</option>
                    </Form.Select>
                    <Form.Control name="name" placeholder="Name (optional)" className={styles.selectSm} />
                    <Form.Control name="url" placeholder="https://addon/catalog/movie/xyz.json" className={styles.inputWide} />
                    <Form.Control name="cap" type="number" min={0} placeholder="cap" className={styles.inputSm} title="Per-list active cap (0 = use default)" />
                    <Form.Select name="seriesScope" defaultValue="" className={styles.selectSm} title="Series scope for this list">
                        {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </Form.Select>
                    <Button type="submit" variant="primary" disabled={addFetcher.state !== "idle"}>Add list</Button>
                </addFetcher.Form>

                <div className={styles.discover}>
                    <div className={styles.panelHint}>
                        Or paste a Stremio addon's <code>manifest.json</code> URL to see its catalogs and pick the ones you want.
                        Each catalog you add becomes its own list.
                    </div>
                    <discoverFetcher.Form method="post" className={styles.addRow}>
                        <input type="hidden" name="action" value="discover-catalogs" />
                        <Form.Control
                            name="url"
                            placeholder="https://addon.example.com/.../manifest.json"
                            className={styles.inputWide}
                        />
                        <Button type="submit" variant="outline-secondary" disabled={discoverFetcher.state !== "idle"}>
                            {discoverFetcher.state !== "idle" ? "Loading…" : "Discover catalogs"}
                        </Button>
                    </discoverFetcher.Form>

                    {discoverError && <div className="alert alert-danger" role="alert">{discoverError}</div>}

                    {discovered && !discoveryDismissed && (
                        <div className={styles.discoverResult}>
                            <div className={styles.discoverHead}>
                                <div className={styles.discoverTitle}>
                                    {discovered.addonName ? `${discovered.addonName} · ` : ""}
                                    {discovered.catalogs.length} catalog{discovered.catalogs.length === 1 ? "" : "s"} found
                                </div>
                                <div className={styles.discoverActions}>
                                    <button type="button" className={styles.linkBtn}
                                        onClick={() => setSelected(new Set(discovered.catalogs.map(c => c.url)))}>select all</button>
                                    <button type="button" className={styles.linkBtn}
                                        onClick={() => setSelected(new Set())}>select none</button>
                                    <button type="button" className={styles.linkBtn}
                                        onClick={() => setDiscoveryDismissed(true)}>close</button>
                                </div>
                            </div>

                            <div className={styles.catList}>
                                {discovered.catalogs.map(cat => (
                                    <label key={cat.url} className={styles.catRow}>
                                        <input
                                            type="checkbox"
                                            checked={selected.has(cat.url)}
                                            onChange={(e) => setSelected(prev => {
                                                const next = new Set(prev);
                                                if (e.target.checked) next.add(cat.url); else next.delete(cat.url);
                                                return next;
                                            })}
                                        />
                                        <span className={styles.kind}>{cat.type}</span>
                                        <span className={styles.catName}>{cat.name}</span>
                                        {cat.extraRequired && (
                                            <span className={styles.metaBad}
                                                title={`This catalog requires "${cat.extraRequired}"; the basic endpoint may return nothing.`}>
                                                needs {cat.extraRequired}
                                            </span>
                                        )}
                                        <span className={`${styles.url} ${styles.catUrl}`} title={cat.url}>{cat.url}</span>
                                    </label>
                                ))}
                            </div>

                            <div className={styles.discoverFoot}>
                                <bulkFetcher.Form method="post" className={styles.addRow}>
                                    <input type="hidden" name="action" value="add-sources" />
                                    <input type="hidden" name="sources" value={sourcesJson} readOnly />
                                    <Form.Select name="seriesScope" defaultValue="" className={styles.selectSm} title="Series scope for these lists">
                                        {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                                    </Form.Select>
                                    <Button type="submit" variant="primary"
                                        disabled={bulkFetcher.state !== "idle" || selected.size === 0}>
                                        {bulkFetcher.state !== "idle" ? "Adding…" : `Add ${selected.size} selected`}
                                    </Button>
                                </bulkFetcher.Form>
                                {bulkFetcher.data && bulkFetcher.data.ok === false && (
                                    <span className={styles.metaBad}>{bulkFetcher.data.error}</span>
                                )}
                            </div>
                        </div>
                    )}
                </div>
            </section>

            <section className={styles.panel}>
                <div className={styles.panelHead}>
                    <div className={styles.panelTitle}>Wanted</div>
                    <div className={styles.panelHint}>
                        Each item is searched once, the biggest healthy release is verified, then
                        re-checked over time. Add one manually by imdb id, or let your lists fill it.
                    </div>
                </div>

                <addFetcher.Form method="post" className={styles.addRow}>
                    <input type="hidden" name="action" value="add-item" />
                    <Form.Select name="type" defaultValue="movie" className={styles.selectSm}>
                        <option value="movie">movie</option>
                        <option value="series">series</option>
                    </Form.Select>
                    <Form.Control name="id" placeholder="tt0111161  (or tt0903747:1:2 for an episode)" className={styles.inputWide} />
                    <Form.Control name="title" placeholder="Title (optional)" className={styles.selectSm} />
                    <Button type="submit" variant="primary" disabled={addFetcher.state !== "idle"}>Add item</Button>
                </addFetcher.Form>

                {stats.total > 0 && (
                    <div className={styles.toolbar}>
                        <label className={styles.selectAll} title="Select all items shown">
                            <input
                                ref={selectAllRef}
                                type="checkbox"
                                checked={allVisibleSelected}
                                onChange={toggleSelectAllVisible}
                                disabled={allVisibleLeafKeys.length === 0}
                            />
                            All
                        </label>
                        <Form.Control
                            value={queryInput}
                            onChange={e => setQueryInput(e.target.value)}
                            placeholder="Search title or id…"
                            className={styles.search}
                        />
                        <Form.Select
                            value={sortKey}
                            onChange={e => setSortKey(e.target.value)}
                            className={styles.sortSelect}
                            title="Sort wanted items"
                        >
                            <option value="default">Sort: recent</option>
                            <option value="status">Status (issues first)</option>
                            <option value="title">Title A–Z</option>
                            <option value="recheck">Re-check soonest</option>
                        </Form.Select>
                        {filtering && (
                            <button type="button" className={styles.linkBtn} onClick={clearFilters}>clear</button>
                        )}
                        {stats.unavailable > 0 && (
                            <filterFetcher.Form method="post" className={styles.toolbarRight}>
                                <input type="hidden" name="action" value="recheck-by-filter" />
                                <input type="hidden" name="state" value="unavailable" />
                                <button type="submit" className={styles.linkBtn} disabled={filterBusy}>
                                    re-check {stats.unavailable} unavailable
                                </button>
                            </filterFetcher.Form>
                        )}
                    </div>
                )}

                {filtering && total > 0 && (
                    <div className={styles.selectionBar}>
                        <span className={styles.selCount}>{Math.min(items.length, total)}</span>
                        <span className={styles.selLabel}>of {total} shown</span>
                        <div className={styles.selActions}>
                            <filterFetcher.Form method="post">
                                <input type="hidden" name="action" value="recheck-by-filter" />
                                {stateFilter && <input type="hidden" name="state" value={stateFilter} />}
                                {urlQuery && <input type="hidden" name="q" value={urlQuery} />}
                                <Button type="submit" size="sm" variant="secondary" disabled={filterBusy}>
                                    {filterBusy ? "Working…" : `Re-check all ${total}`}
                                </Button>
                            </filterFetcher.Form>
                            <filterFetcher.Form
                                method="post"
                                onSubmit={e => {
                                    if (!window.confirm(`Remove all ${total} matching item${total === 1 ? "" : "s"} from Watchtower?`))
                                        e.preventDefault();
                                }}
                            >
                                <input type="hidden" name="action" value="remove-by-filter" />
                                {stateFilter && <input type="hidden" name="state" value={stateFilter} />}
                                {urlQuery && <input type="hidden" name="q" value={urlQuery} />}
                                <Button type="submit" size="sm" variant="danger" disabled={filterBusy}>Remove all {total}</Button>
                            </filterFetcher.Form>
                        </div>
                    </div>
                )}

                {selectedItems.size > 0 && (
                    <div className={styles.selectionBar}>
                        <span className={styles.selCount}>{selectedItems.size}</span>
                        <span className={styles.selLabel}>selected</span>
                        <div className={styles.selActions}>
                            <bulkItemFetcher.Form method="post">
                                <input type="hidden" name="action" value="bulk-recheck" />
                                <input type="hidden" name="keys" value={bulkKeysValue} readOnly />
                                <Button type="submit" size="sm" variant="secondary" disabled={bulkBusy}>
                                    {bulkBusy ? "Working…" : "Re-check"}
                                </Button>
                            </bulkItemFetcher.Form>
                            <bulkItemFetcher.Form
                                method="post"
                                onSubmit={e => {
                                    if (!window.confirm(`Remove ${selectedItems.size} item${selectedItems.size === 1 ? "" : "s"} from Watchtower?`))
                                        e.preventDefault();
                                }}
                            >
                                <input type="hidden" name="action" value="bulk-remove" />
                                <input type="hidden" name="keys" value={bulkKeysValue} readOnly />
                                <Button type="submit" size="sm" variant="danger" disabled={bulkBusy}>Remove</Button>
                            </bulkItemFetcher.Form>
                            <button type="button" className={styles.linkBtn} onClick={() => setSelectedItems(new Set())}>Clear</button>
                        </div>
                    </div>
                )}

                {(bulkItemFetcher.data && bulkItemFetcher.data.ok === false) && (
                    <div className="alert alert-danger" role="alert">Bulk action failed: {bulkItemFetcher.data.error}</div>
                )}
                {(filterFetcher.data && filterFetcher.data.ok === false) && (
                    <div className="alert alert-danger" role="alert">Bulk action failed: {filterFetcher.data.error}</div>
                )}

                {nothingShown
                    ? <div className={styles.empty}>{filtering ? "No items match." : "Nothing wanted yet."}</div>
                    : <div className={styles.list}>
                        {expanders.map(ex => (
                            <ExpanderGroup
                                key={ex.key}
                                expander={ex}
                                episodes={childrenByExpander.get(ex.key) ?? []}
                                expanded={isShowOpen(ex.key)}
                                canToggle={!forceOpenShows}
                                onToggle={() => toggleShow(ex.key)}
                                selectedKeys={selectedItems}
                                onToggleSelect={toggleItem}
                                onSelectMany={setKeysSelected}
                            />
                        ))}
                        {orphans.map(it => (
                            <ItemRow
                                key={it.key}
                                item={it}
                                selected={selectedItems.has(it.key)}
                                onToggleSelect={toggleItem}
                            />
                        ))}
                      </div>}

                <div ref={sentinelRef} />
                {moreFetcher.state !== "idle" && <div className={styles.empty}>Loading more…</div>}
            </section>
        </div>
    );
}

function SourceRow({ source }: { source: WatchtowerSource }) {
    const fetcher = useFetcher();
    const label = sourceLabel(source);
    const host = source.url ? hostOf(source.url) : "";
    return (
        <div className={`${styles.row} ${source.enabled ? "" : styles.dimmed}`}>
            <div className={styles.rowMain}>
                <span className={styles.kind}>{kindLabel(source.kind)}</span>
                <div className={styles.itemTitleWrap}>
                    <div className={styles.itemTitle} title={source.url ?? undefined}>{label}</div>
                    {host && host !== label && <div className={styles.itemSub}><span>{host}</span></div>}
                </div>
            </div>
            <div className={styles.rowActions}>
                {source.cap > 0 && <span className={styles.meta}>cap {source.cap}</span>}
                {source.lastSyncError
                    ? <span className={styles.metaBad} title={source.lastSyncError}>sync error</span>
                    : source.lastSyncedAtUnix
                        ? <span className={styles.metaOk}>synced {formatAge(source.lastSyncedAtUnix)}</span>
                        : <span className={styles.meta}>not synced yet</span>}
                {source.url && (
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="sync-source" />
                        <input type="hidden" name="id" value={source.id} />
                        <button type="submit" className={styles.linkBtn} disabled={fetcher.state !== "idle"}>sync now</button>
                    </fetcher.Form>
                )}
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="set-source-scope" />
                    <input type="hidden" name="id" value={source.id} />
                    <Form.Select
                        name="seriesScope"
                        defaultValue={source.seriesScope ?? ""}
                        className={styles.selectSm}
                        title="Series scope for this list"
                        onChange={(e) => e.currentTarget.form?.requestSubmit()}
                    >
                        {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </Form.Select>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="toggle-source" />
                    <input type="hidden" name="id" value={source.id} />
                    <input type="hidden" name="enabled" value={String(!source.enabled)} />
                    <button type="submit" className={styles.linkBtn}>{source.enabled ? "disable" : "enable"}</button>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="remove-source" />
                    <input type="hidden" name="id" value={source.id} />
                    <button type="submit" className={`${styles.linkBtn} ${styles.linkDanger}`}>remove</button>
                </fetcher.Form>
            </div>
        </div>
    );
}

function ItemRow({ item, selected, onToggleSelect }: {
    item: WatchtowerItem;
    selected: boolean;
    onToggleSelect: (key: string) => void;
}) {
    const fetcher = useFetcher<typeof action>();
    const pending = fetcher.formData?.get("action");
    const removing = pending === "remove-item";
    const checking = pending === "recheck-item";
    const error = fetcher.data && fetcher.data.ok === false ? fetcher.data.error : null;
    return (
        <div className={`${styles.row} ${removing ? styles.dimmed : ""} ${selected ? styles.rowSelected : ""}`}>
            <div className={styles.rowMain}>
                <input
                    type="checkbox"
                    className={styles.rowCheck}
                    checked={selected}
                    onChange={() => onToggleSelect(item.key)}
                    aria-label={`Select ${item.title}`}
                />
                <StateChip state={item.state} />
                <div className={styles.itemTitleWrap}>
                    <div className={styles.itemTitle} title={item.title}>{item.title}</div>
                    <div className={styles.itemSub}>
                        <span className={styles.kind}>{item.type === "season" ? "season bundle" : item.type}</span>
                        <span className={styles.mono}>{item.contentId}</span>
                        {item.provenanceCount > 1 && <span>on {item.provenanceCount} lists</span>}
                        {item.state === "ready" && <>
                            {item.winnerTitle && <span className={styles.rel} title={item.winnerTitle}>{item.winnerTitle}</span>}
                            <span>{formatBytes(item.winnerSize)} · {item.shortlistCount} pointer{item.shortlistCount === 1 ? "" : "s"}</span>
                            {item.lastVerifiedAtUnix && <span>verified {formatAge(item.lastVerifiedAtUnix)}</span>}
                            {item.nextCheckAtUnix && <span>re-checks {formatWhen(item.nextCheckAtUnix)}</span>}
                        </>}
                        {item.state === "unavailable" && <>
                            {item.failReason && <span>{item.failReason}</span>}
                            {item.nextCheckAtUnix && <span>retries {formatWhen(item.nextCheckAtUnix)}</span>}
                        </>}
                        {item.state === "parked" && item.failReason && <span>{item.failReason}</span>}
                        {item.state === "scouting" && <span>searching…</span>}
                    </div>
                </div>
            </div>
            <div className={styles.rowActions}>
                {error && <span className={styles.metaBad} title={error}>failed — retry</span>}
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="recheck-item" />
                    <input type="hidden" name="key" value={item.key} />
                    <button type="submit" className={styles.linkBtn} disabled={fetcher.state !== "idle"}>{checking ? "checking…" : "check now"}</button>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="remove-item" />
                    <input type="hidden" name="key" value={item.key} />
                    <button type="submit" className={`${styles.linkBtn} ${styles.linkDanger}`} disabled={fetcher.state !== "idle"}>{removing ? "removing…" : "remove"}</button>
                </fetcher.Form>
            </div>
        </div>
    );
}

function ExpanderGroup({ expander, episodes, expanded, canToggle, onToggle, selectedKeys, onToggleSelect, onSelectMany }: {
    expander: WatchtowerItem;
    episodes: WatchtowerItem[];
    expanded: boolean;
    canToggle: boolean;
    onToggle: () => void;
    selectedKeys: Set<string>;
    onToggleSelect: (key: string) => void;
    onSelectMany: (keys: string[], select: boolean) => void;
}) {
    const fetcher = useFetcher<typeof action>();
    const seriesCheckRef = useRef<HTMLInputElement>(null);
    const pending = fetcher.formData?.get("action");
    const removing = pending === "remove-item";
    const checking = pending === "recheck-item";
    const error = fetcher.data && fetcher.data.ok === false ? fetcher.data.error : null;
    const countable = episodes.filter(c => c.state !== "parked");
    const ready = countable.filter(c => c.state === "ready").length;
    const unavailable = countable.filter(c => c.state === "unavailable").length;
    const sorted = [...episodes].sort((a, b) => a.contentId.localeCompare(b.contentId, undefined, { numeric: true }));

    const childKeys = episodes.map(c => c.key);
    const allSel = childKeys.length > 0 && childKeys.every(k => selectedKeys.has(k));
    const someSel = childKeys.some(k => selectedKeys.has(k));
    const pct = countable.length > 0 ? Math.round((ready / countable.length) * 100) : 0;

    useEffect(() => {
        if (seriesCheckRef.current) seriesCheckRef.current.indeterminate = someSel && !allSel;
    }, [someSel, allSel]);

    return (
        <div className={styles.group}>
            <div
                className={`${styles.showHead} ${canToggle ? "" : styles.showHeadStatic} ${expanded ? styles.showHeadOpen : ""} ${removing ? styles.dimmed : ""}`}
                role="button"
                tabIndex={0}
                aria-expanded={expanded}
                onClick={() => canToggle && onToggle()}
                onKeyDown={e => {
                    if (canToggle && (e.key === "Enter" || e.key === " ") && e.target === e.currentTarget) {
                        e.preventDefault();
                        onToggle();
                    }
                }}
            >
                <span className={styles.showCheck} onClick={e => e.stopPropagation()}>
                    <input
                        ref={seriesCheckRef}
                        type="checkbox"
                        checked={allSel}
                        disabled={childKeys.length === 0}
                        onChange={() => onSelectMany(childKeys, !allSel)}
                        aria-label={`Select loaded episodes of ${expander.title}`}
                    />
                </span>
                <svg className={`${styles.chev} ${expanded ? styles.chevOpen : ""}`} width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true">
                    <path d="M6 4l4 4-4 4" stroke="currentColor" strokeWidth="1.75" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
                <span className={`${styles.chip} ${styles.chipShow}`}>Show</span>
                <div className={styles.showInfo}>
                    <div className={styles.itemTitle} title={expander.title}>{expander.title}</div>
                    <div className={styles.itemSub}>
                        <span className={styles.mono}>{expander.contentId}</span>
                        {countable.length === 0
                            ? <span>expanding…</span>
                            : <span className={styles.readyLine}>
                                <span className={styles.meter}><span className={styles.meterFill} style={{ width: `${pct}%` }} /></span>
                                <span>{ready}/{countable.length} ready</span>
                              </span>}
                        {unavailable > 0 && <span className={styles.metaBad}>{unavailable} unavailable</span>}
                    </div>
                </div>
                <div className={styles.rowActions} onClick={e => e.stopPropagation()}>
                    {error && <span className={styles.metaBad} title={error}>failed — retry</span>}
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="recheck-item" />
                        <input type="hidden" name="key" value={expander.key} />
                        <button type="submit" className={styles.linkBtn} disabled={fetcher.state !== "idle"}>{checking ? "checking…" : "check now"}</button>
                    </fetcher.Form>
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="remove-item" />
                        <input type="hidden" name="key" value={expander.key} />
                        <button type="submit" className={`${styles.linkBtn} ${styles.linkDanger}`} disabled={fetcher.state !== "idle"}>{removing ? "removing…" : "remove"}</button>
                    </fetcher.Form>
                </div>
            </div>
            {expanded && sorted.length > 0 && (
                <div className={styles.children}>
                    {sorted.map(c => <ItemRow key={c.key} item={c} selected={selectedKeys.has(c.key)} onToggleSelect={onToggleSelect} />)}
                </div>
            )}
        </div>
    );
}

function StateChip({ state }: { state: string }) {
    const cls = state === "ready" ? styles.chipReady
        : state === "unavailable" ? styles.chipBad
        : state === "parked" ? styles.chipParked
        : state === "expander" ? styles.chipShow
        : styles.chipScouting;
    const label = state === "ready" ? "Ready"
        : state === "unavailable" ? "Unavailable"
        : state === "parked" ? "Parked"
        : state === "expander" ? "Show"
        : "Scouting";
    return <span className={`${styles.chip} ${cls}`}>{label}</span>;
}

function Stat({ label, value, tone, active, onClick }: { label: string, value: number, tone?: "ok" | "warn" | "bad", active?: boolean, onClick?: () => void }) {
    const toneClass = tone === "ok" ? styles.statOk
        : tone === "warn" ? styles.statWarn
        : tone === "bad" ? styles.statBad
        : "";
    return (
        <button type="button" className={`${styles.stat} ${active ? styles.statActive : ""}`} onClick={onClick}>
            <div className={`${styles.statValue} ${toneClass}`}>{value}</div>
            <div className={styles.statLabel}>{label}</div>
        </button>
    );
}

function formatBytes(bytes: number): string {
    if (bytes <= 0) return "-";
    const u = ["B", "KB", "MB", "GB", "TB"];
    let i = 0;
    let v = bytes;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2)} ${u[i]}`;
}

function formatAge(unixSeconds: number): string {
    const age = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (age < 5) return "just now";
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}

function formatWhen(unixSeconds: number): string {
    const d = Math.floor(unixSeconds - Date.now() / 1000);
    if (d <= 0) return "soon";
    if (d < 60) return `in ${d}s`;
    if (d < 3600) return `in ${Math.floor(d / 60)}m`;
    if (d < 86400) return `in ${Math.floor(d / 3600)}h`;
    return `in ${Math.floor(d / 86400)}d`;
}

function kindLabel(kind: string): string {
    if (kind === "stremio-catalog") return "catalog";
    if (kind === "url-list") return "url list";
    return kind;
}

function titleCase(value: string): string {
    return value
        .replace(/[-_]+/g, " ")
        .replace(/\s+/g, " ")
        .trim()
        .split(" ")
        .map(w => (w ? w[0].toUpperCase() + w.slice(1) : w))
        .join(" ");
}

function hostOf(raw: string): string {
    try {
        return new URL(raw).hostname.replace(/^www\./, "");
    } catch {
        return "";
    }
}

function labelFromUrl(raw: string): string {
    let parsed: URL;
    try {
        parsed = new URL(raw);
    } catch {
        return raw;
    }
    const parts = parsed.pathname.split("/").filter(Boolean).map(p => {
        try { return decodeURIComponent(p); } catch { return p; }
    });
    const ci = parts.indexOf("catalog");
    if (ci >= 0 && parts.length > ci + 2) {
        const type = parts[ci + 1];
        const id = parts[ci + 2].replace(/\.json$/i, "");
        const pretty = titleCase(id);
        return type ? `${pretty} · ${titleCase(type)}` : pretty;
    }
    const last = parts.length ? parts[parts.length - 1].replace(/\.json$/i, "") : "";
    return last ? titleCase(last) : hostOf(raw);
}

function sourceLabel(source: WatchtowerSource): string {
    const name = (source.name ?? "").trim();
    const url = (source.url ?? "").trim();
    if (name && name !== url) return name;
    if (url) return labelFromUrl(url);
    return name || "Untitled list";
}
