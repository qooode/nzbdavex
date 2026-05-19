import styles from "./lifetime-block.module.css";
import { formatBytes, formatNumber } from "../../utils/format";

export type LifetimeBlockProps = {
    lifetime: {
        bytesFetched: number,
        bytesRead: number,
        articles: number,
        readSessions: number,
        readSeconds: number,
        firstSeenAt: number | null,
    },
};

export function LifetimeBlock({ lifetime }: LifetimeBlockProps) {
    const isEmpty = lifetime.bytesRead === 0 && lifetime.articles === 0;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>All time</h3>
                <div className={styles.sub}>{since(lifetime.firstSeenAt)}</div>
            </div>

            {isEmpty ? (
                <div className={styles.empty}>Lifetime totals appear after your first reads.</div>
            ) : (
                <div className={styles.grid}>
                    <Tile label="Read" value={formatBytes(lifetime.bytesRead)} />
                    <Tile label="Articles" value={formatNumber(lifetime.articles)} />
                    <Tile label="Read sessions" value={formatNumber(lifetime.readSessions)} />
                    <Tile label="Active-reads time" value={formatHours(lifetime.readSeconds)} />
                </div>
            )}
        </div>
    );
}

function Tile({ label, value }: { label: string, value: string }) {
    return (
        <div className={styles.cell}>
            <div className={styles.label}>{label}</div>
            <div className={styles.value}>{value}</div>
        </div>
    );
}

function since(firstSeenAt: number | null): string {
    if (!firstSeenAt) return "Since you started";
    const days = Math.max(1, Math.floor((Date.now() - firstSeenAt) / 86_400_000));
    if (days < 30) return `Last ${days} days of activity`;
    const months = Math.floor(days / 30);
    if (months < 24) return `Last ${months} months of activity`;
    return `Last ${Math.floor(months / 12)} years of activity`;
}

function formatHours(seconds: number): string {
    if (seconds < 60) return `${seconds} s`;
    const m = seconds / 60;
    if (m < 60) return `${m.toFixed(0)} min`;
    const h = m / 60;
    if (h < 48) return `${h.toFixed(1)} h`;
    const d = h / 24;
    return `${d.toFixed(1)} d`;
}
