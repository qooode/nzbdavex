import { useMemo, useState } from "react";
import styles from "./failover-saves.module.css";
import type { FailoverBlock, OverviewWindow } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type FailoverSavesProps = {
    failover: FailoverBlock,
    window: OverviewWindow,
}

const PLOT_H = 132;

export function FailoverSaves({ failover, window }: FailoverSavesProps) {
    const [hoverIdx, setHoverIdx] = useState<number | null>(null);
    const { articlesRecovered, readsSaved, providers, buckets, bucketSizeMs } = failover;

    const colors = useMemo(
        () => providers.map((_, i) => PROVIDER_COLORS[i] ?? PROVIDER_OVERFLOW),
        [providers],
    );

    const maxBucketTotal = useMemo(
        () => Math.max(1, ...buckets.map(b => b.counts.reduce((s, c) => s + c, 0))),
        [buckets],
    );

    const hasData = articlesRecovered > 0 && buckets.length > 0;
    const sinceLabel = window === "all" ? "all time" : `last ${window}`;
    const hover = hoverIdx !== null ? buckets[hoverIdx] : null;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Failover saves</h3>
                <div className={styles.sub}>Articles a backup provider rescued, {sinceLabel}</div>
            </div>

            {hasData ? (
                <>
                    <div className={styles.hero}>
                        <div className={styles.heroMain}>
                            <span className={styles.heroNum}>{formatNumber(articlesRecovered)}</span>
                            <span className={styles.heroLabel}>articles recovered</span>
                        </div>
                        {readsSaved > 0 && (
                            <div className={styles.heroAside}>
                                <span className={styles.heroAsideNum}>{formatNumber(readsSaved)}</span>
                                <span className={styles.heroAsideLabel}>
                                    {readsSaved === 1 ? "read would've failed" : "reads would've failed"}
                                </span>
                            </div>
                        )}
                    </div>

                    <div className={styles.plot} style={{ height: PLOT_H }}>
                        {buckets.map((b, i) => {
                            const total = b.counts.reduce((s, c) => s + c, 0);
                            return (
                                <div
                                    key={b.bucket}
                                    className={`${styles.bar} ${hoverIdx === i ? styles.barActive : ""}`}
                                    onMouseEnter={() => setHoverIdx(i)}
                                    onMouseLeave={() => setHoverIdx(null)}
                                    title={`${formatBucketTime(b.bucket, bucketSizeMs)} · ${formatNumber(total)} rescued`}
                                >
                                    {b.counts.map((c, pi) => (c > 0 ? (
                                        <div
                                            key={pi}
                                            className={styles.seg}
                                            style={{ height: (c / maxBucketTotal) * PLOT_H, background: colors[pi] }}
                                        />
                                    ) : null))}
                                </div>
                            );
                        })}
                    </div>

                    <div className={styles.footer}>
                        {hover ? (
                            <span className={styles.hoverLine}>
                                <strong>{formatBucketTime(hover.bucket, bucketSizeMs)}</strong>
                                &nbsp;·&nbsp;{formatNumber(hover.counts.reduce((s, c) => s + c, 0))} rescued
                            </span>
                        ) : (
                            <span className={styles.hint}>Rescues over time, stacked by which provider stepped in</span>
                        )}
                    </div>

                    <div className={styles.legend}>
                        {providers.map((p, i) => (
                            <span key={p.provider} className={styles.legendItem} title={p.provider}>
                                <span className={styles.swatch} style={{ background: colors[i] }} />
                                <span className={styles.legendName}>{p.nickname?.trim() || p.provider}</span>
                                <span className={styles.legendNum}>{formatNumber(p.saves)}</span>
                            </span>
                        ))}
                    </div>
                </>
            ) : (
                <div className={styles.empty}>
                    No failover saves in this window.
                    <div className={styles.emptySub}>
                        Every article was served on the first try. When a provider misses, a backup steps in
                        — and you'll see exactly what it rescued here.
                    </div>
                </div>
            )}
        </div>
    );
}

const PROVIDER_COLORS = [
    "#34d399",
    "#38bdf8",
    "#a78bfa",
    "#fbbf24",
    "#2dd4bf",
    "#94a3b8",
];
const PROVIDER_OVERFLOW = "#71717a";

function formatBucketTime(ms: number, bucketSizeMs: number): string {
    const d = new Date(ms);
    const day = String(d.getDate()).padStart(2, "0");
    const mon = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"][d.getMonth()];
    if (bucketSizeMs <= 3_600_000) {
        const hh = String(d.getHours()).padStart(2, "0");
        return `${day} ${mon} ${hh}:00`;
    }
    if (bucketSizeMs >= 7 * 86_400_000) {
        return `wk of ${day} ${mon}`;
    }
    return `${day} ${mon}`;
}
