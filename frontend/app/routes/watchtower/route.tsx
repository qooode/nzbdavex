import type { Route } from "./+types/route";
import { useEffect } from "react";
import { useFetcher, useRevalidator } from "react-router";
import { Form, Button } from "react-bootstrap";
import styles from "./route.module.css";
import { backendClient, type WatchtowerItem, type WatchtowerSource } from "~/clients/backend-client.server";

const POLL_INTERVAL_MS = 5000;

export async function loader() {
    return await backendClient.getWatchtower();
}

export async function action({ request }: Route.ActionArgs) {
    const form = await request.formData();
    const fields: Record<string, string> = {};
    for (const [k, v] of form.entries()) fields[k] = String(v);
    try {
        await backendClient.watchtowerMutate(fields);
        return { ok: true as const };
    } catch (e: any) {
        return { ok: false as const, error: e?.message ?? String(e) };
    }
}

export default function Watchtower({ loaderData }: Route.ComponentProps) {
    const { enabled, sources, items, stats } = loaderData;
    const addFetcher = useFetcher<typeof action>();
    const revalidator = useRevalidator();

    useEffect(() => {
        const t = setInterval(() => revalidator.revalidate(), POLL_INTERVAL_MS);
        return () => clearInterval(t);
    }, [revalidator]);

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
                    <Stat label="Ready" value={stats.ready} tone="ok" />
                    <Stat label="Scouting" value={stats.scouting} tone="warn" />
                    <Stat label="Unavailable" value={stats.unavailable} tone="bad" />
                    <Stat label="Total" value={stats.total} />
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
                    <Button type="submit" variant="primary" disabled={addFetcher.state !== "idle"}>Add list</Button>
                </addFetcher.Form>
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

                {items.length === 0
                    ? <div className={styles.empty}>Nothing wanted yet.</div>
                    : <div className={styles.list}>{items.map(it => <ItemRow key={it.key} item={it} />)}</div>}
            </section>
        </div>
    );
}

function SourceRow({ source }: { source: WatchtowerSource }) {
    const fetcher = useFetcher();
    return (
        <div className={`${styles.row} ${source.enabled ? "" : styles.dimmed}`}>
            <div className={styles.rowMain}>
                <span className={styles.kind}>{source.kind}</span>
                <span className={styles.name}>{source.name}</span>
                {source.url && <span className={styles.url} title={source.url}>{source.url}</span>}
            </div>
            <div className={styles.rowActions}>
                {source.lastSyncError
                    ? <span className={styles.metaBad} title={source.lastSyncError}>sync error</span>
                    : source.lastSyncedAtUnix
                        ? <span className={styles.metaOk}>synced {formatAge(source.lastSyncedAtUnix)}</span>
                        : <span className={styles.meta}>not synced yet</span>}
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

function ItemRow({ item }: { item: WatchtowerItem }) {
    const fetcher = useFetcher();
    return (
        <div className={styles.row}>
            <div className={styles.rowMain}>
                <StateChip state={item.state} />
                <div className={styles.itemTitleWrap}>
                    <div className={styles.itemTitle} title={item.title}>{item.title}</div>
                    <div className={styles.itemSub}>
                        <span className={styles.kind}>{item.type}</span>
                        <span className={styles.mono}>{item.contentId}</span>
                        {item.state === "ready" && item.winnerTitle && (
                            <span title={item.winnerTitle}>
                                {formatBytes(item.winnerSize)} · {item.shortlistCount} pointer{item.shortlistCount === 1 ? "" : "s"}
                                {item.lastVerifiedAtUnix ? ` · verified ${formatAge(item.lastVerifiedAtUnix)}` : ""}
                            </span>
                        )}
                        {item.state === "unavailable" && item.failReason && <span>{item.failReason}</span>}
                    </div>
                </div>
            </div>
            <fetcher.Form method="post" className={styles.rowActions}>
                <input type="hidden" name="action" value="remove-item" />
                <input type="hidden" name="key" value={item.key} />
                <button type="submit" className={`${styles.linkBtn} ${styles.linkDanger}`}>remove</button>
            </fetcher.Form>
        </div>
    );
}

function StateChip({ state }: { state: string }) {
    const cls = state === "ready" ? styles.chipReady
        : state === "unavailable" ? styles.chipBad
        : state === "parked" ? styles.chipParked
        : styles.chipScouting;
    const label = state === "ready" ? "Ready"
        : state === "unavailable" ? "Unavailable"
        : state === "parked" ? "Parked"
        : "Scouting";
    return <span className={`${styles.chip} ${cls}`}>{label}</span>;
}

function Stat({ label, value, tone }: { label: string, value: number, tone?: "ok" | "warn" | "bad" }) {
    const toneClass = tone === "ok" ? styles.statOk
        : tone === "warn" ? styles.statWarn
        : tone === "bad" ? styles.statBad
        : "";
    return (
        <div className={styles.stat}>
            <div className={`${styles.statValue} ${toneClass}`}>{value}</div>
            <div className={styles.statLabel}>{label}</div>
        </div>
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
