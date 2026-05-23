import { redirect } from "react-router";
import type { Route } from "./+types/route";
import { useCallback, useEffect, useMemo, useState } from "react";
import styles from "./route.module.css";
import { backendClient, type WatchdogEntry, type WatchdogOutcome } from "~/clients/backend-client.server";

const POLL_INTERVAL_MS = 3000;

export async function loader() {
    const [config, entries] = await Promise.all([
        backendClient.getConfig(["play.watchdog-enabled"]),
        backendClient.getWatchdogEntries(200),
    ]);
    const enabledRaw = config.find(x => x.configName === "play.watchdog-enabled")?.configValue ?? "true";
    const isEnabled = enabledRaw.toLowerCase() === "true";
    if (!isEnabled) {
        return redirect("/queue");
    }
    return { entries };
}

type FilterKey = "all" | "live" | "resolved" | "failed" | "excluded";

export default function Watchdog({ loaderData }: Route.ComponentProps) {
    const [attempts, setAttempts] = useState<WatchdogEntry[]>(loaderData.entries);
    const [autoRefresh, setAutoRefresh] = useState(true);
    const [filter, setFilter] = useState<FilterKey>("all");
    const [refreshing, setRefreshing] = useState(false);
    const [clearing, setClearing] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const refresh = useCallback(async (silent: boolean = false) => {
        if (!silent) setRefreshing(true);
        try {
            const r = await fetch("/settings/watchdog-attempts?limit=200");
            if (!r.ok) throw new Error(`HTTP ${r.status}`);
            const data = await r.json();
            const next: WatchdogEntry[] = data.entries ?? [];
            setAttempts(prev => attemptsEqual(prev, next) ? prev : next);
            setError(null);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            if (!silent) setRefreshing(false);
        }
    }, []);

    const clearAll = useCallback(async () => {
        if (!window.confirm("Permanently delete all watchdog entries? This can't be undone.")) return;
        setClearing(true);
        try {
            const r = await fetch("/settings/watchdog-attempts", { method: "POST" });
            if (!r.ok) throw new Error(`HTTP ${r.status}`);
            setAttempts([]);
            setError(null);
        } catch (e: any) {
            setError(e?.message ?? String(e));
        } finally {
            setClearing(false);
        }
    }, []);

    useEffect(() => {
        if (!autoRefresh) return;
        let cancelled = false;
        let timer: ReturnType<typeof setTimeout> | null = null;
        const loop = async () => {
            if (cancelled) return;
            await refresh(true);
            if (cancelled) return;
            timer = setTimeout(loop, POLL_INTERVAL_MS);
        };
        timer = setTimeout(loop, POLL_INTERVAL_MS);
        return () => {
            cancelled = true;
            if (timer) clearTimeout(timer);
        };
    }, [autoRefresh, refresh]);

    const groups = useMemo(() => groupByClick(attempts), [attempts]);
    const filteredGroups = useMemo(() => groups.filter(g => matchesFilter(g, filter)), [groups, filter]);
    const stats = useMemo(() => computeStats(groups), [groups]);

    return (
        <div className={styles.page}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>
                        <h2 className={styles.title}>Watchdog</h2>
                        <div className={styles.subtitle}>
                            Live playback resolution log. Persisted across restarts.
                        </div>
                    </div>
                    <div className={styles.controls}>
                        <button
                            type="button"
                            className={`${styles.toolbarBtn} ${styles.liveBtn} ${autoRefresh ? styles.liveBtnOn : ""}`}
                            onClick={() => setAutoRefresh(v => !v)}
                            title={autoRefresh ? "Auto-refresh on. Click to pause." : "Auto-refresh paused. Click to resume."}>
                            <span className={`${styles.liveDot} ${autoRefresh ? styles.liveDotOn : ""}`} />
                            {autoRefresh ? (refreshing ? "Refreshing…" : "Live") : "Paused"}
                        </button>
                        <button
                            type="button"
                            className={styles.toolbarBtn}
                            onClick={() => refresh()}
                            disabled={refreshing || clearing}
                            title="Refresh now.">
                            <svg
                                className={`${styles.toolbarIcon} ${refreshing ? styles.spinning : ""}`}
                                viewBox="0 0 16 16"
                                fill="currentColor"
                                aria-hidden="true">
                                <path d="M8 3a5 5 0 1 0 4.546 2.914.5.5 0 0 1 .908-.417A6 6 0 1 1 8 2v1z" />
                                <path d="M8 4.466V.534a.25.25 0 0 1 .41-.192l2.36 1.966c.12.1.12.284 0 .384L8.41 4.658A.25.25 0 0 1 8 4.466z" />
                            </svg>
                            Refresh
                        </button>
                        <button
                            type="button"
                            className={`${styles.toolbarBtn} ${styles.toolbarBtnDanger}`}
                            onClick={clearAll}
                            disabled={groups.length === 0 || clearing}
                            title="Permanently delete all watchdog entries.">
                            <svg
                                className={styles.toolbarIcon}
                                viewBox="0 0 16 16"
                                fill="currentColor"
                                aria-hidden="true">
                                <path d="M5.5 5.5A.5.5 0 0 1 6 6v6a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5zm2.5 0a.5.5 0 0 1 .5.5v6a.5.5 0 0 1-1 0V6a.5.5 0 0 1 .5-.5zm3 .5a.5.5 0 0 0-1 0v6a.5.5 0 0 0 1 0V6z" />
                                <path fillRule="evenodd" d="M14.5 3a1 1 0 0 1-1 1H13v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V4h-.5a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1H6a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1h3.5a1 1 0 0 1 1 1v1zM4.118 4 4 4.059V13a1 1 0 0 0 1 1h6a1 1 0 0 0 1-1V4.059L11.882 4H4.118zM2.5 3v-1h11v1h-11z" />
                            </svg>
                            {clearing ? "Clearing…" : "Clear log"}
                        </button>
                    </div>
                </div>

                <div className={styles.statsBar}>
                    <Stat label="Clicks" value={stats.total} />
                    <Stat label="Resolved" value={stats.resolved} tone="ok" />
                    <Stat label="Failed" value={stats.failed} tone="bad" />
                    <Stat label="In flight" value={stats.inFlight} tone="warn" />
                </div>

                <div className={styles.filterBar}>
                    <FilterChip active={filter === "all"} onClick={() => setFilter("all")} count={groups.length}>All</FilterChip>
                    <FilterChip active={filter === "live"} onClick={() => setFilter("live")} count={stats.inFlight}>Live</FilterChip>
                    <FilterChip active={filter === "resolved"} onClick={() => setFilter("resolved")} count={stats.resolved}>Resolved</FilterChip>
                    <FilterChip active={filter === "failed"} onClick={() => setFilter("failed")} count={stats.failed}>Failed</FilterChip>
                    <FilterChip active={filter === "excluded"} onClick={() => setFilter("excluded")} count={stats.excluded}>Excluded</FilterChip>
                </div>

                {error && <div className={styles.errorBox}>Could not load: {error}</div>}
            </div>

            {filteredGroups.length === 0 ? (
                <div className={styles.emptyState}>
                    {groups.length === 0
                        ? "No watchdog entries recorded yet. Click Play in your client to see live activity here."
                        : "No clicks match this filter."}
                </div>
            ) : (
                <div className={styles.clickList}>
                    {filteredGroups.map(g => <ClickCard key={g.clickId} group={g} />)}
                </div>
            )}
        </div>
    );
}

function ClickCard({ group }: { group: ClickGroup }) {
    const status: "win" | "loss" | "inflight" =
        group.hasWinner ? "win" : group.allResolved ? "loss" : "inflight";
    const winner = group.attempts.find(a => a.isWinner);

    return (
        <div className={styles.clickCard}>
            <div className={styles.clickHeader}>
                <div className={styles.clickHeaderMain}>
                    <StatusPill status={status} />
                    <div className={styles.clickTitle} title={group.requestedTitle}>{group.requestedTitle}</div>
                </div>
                <div className={styles.clickHeaderMeta}>
                    <span className={styles.metaBadge}>{group.contentType}</span>
                    <span className={styles.metaBadge}>{group.attempts.length} attempt{group.attempts.length === 1 ? "" : "s"}</span>
                    <span className={styles.timestamp} title={new Date(group.firstAt * 1000).toLocaleString()}>
                        {formatAge(group.firstAt)}
                    </span>
                </div>
            </div>

            {winner && (
                <div className={styles.winnerLine}>
                    Resolved via <span className={styles.winnerIndexer}>{winner.indexerName}</span>
                    <span className={styles.winnerDot}>·</span>
                    <span className={styles.winnerDuration}>{winner.durationMs}ms</span>
                    {winner.size > 0 && <>
                        <span className={styles.winnerDot}>·</span>
                        <span>{formatBytes(winner.size)}</span>
                    </>}
                </div>
            )}

            <div className={styles.attemptTableWrap}>
                <table className={styles.attemptTable}>
                    <thead>
                        <tr>
                            <th className={styles.colRank}>#</th>
                            <th className={styles.colCandidate}>Candidate</th>
                            <th className={styles.colIndexer}>Indexer</th>
                            <th className={styles.colProvider}>Provider</th>
                            <th className={styles.colSize}>Size</th>
                            <th className={styles.colOutcome}>Outcome</th>
                            <th className={styles.colReason}>Reason</th>
                            <th className={styles.colDuration}>Took</th>
                        </tr>
                    </thead>
                    <tbody>
                        {group.attempts.map((a, i) => (
                            <tr key={i} className={a.isWinner ? styles.winnerRow : undefined}>
                                <td className={styles.colRank}>{a.rankIndex + 1}</td>
                                <td className={styles.colCandidate} title={a.candidateTitle}>{a.candidateTitle || "—"}</td>
                                <td className={styles.colIndexer}>{a.indexerName || "—"}</td>
                                <td className={styles.colProvider} title={a.providerHost ?? undefined}>{a.providerNickname?.trim() || formatProviderShort(a.providerHost)}</td>
                                <td className={styles.colSize}>{formatBytes(a.size)}</td>
                                <td className={styles.colOutcome}>
                                    <OutcomeBadge outcome={a.outcome} winner={a.isWinner} />
                                </td>
                                <td className={styles.colReason} title={a.failReason ?? undefined}>{a.failReason ?? "—"}</td>
                                <td className={styles.colDuration}>{a.durationMs}ms</td>
                            </tr>
                        ))}
                    </tbody>
                </table>

                <div className={styles.attemptCards}>
                    {group.attempts.map((a, i) => (
                        <div key={i} className={`${styles.attemptCard} ${a.isWinner ? styles.attemptCardWinner : ""}`}>
                            <div className={styles.attemptCardTop}>
                                <span className={styles.attemptRank}>#{a.rankIndex + 1}</span>
                                <span className={styles.attemptIndexer} title={a.indexerName}>{a.indexerName || "—"}</span>
                                <OutcomeBadge outcome={a.outcome} winner={a.isWinner} />
                            </div>
                            <div className={styles.attemptCardTitle} title={a.candidateTitle}>{a.candidateTitle || "—"}</div>
                            <div className={styles.attemptCardMeta}>
                                <span title={a.providerHost ?? undefined}>📡 {a.providerNickname?.trim() || formatProviderShort(a.providerHost)}</span>
                                <span className={styles.attemptCardMetaDot}>·</span>
                                <span>{formatBytes(a.size)}</span>
                                <span className={styles.attemptCardMetaDot}>·</span>
                                <span>{a.durationMs}ms</span>
                            </div>
                            {a.failReason && <div className={styles.attemptCardReason}>{a.failReason}</div>}
                        </div>
                    ))}
                </div>
            </div>
        </div>
    );
}

function Stat({ label, value, tone }: { label: string, value: number, tone?: "ok" | "bad" | "warn" }) {
    const toneClass = tone === "ok" ? styles.statValueOk
        : tone === "bad" ? styles.statValueBad
        : tone === "warn" ? styles.statValueWarn
        : "";
    return (
        <div className={styles.stat}>
            <div className={`${styles.statValue} ${toneClass}`}>{value}</div>
            <div className={styles.statLabel}>{label}</div>
        </div>
    );
}

function FilterChip({ active, onClick, count, children }: { active: boolean, onClick: () => void, count: number, children: React.ReactNode }) {
    return (
        <button
            type="button"
            className={`${styles.filterChip} ${active ? styles.filterChipActive : ""}`}
            onClick={onClick}>
            <span>{children}</span>
            <span className={styles.filterChipCount}>{count}</span>
        </button>
    );
}

function StatusPill({ status }: { status: "win" | "loss" | "inflight" }) {
    const label = status === "win" ? "Resolved" : status === "loss" ? "Failed" : "Live";
    const cls = status === "win" ? styles.pillOk
        : status === "loss" ? styles.pillBad
        : styles.pillLive;
    return <span className={`${styles.statusPill} ${cls}`}>{label}</span>;
}

function OutcomeBadge({ outcome, winner }: { outcome: WatchdogOutcome, winner: boolean }) {
    if (winner) return <span className={`${styles.outcomeBadge} ${styles.outcomeWin}`}>winner</span>;
    const tone = outcomeToTone(outcome);
    const cls = tone === "ok" ? styles.outcomeOk
        : tone === "warn" ? styles.outcomeWarn
        : styles.outcomeBad;
    return <span className={`${styles.outcomeBadge} ${cls}`}>{shortOutcome(outcome)}</span>;
}

function outcomeToTone(o: WatchdogOutcome): "ok" | "warn" | "bad" {
    switch (o) {
        case "QueueCompleted":
        case "PreVerifyAvailable":
            return "ok";
        case "BudgetTimeout":
        case "Cancelled":
        case "ExcludedByPattern":
            return "warn";
        default:
            return "bad";
    }
}

function shortOutcome(o: WatchdogOutcome): string {
    switch (o) {
        case "QueueCompleted": return "completed";
        case "QueueFailed": return "queue failed";
        case "EnqueueFailed": return "enqueue failed";
        case "PreVerifyDead": return "verify: dead";
        case "PreVerifyTimeout": return "verify: timeout";
        case "PreVerifyAvailable": return "verify: ok";
        case "BudgetTimeout": return "budget timeout";
        case "Cancelled": return "cancelled";
        case "ExcludedByPattern": return "excluded";
        default: return o;
    }
}

type ClickGroup = {
    clickId: string,
    firstAt: number,
    requestedTitle: string,
    contentType: string,
    hasWinner: boolean,
    allResolved: boolean,
    attempts: WatchdogEntry[],
};

function attemptsEqual(a: WatchdogEntry[], b: WatchdogEntry[]): boolean {
    if (a === b) return true;
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
        const x = a[i], y = b[i];
        if (x.clickId !== y.clickId) return false;
        if (x.rankIndex !== y.rankIndex) return false;
        if (x.outcome !== y.outcome) return false;
        if (x.isWinner !== y.isWinner) return false;
        if (x.attemptedAtUnix !== y.attemptedAtUnix) return false;
        if (x.durationMs !== y.durationMs) return false;
        if (x.size !== y.size) return false;
        if (x.failReason !== y.failReason) return false;
    }
    return true;
}

function groupByClick(list: WatchdogEntry[]): ClickGroup[] {
    const map = new Map<string, ClickGroup>();
    for (const a of list) {
        const g = map.get(a.clickId);
        if (g) {
            g.attempts.push(a);
            if (a.attemptedAtUnix > g.firstAt) g.firstAt = a.attemptedAtUnix;
            if (a.isWinner) g.hasWinner = true;
        } else {
            map.set(a.clickId, {
                clickId: a.clickId,
                firstAt: a.attemptedAtUnix,
                requestedTitle: a.requestedTitle,
                contentType: a.contentType,
                hasWinner: a.isWinner,
                allResolved: false,
                attempts: [a],
            });
        }
    }
    const arr = Array.from(map.values());
    for (const g of arr) {
        g.attempts.sort((x, y) => x.rankIndex - y.rankIndex);
        g.allResolved = g.attempts.every(isTerminal);
    }
    arr.sort((x, y) => y.firstAt - x.firstAt);
    return arr;
}

function isTerminal(a: WatchdogEntry): boolean {
    switch (a.outcome) {
        case "QueueCompleted":
        case "QueueFailed":
        case "EnqueueFailed":
        case "PreVerifyDead":
        case "PreVerifyTimeout":
        case "Cancelled":
        case "BudgetTimeout":
        case "ExcludedByPattern":
            return true;
        case "PreVerifyAvailable":
            return false;
        default:
            return false;
    }
}

function hasExclusion(g: ClickGroup): boolean {
    return g.attempts.some(a => a.outcome === "ExcludedByPattern");
}

function matchesFilter(g: ClickGroup, f: FilterKey): boolean {
    switch (f) {
        case "all": return true;
        case "live": return !g.hasWinner && !g.allResolved;
        case "resolved": return g.hasWinner;
        case "failed": return !g.hasWinner && g.allResolved;
        case "excluded": return hasExclusion(g);
    }
}

function computeStats(groups: ClickGroup[]) {
    let resolved = 0, failed = 0, inFlight = 0, excluded = 0;
    for (const g of groups) {
        if (g.hasWinner) resolved++;
        else if (g.allResolved) failed++;
        else inFlight++;
        if (hasExclusion(g)) excluded++;
    }
    return { total: groups.length, resolved, failed, inFlight, excluded };
}

function formatProviderShort(raw: string | null | undefined): string {
    if (!raw) return "—";
    return raw.split(",").map(h => stripHost(h.trim())).filter(Boolean).join(" · ");
}

const GENERIC_HOST_PREFIXES = new Set(["news", "reader", "premium", "secure", "ssl", "nntp", "usenet", "block"]);

function stripHost(host: string): string {
    if (!host) return "";
    const labels = host.split(".").filter(Boolean);
    if (labels.length === 0) return host;
    if (labels.length === 1) return labels[0];
    if (labels.length === 2) return labels[0];
    if (GENERIC_HOST_PREFIXES.has(labels[0].toLowerCase())) return labels[1];
    return labels[0].length >= labels[1].length ? labels[0] : labels[1];
}

function formatBytes(bytes: number): string {
    if (bytes <= 0) return "—";
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
