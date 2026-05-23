import { useEffect, useRef, useState } from "react";
import styles from "./live-reads-panel.module.css";
import type { ActiveRead, ActiveReadsMessage } from "~/clients/backend-client.server";
import { formatBytes } from "../../utils/format";

const TOPIC_ACTIVE_READS = "ar";
const SUBSCRIPTION = { [TOPIC_ACTIVE_READS]: "state" };

/**
 * Live "right now" panel — reads cards refreshed via the ActiveReads WS topic.
 * Hidden when no reads are active so the page collapses cleanly.
 */
export function LiveReadsPanel() {
    const [reads, setReads] = useState<ActiveRead[]>([]);
    // Track previous bytesRead per session for live MiB/s computation.
    const prevRef = useRef<Map<string, { bytes: number, at: number, rate: number }>>(new Map());

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(globalThis.location.origin.replace(/^http/, "ws"));
            ws.onmessage = (event) => {
                try {
                    const parsed = JSON.parse(event.data);
                    if (parsed.Topic !== TOPIC_ACTIVE_READS) return;
                    const payload: ActiveReadsMessage = JSON.parse(parsed.Message);
                    const now = Date.now();
                    const prev = prevRef.current;
                    const next = new Map<string, { bytes: number, at: number, rate: number }>();
                    for (const r of payload.reads ?? []) {
                        const old = prev.get(r.id);
                        let rate = old?.rate ?? 0;
                        if (old && now > old.at) {
                            const dt = (now - old.at) / 1000;
                            const db = r.bytesRead - old.bytes;
                            if (dt > 0 && db >= 0) {
                                const instant = db / dt;
                                rate = old.rate * 0.4 + instant * 0.6;
                            }
                        }
                        next.set(r.id, { bytes: r.bytesRead, at: now, rate });
                    }
                    prevRef.current = next;
                    setReads(payload.reads ?? []);
                } catch { /* ignore */ }
            };
            ws.onopen = () => { ws.send(JSON.stringify(SUBSCRIPTION)); };
            ws.onclose = () => { if (!disposed) setTimeout(connect, 1000); };
            ws.onerror = () => { ws.close(); };
        }
        connect();
        return () => { disposed = true; ws?.close(); };
    }, []);

    if (reads.length === 0) return null;

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <div className={styles.titleWrap}>
                    <span className={styles.livePulse} />
                    <h3 className={styles.title}>Right now</h3>
                    <span className={styles.count}>{reads.length} active</span>
                </div>
            </div>
            <div className={styles.grid}>
                {reads.map(r => {
                    const meta = prevRef.current.get(r.id);
                    const rate = meta?.rate ?? 0;
                    // Use the latest read position (what the player is requesting
                    // right now) — not cumulative bytes transferred — so the bar
                    // reflects actual playback location, immune to seeks/replays.
                    const pct = r.fileSize && r.fileSize > 0
                        ? Math.min(100, (r.currentOffset / r.fileSize) * 100)
                        : null;
                    return (
                        <div key={r.id} className={styles.card}>
                            <div className={styles.fileName} title={r.path}>
                                {r.fileName || lastSegment(r.path)}
                            </div>
                            <div className={styles.progressWrap}>
                                <div
                                    className={pct !== null ? styles.progressFill : styles.progressIndeterminate}
                                    style={pct !== null ? { width: `${pct.toFixed(1)}%` } : undefined}
                                />
                            </div>
                            <div className={styles.stats}>
                                <span className={styles.bytes}>
                                    {r.fileSize
                                        ? <>at {formatBytes(r.currentOffset)} <span className={styles.bytesTotal}>/ {formatBytes(r.fileSize)}</span></>
                                        : <>at {formatBytes(r.currentOffset)}</>
                                    }
                                </span>
                                <span className={styles.rate}>{formatBytes(rate)}/s</span>
                            </div>
                            {r.providers.length > 0 && (
                                <div className={styles.providerStrip}>
                                    {r.providers.slice(0, 6).map((p, i) => {
                                        const label = p.nickname?.trim() || p.host;
                                        return (
                                            <span
                                                key={p.host}
                                                className={`${styles.providerChip} ${i === 0 ? styles.providerChipPrimary : ""}`}
                                                title={`${label} (${p.host}): ${p.segments} segments`}
                                            >
                                                <span className={styles.providerChipHost}>{label}</span>
                                                <span className={styles.providerChipCount}>{p.segments}</span>
                                            </span>
                                        );
                                    })}
                                </div>
                            )}
                        </div>
                    );
                })}
            </div>
        </div>
    );
}

function lastSegment(path: string): string {
    const idx = path.lastIndexOf("/");
    return idx >= 0 ? path.slice(idx + 1) : path;
}
