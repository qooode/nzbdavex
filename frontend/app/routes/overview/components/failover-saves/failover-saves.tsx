import { useMemo } from "react";
import styles from "./failover-saves.module.css";
import type { FailoverBlock, OverviewWindow } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type FailoverSavesProps = {
    failover: FailoverBlock,
    window: OverviewWindow,
}

export function FailoverSaves({ failover, window }: FailoverSavesProps) {
    const { articlesRecovered, readsSaved, providers, buckets, bucketSizeMs } = failover;

    const maxSaves = useMemo(() => Math.max(1, ...providers.map(p => p.saves)), [providers]);

    const peak = useMemo(() => {
        let best: { bucket: number, total: number } | null = null;
        for (const b of buckets) {
            const total = b.counts.reduce((s, c) => s + c, 0);
            if (!best || total > best.total) best = { bucket: b.bucket, total };
        }
        return best;
    }, [buckets]);

    const hasData = articlesRecovered > 0 && providers.length > 0;
    const sinceLabel = window === "all" ? "all time" : `last ${window}`;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Failover saves</h3>
                <div className={styles.sub}>Articles a backup provider rescued, {sinceLabel}</div>
            </div>

            {hasData ? (
                <>
                    <div className={styles.hero}>
                        <span className={styles.heroNum}>{formatNumber(articlesRecovered)}</span>
                        <div className={styles.heroText}>
                            <div className={styles.heroLabel}>articles your backups rescued</div>
                            {readsSaved > 0 && (
                                <div className={styles.heroSub}>
                                    without them, <strong>{formatNumber(readsSaved)}</strong>{" "}
                                    {readsSaved === 1 ? "read" : "reads"} would&rsquo;ve failed
                                </div>
                            )}
                        </div>
                    </div>

                    <div className={styles.rankHead}>
                        <span>Rescued by</span>
                        <span>saves</span>
                    </div>
                    <div className={styles.bars}>
                        {providers.map(p => {
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

                    {peak && (
                        <div className={styles.footnote}>
                            Backups worked hardest {formatPeak(peak.bucket, bucketSizeMs)}{" "}
                            ({formatNumber(peak.total)} {peak.total === 1 ? "rescue" : "rescues"})
                        </div>
                    )}
                </>
            ) : (
                <div className={styles.empty}>
                    No failover saves in this window.
                    <div className={styles.emptySub}>
                        Every article was served on the first try. When a provider misses, a backup steps in.
                        You&rsquo;ll see which one rescued what here.
                    </div>
                </div>
            )}
        </div>
    );
}

const DAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

function formatPeak(ms: number, bucketSizeMs: number): string {
    const d = new Date(ms);
    const day = DAYS[d.getDay()];
    const dd = String(d.getDate()).padStart(2, "0");
    const mon = MONTHS[d.getMonth()];
    if (bucketSizeMs <= 3_600_000) {
        const hh = String(d.getHours()).padStart(2, "0");
        return `${day} ${hh}:00`;
    }
    if (bucketSizeMs >= 7 * 86_400_000) {
        return `the week of ${dd} ${mon}`;
    }
    return `${day} ${dd} ${mon}`;
}
