import { useEffect, useState } from "react";
import styles from "./live-streams.module.css";
import { receiveMessage } from "~/utils/websocket-util";
import { useNavigate } from "react-router";

const activeStreamsTopic = { as: 'state' };

type ProviderUsage = { host: string; segments: number };
type Stream = {
    id: string;
    fileName: string;
    path: string;
    startedAt: number;
    lastActivityAt: number;
    bytesRead: number;
    fileSize: number | null;
    providers: ProviderUsage[];
};
type Snapshot = { streams: Stream[] };

const GENERIC_HOST_PREFIXES = new Set(["news", "reader", "premium", "secure", "ssl", "nntp", "usenet", "block"]);

function stripHost(host: string): string {
    if (!host) return "—";
    const labels = host.split(".").filter(Boolean);
    if (labels.length === 0) return host;
    if (labels.length === 1) return labels[0];
    if (labels.length === 2) return labels[0];
    if (GENERIC_HOST_PREFIXES.has(labels[0].toLowerCase())) return labels[1];
    return labels[0].length >= labels[1].length ? labels[0] : labels[1];
}

function shortName(name: string): string {
    if (!name) return "—";
    const max = 28;
    return name.length <= max ? name : name.slice(0, max - 1) + "…";
}

export function LiveStreams() {
    const navigate = useNavigate();
    const [snapshot, setSnapshot] = useState<Snapshot | null>(null);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => {
                try { setSnapshot(JSON.parse(message)); }
                catch { /* ignore malformed frames */ }
            });
            ws.onopen = () => ws.send(JSON.stringify(activeStreamsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            if (e.code == 1008) navigate('/login');
            !disposed && setTimeout(() => connect(), 1000);
            setSnapshot(null);
        }
        return connect();
    }, []);

    const streams = snapshot?.streams ?? [];
    if (streams.length === 0) return null;

    return (
        <div className={styles.container}>
            <div className={styles.title}>
                Active Reads
            </div>
            <div className={styles.list}>
                {streams.map(s => <StreamRow key={s.id} stream={s} />)}
            </div>
        </div>
    );
}

function StreamRow({ stream }: { stream: Stream }) {
    const totalSegments = stream.providers.reduce((acc, p) => acc + p.segments, 0);
    return (
        <div className={styles.row} title={stream.fileName}>
            <div className={styles.fileName}>{shortName(stream.fileName)}</div>
            <div className={styles.providers}>
                {stream.providers.length === 0
                    ? <span className={styles.providersIdle}>buffering…</span>
                    : stream.providers.map((p, i) => (
                        <span key={p.host} className={styles.providersEntry}>
                            {i > 0 && <span className={styles.providersSep}>·</span>}
                            <span className={styles.providersHost} title={p.host}>{stripHost(p.host)}</span>
                            {totalSegments > 0 && (
                                <span className={styles.providersPct}>
                                    {Math.round((p.segments / totalSegments) * 100)}%
                                </span>
                            )}
                        </span>
                    ))}
            </div>
        </div>
    );
}
