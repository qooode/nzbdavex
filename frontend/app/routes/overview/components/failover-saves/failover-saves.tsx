import { useMemo } from "react";
import styles from "./failover-saves.module.css";
import type { FailoverBlock, OverviewWindow } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type FailoverSavesProps = {
    failover: FailoverBlock,
    window: OverviewWindow,
}

export function FailoverSaves({ failover, window }: FailoverSavesProps) {
    const {
        articlesRecovered, previousArticlesRecovered, segmentsCovered,
        readsSaved, readSessions, totalArticles,
        rescuedBy, rescuedFrom, reasons, buckets, bucketSizeMs,
    } = failover;

    const maxSaves = useMemo(() => Math.max(1, ...rescuedBy.map(p => p.saves)), [rescuedBy]);
    const maxMisses = useMemo(() => Math.max(1, ...rescuedFrom.map(p => p.misses)), [rescuedFrom]);

    const bucketTotals = useMemo(
        () => buckets.map(b => ({ bucket: b.bucket, total: b.counts.reduce((s, c) => s + c, 0) })),
        [buckets]
    );
    const maxBucket = useMemo(() => Math.max(1, ...bucketTotals.map(b => b.total)), [bucketTotals]);
    const peak = useMemo(() => {
        let best: { bucket: number, total: number } | null = null;
        for (const b of bucketTotals) if (!best || b.total > best.total) best = b;
        return best;
    }, [bucketTotals]);

    const reasonTotal = useMemo(() => reasons.reduce((s, r) => s + r.count, 0), [reasons]);
    const momentum = useMemo(
        () => computeMomentum(articlesRecovered, previousArticlesRecovered),
        [articlesRecovered, previousArticlesRecovered]
    );

    const hasData = articlesRecovered > 0 && rescuedBy.length > 0;
    const sinceLabel = window === "all" ? "all time" : `last ${window}`;
    const saveRate = totalArticles > 0 ? articlesRecovered / totalArticles : 0;
    const oneIn = readsSaved > 0 ? Math.max(1, Math.round(readSessions / readsSaved)) : 0;
    const topHero = rescuedBy[0];
    const topVillain = rescuedFrom[0];

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Backup rescues</h3>
                <div className={styles.sub}>When your main provider misses a piece mid-session, a backup delivers it before anything fails ({sinceLabel})</div>
            </div>

            {hasData ? (
                <>
                    <div className={styles.hero}>
                        <span className={styles.heroNum}>{formatNumber(articlesRecovered)}</span>
                        <div className={styles.heroText}>
                            <div className={styles.heroLabel}>segments your backups rescued</div>
                            <div className={styles.heroSub}>
                                {totalArticles > 0 ? (
                                    <>that&rsquo;s <strong>{formatSmallPercent(saveRate)}</strong> of all fetches a backup had to cover</>
                                ) : (
                                    <>your stack self-healed every time it mattered</>
                                )}
                            </div>
                        </div>
                        {momentum && (
                            <div
                                className={`${styles.momentum} ${momentum.good ? styles.momentumGood : styles.momentumBad}`}
                                title={`Failover load ${momentum.label} vs the previous ${window}`}>
                                <span className={styles.momentumArrow}>{momentum.arrow}</span>
                                <span className={styles.momentumPct}>{momentum.text}</span>
                                <span className={styles.momentumWhen}>vs prev {window}</span>
                            </div>
                        )}
                    </div>

                    {readsSaved > 0 && (
                        <div className={styles.reads}>
                            <strong>1 in {oneIn}</strong> sessions needed a backup
                            <span className={styles.readsMuted}>
                                {" "}· {formatNumber(readsSaved)} of {formatNumber(readSessions)} rescued
                            </span>
                        </div>
                    )}

                    {(topHero || topVillain) && (
                        <div className={styles.cards}>
                            {topHero && (
                                <div className={styles.card}>
                                    <div className={styles.cardKicker}>Most saves</div>
                                    <div className={styles.cardName} title={topHero.provider}>
                                        {topHero.nickname?.trim() || topHero.provider}
                                    </div>
                                    <div className={`${styles.cardStat} ${styles.cardStatGood}`}>
                                        {formatNumber(topHero.saves)} <span>saves</span>
                                    </div>
                                </div>
                            )}
                            {topVillain && (
                                <div className={styles.card}>
                                    <div className={styles.cardKicker}>Most misses</div>
                                    <div className={styles.cardName} title={topVillain.provider}>
                                        {topVillain.nickname?.trim() || topVillain.provider}
                                    </div>
                                    <div className={`${styles.cardStat} ${styles.cardStatBad}`}>
                                        {formatNumber(topVillain.misses)} <span>misses</span>
                                    </div>
                                </div>
                            )}
                        </div>
                    )}

                    {reasons.length > 0 && (
                        <div className={styles.reasons}>
                            <div className={styles.sectionHead}>Why they missed</div>
                            <div className={styles.reasonBar}>
                                {reasons.map(r => {
                                    const meta = reasonMeta(r.status);
                                    const width = reasonTotal > 0 ? (r.count / reasonTotal) * 100 : 0;
                                    return (
                                        <span
                                            key={r.status}
                                            className={`${styles.reasonSeg} ${meta.cls}`}
                                            style={{ width: `${width.toFixed(1)}%` }}
                                            title={`${meta.label}: ${formatNumber(r.count)} (${width.toFixed(0)}%)`}
                                        />
                                    );
                                })}
                            </div>
                            <div className={styles.reasonLegend}>
                                {reasons.map(r => {
                                    const meta = reasonMeta(r.status);
                                    return (
                                        <span key={r.status} className={styles.reasonChip}>
                                            <span className={`${styles.dot} ${meta.cls}`} />
                                            {meta.label} <strong>{formatNumber(r.count)}</strong>
                                        </span>
                                    );
                                })}
                            </div>
                        </div>
                    )}

                    {rescuedFrom.length > 0 && (
                        <>
                            <div className={styles.rankHead}>
                                <span>Needed rescuing</span>
                                <span>misses</span>
                            </div>
                            <div className={styles.bars}>
                                {rescuedFrom.map(p => {
                                    const width = (p.misses / maxMisses) * 100;
                                    const share = segmentsCovered > 0 ? (p.misses / segmentsCovered) * 100 : 0;
                                    return (
                                        <div key={p.provider} className={styles.row} title={p.provider}>
                                            <span className={styles.name}>{p.nickname?.trim() || p.provider}</span>
                                            <span className={styles.barTrack}>
                                                <span className={`${styles.barFill} ${styles.barFillBad}`} style={{ width: `${width.toFixed(1)}%` }} />
                                            </span>
                                            <span className={styles.count}>{formatNumber(p.misses)}</span>
                                            <span className={styles.share}>{share.toFixed(0)}%</span>
                                        </div>
                                    );
                                })}
                            </div>
                        </>
                    )}

                    <div className={styles.rankHead}>
                        <span>Rescued by</span>
                        <span>saves</span>
                    </div>
                    <div className={styles.bars}>
                        {rescuedBy.map(p => {
                            const width = (p.saves / maxSaves) * 100;
                            const share = articlesRecovered > 0 ? (p.saves / articlesRecovered) * 100 : 0;
                            return (
                                <div key={p.provider} className={styles.row} title={p.provider}>
                                    <span className={styles.name}>{p.nickname?.trim() || p.provider}</span>
                                    <span className={styles.barTrack}>
                                        <span className={styles.barFill} style={{ width: `${width.toFixed(1)}%` }} />
                                    </span>
                                    <span className={styles.count}>{formatNumber(p.saves)}</span>
                                    <span className={styles.share}>{share.toFixed(0)}%</span>
                                </div>
                            );
                        })}
                    </div>

                    {bucketTotals.length > 1 && (
                        <div className={styles.trend}>
                            <div className={styles.sectionHead}>Trend</div>
                            <div className={styles.spark}>
                                {bucketTotals.map(b => (
                                    <span
                                        key={b.bucket}
                                        className={styles.sparkBar}
                                        style={{ height: `${Math.max(6, (b.total / maxBucket) * 100).toFixed(0)}%` }}
                                        title={`${formatBucket(b.bucket, bucketSizeMs)}: ${formatNumber(b.total)}`}
                                    />
                                ))}
                            </div>
                        </div>
                    )}

                    {peak && peak.total > 0 && (
                        <div className={styles.footnote}>
                            <span>Busiest {formatBucket(peak.bucket, bucketSizeMs)} ({formatNumber(peak.total)})</span>
                        </div>
                    )}
                </>
            ) : (
                <div className={styles.empty}>
                    No backup rescues in this window.
                    <div className={styles.emptySub}>
                        Every segment was served on the first try. When a provider misses, a backup steps in.
                        You&rsquo;ll see who failed, who covered, and why, right here.
                    </div>
                </div>
            )}
        </div>
    );
}

function formatSmallPercent(fraction: number): string {
    const pct = fraction * 100;
    if (pct <= 0) return "0%";
    if (pct >= 1) return `${pct.toFixed(1)}%`;
    if (pct >= 0.01) return `${pct.toFixed(2)}%`;
    return "<0.01%";
}

type Momentum = { arrow: string, text: string, label: string, good: boolean };

function computeMomentum(current: number, previous: number | null): Momentum | null {
    if (previous === null) return null;
    if (previous === 0 && current === 0) return null;
    if (previous === 0) return { arrow: "↑", text: "new", label: "up from none", good: false };
    const delta = ((current - previous) / previous) * 100;
    if (Math.abs(delta) < 3) return { arrow: "→", text: "flat", label: "holding flat", good: true };
    const up = delta > 0;
    const pct = `${Math.abs(delta).toFixed(0)}%`;
    return { arrow: up ? "↑" : "↓", text: pct, label: `${up ? "up" : "down"} ${pct}`, good: !up };
}

function reasonMeta(status: string): { label: string, cls: string } {
    switch (status) {
        case "Missing": return { label: "missing", cls: styles.rMissing };
        case "Timeout": return { label: "timed out", cls: styles.rTimeout };
        case "Auth": return { label: "auth", cls: styles.rAuth };
        case "Network": return { label: "network", cls: styles.rNetwork };
        case "Corrupt": return { label: "corrupt", cls: styles.rCorrupt };
        default: return { label: status.toLowerCase(), cls: styles.rOther };
    }
}

const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

function formatBucket(ms: number, bucketSizeMs: number): string {
    const d = new Date(ms);
    const day = DAYS[d.getDay()];
    const dd = String(d.getDate()).padStart(2, "0");
    const mon = MONTHS[d.getMonth()];
    if (bucketSizeMs <= 3_600_000) {
        const hh = String(d.getHours()).padStart(2, "0");
        return `${day} ${hh}:00`;
    }
    if (bucketSizeMs >= 7 * 86_400_000) {
        return `week of ${dd} ${mon}`;
    }
    return `${day} ${dd} ${mon}`;
}
