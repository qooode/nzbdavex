import { useMemo, useState } from "react";
import styles from "./activity-heatmap.module.css";
import type { HeatmapCell, HeatmapMode } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";

export type ActivityHeatmapProps = {
    maxCell: number,
    mode: HeatmapMode,
    windowStartMs: number,
    windowEndMs: number,
    bucketSizeMs: number,
    cells: HeatmapCell[],
}

const ONE_HOUR = 3_600_000;
const ONE_DAY = 86_400_000;
const DOW_LABELS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
const MONTH_LABELS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

type GridCell = {
    bucket: number,
    count: number,
    inRange: boolean,
}

type GridRow = {
    label: string,
    title?: string,
    cells: GridCell[],
}

type GridShape = {
    rows: GridRow[],
    cols: number,
    columnLabels?: { index: number, label: string }[],
}

export function ActivityHeatmap({ maxCell, mode, windowStartMs, windowEndMs, bucketSizeMs, cells }: ActivityHeatmapProps) {
    const [hover, setHover] = useState<GridCell | null>(null);

    const lookup = useMemo(() => {
        const m = new Map<number, number>();
        for (const c of cells) m.set(c.bucket, c.count);
        return m;
    }, [cells]);

    const grid = useMemo(
        () => buildGrid(mode, windowStartMs, windowEndMs, bucketSizeMs, lookup),
        [mode, windowStartMs, windowEndMs, bucketSizeMs, lookup],
    );

    const total = useMemo(() => cells.reduce((s, c) => s + c.count, 0), [cells]);
    const empty = total === 0;

    const peak = useMemo(() => {
        let best: HeatmapCell | null = null;
        for (const c of cells) if (!best || c.count > best.count) best = c;
        return best;
    }, [cells]);

    const subtitle = subtitleFor(mode);
    const emptyMessage = emptyMessageFor(mode);
    const peakLabel = peak ? formatBucket(peak.bucket, bucketSizeMs) : null;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div>
                    <h3 className={styles.title}>Activity heatmap</h3>
                    <div className={styles.sub}>{subtitle}</div>
                </div>
                {peak && peak.count > 0 && peakLabel && (
                    <div className={styles.peak}>
                        <span className={styles.peakLabel}>Peak</span>
                        <span className={styles.peakValue}>{peakLabel}</span>
                        <span className={styles.peakCount}>{formatNumber(peak.count)} articles</span>
                    </div>
                )}
            </div>

            {empty ? (
                <div className={styles.empty}>{emptyMessage}</div>
            ) : (
                <>
                    <div className={`${styles.grid} ${styles[`mode_${mode}`]}`}>
                        {grid.rows.map((row, r) => (
                            <div key={r} className={styles.row}>
                                <div className={styles.dayLabel} title={row.title}>{row.label}</div>
                                <div
                                    className={styles.cellRow}
                                    style={{ gridTemplateColumns: `repeat(${grid.cols}, minmax(0, 1fr))` }}>
                                    {row.cells.map((cell, c) => {
                                        if (!cell.inRange) {
                                            return <div key={c} className={styles.cellEmpty} />;
                                        }
                                        const intensity = maxCell > 0 ? cell.count / maxCell : 0;
                                        return (
                                            <div
                                                key={c}
                                                className={styles.cell}
                                                style={{ backgroundColor: cellColor(intensity) }}
                                                onMouseEnter={() => setHover(cell)}
                                                onMouseLeave={() => setHover(h => (h && h.bucket === cell.bucket ? null : h))}
                                            />
                                        );
                                    })}
                                </div>
                            </div>
                        ))}
                        {grid.columnLabels && grid.columnLabels.length > 0 && (
                            <div className={styles.axisRow}>
                                <div className={styles.dayLabel} aria-hidden />
                                <div
                                    className={styles.axisGrid}
                                    style={{ gridTemplateColumns: `repeat(${grid.cols}, minmax(0, 1fr))` }}>
                                    {grid.columnLabels.map((c, i) => (
                                        <span key={i} className={styles.axisTick} style={{ gridColumnStart: c.index + 1 }}>
                                            {c.label}
                                        </span>
                                    ))}
                                </div>
                            </div>
                        )}
                    </div>

                    <div className={styles.footer}>
                        <div className={styles.tooltip}>
                            {hover ? (
                                <>
                                    {formatBucket(hover.bucket, bucketSizeMs)} &mdash;{" "}
                                    {formatNumber(hover.count)} {hover.count === 1 ? "article" : "articles"}
                                </>
                            ) : (
                                <>Hover a cell for details</>
                            )}
                        </div>
                        <div className={styles.scale}>
                            <span>Less</span>
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(0) }} />
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(0.25) }} />
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(0.5) }} />
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(0.75) }} />
                            <div className={styles.scaleSwatch} style={{ backgroundColor: cellColor(1) }} />
                            <span>More</span>
                        </div>
                    </div>
                </>
            )}
        </div>
    );
}

function buildGrid(
    mode: HeatmapMode,
    windowStartMs: number,
    windowEndMs: number,
    bucketSizeMs: number,
    lookup: Map<number, number>,
): GridShape {
    if (mode === "day") {
        const cells: GridCell[] = [];
        for (let h = 0; h < 24; h++) {
            const bucket = windowStartMs + h * ONE_HOUR;
            cells.push({ bucket, count: lookup.get(bucket) ?? 0, inRange: true });
        }
        return {
            rows: [{ label: "Hours", cells }],
            cols: 24,
            columnLabels: hourAxisLabels(),
        };
    }

    if (mode === "week" || mode === "month") {
        const dayCount = mode === "week" ? 7 : 30;
        const rows: GridRow[] = [];
        for (let d = 0; d < dayCount; d++) {
            const dayStart = windowStartMs + d * ONE_DAY;
            const cellsRow: GridCell[] = [];
            for (let h = 0; h < 24; h++) {
                const bucket = dayStart + h * ONE_HOUR;
                cellsRow.push({
                    bucket,
                    count: lookup.get(bucket) ?? 0,
                    inRange: bucket <= windowEndMs,
                });
            }
            rows.push({
                label: rowDateLabel(dayStart, mode),
                title: formatBucket(dayStart, ONE_DAY),
                cells: cellsRow,
            });
        }
        return {
            rows,
            cols: 24,
            columnLabels: hourAxisLabels(),
        };
    }

    const weekCount = 53;
    const rows: GridRow[] = [];
    for (let dow = 0; dow < 7; dow++) {
        const cellsRow: GridCell[] = [];
        for (let w = 0; w < weekCount; w++) {
            const bucket = windowStartMs + w * 7 * ONE_DAY + dow * ONE_DAY;
            cellsRow.push({
                bucket,
                count: lookup.get(bucket) ?? 0,
                inRange: bucket <= windowEndMs,
            });
        }
        rows.push({ label: DOW_LABELS[dow], cells: cellsRow });
    }
    return {
        rows,
        cols: weekCount,
        columnLabels: monthAxisLabels(windowStartMs, weekCount),
    };
}

function hourAxisLabels(): { index: number, label: string }[] {
    return [0, 6, 12, 18, 23].map(h => ({ index: h, label: String(h).padStart(2, "0") }));
}

function monthAxisLabels(startMonday: number, weekCount: number): { index: number, label: string }[] {
    const labels: { index: number, label: string }[] = [];
    let lastMonth = -1;
    for (let w = 0; w < weekCount; w++) {
        const weekStart = new Date(startMonday + w * 7 * ONE_DAY);
        const m = weekStart.getUTCMonth();
        if (m !== lastMonth) {
            labels.push({ index: w, label: MONTH_LABELS[m] });
            lastMonth = m;
        }
    }
    return labels;
}

function rowDateLabel(dayStartMs: number, mode: HeatmapMode): string {
    const d = new Date(dayStartMs);
    const month = MONTH_LABELS[d.getUTCMonth()];
    const day = d.getUTCDate();
    if (mode === "week") {
        return `${DOW_LABELS[(d.getUTCDay() + 6) % 7]} ${day}`;
    }
    return `${month} ${day}`;
}

function formatBucket(bucketMs: number, bucketSizeMs: number): string {
    const d = new Date(bucketMs);
    const month = MONTH_LABELS[d.getUTCMonth()];
    const day = d.getUTCDate();
    const dow = DOW_LABELS[(d.getUTCDay() + 6) % 7];
    const year = d.getUTCFullYear();
    const yearNow = new Date().getUTCFullYear();
    const yearSuffix = year === yearNow ? "" : ` ${year}`;
    if (bucketSizeMs >= ONE_DAY) {
        return `${dow} ${month} ${day}${yearSuffix}`;
    }
    const hour = String(d.getUTCHours()).padStart(2, "0");
    return `${dow} ${month} ${day}${yearSuffix} ${hour}:00 UTC`;
}

function subtitleFor(mode: HeatmapMode): string {
    switch (mode) {
        case "day": return "Articles per hour, last 24 hours";
        case "week": return "Articles per hour, last 7 days";
        case "month": return "Articles per hour, last 30 days";
        case "year": return "Articles per day, last year";
    }
}

function emptyMessageFor(mode: HeatmapMode): string {
    switch (mode) {
        case "day": return "No activity in the last 24 hours yet.";
        case "week": return "No activity in the last 7 days yet.";
        case "month": return "No activity in the last 30 days yet.";
        case "year": return "No activity in the last year yet.";
    }
}

function cellColor(intensity: number): string {
    if (intensity <= 0) return "rgba(255,255,255,0.04)";
    const eased = Math.pow(Math.min(1, intensity), 0.6);
    const alpha = 0.15 + eased * 0.75;
    return `rgba(52, 211, 153, ${alpha.toFixed(3)})`;
}
