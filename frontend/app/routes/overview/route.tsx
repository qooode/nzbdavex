import type { Route } from "./+types/route";
import styles from "./route.module.css";
import { backendClient, type LiveStatsMessage, type OverviewStatsResponse, type OverviewWindow } from "~/clients/backend-client.server";
import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { receiveMessage } from "~/utils/websocket-util";
import { DndContext, PointerSensor, KeyboardSensor, useSensor, useSensors, closestCenter, type DragEndEvent } from "@dnd-kit/core";
import { SortableContext, arrayMove, verticalListSortingStrategy, sortableKeyboardCoordinates } from "@dnd-kit/sortable";
import { LiveTiles } from "./components/live-tiles/live-tiles";
import { LiveReadsPanel } from "./components/live-reads-panel/live-reads-panel";
import { ActivityHeatmap } from "./components/activity-heatmap/activity-heatmap";
import { ThroughputChart } from "./components/throughput-chart/throughput-chart";
import { LatencyHistogram } from "./components/latency-histogram/latency-histogram";
import { ErrorDonut } from "./components/error-donut/error-donut";
import { ProviderScoreboard } from "./components/provider-scoreboard/provider-scoreboard";
import { IndexerScoreboard } from "./components/indexer-scoreboard/indexer-scoreboard";
import { IndexerApiUsage } from "./components/indexer-api-usage/indexer-api-usage";
import { SessionsBlock } from "./components/sessions-block/sessions-block";
import { CatalogueBlock } from "./components/catalogue-block/catalogue-block";
import { LifetimeBlock } from "./components/lifetime-block/lifetime-block";
import { RecordsBlock } from "./components/records-block/records-block";
import { SortableRow } from "./components/sortable-row/sortable-row";
import { useRowOrder } from "./utils/use-row-order";

const topicNames = {
    liveStats: 'ls',
};
const topicSubscriptions = {
    [topicNames.liveStats]: 'state',
};

const WINDOWS: { value: OverviewWindow, label: string }[] = [
    { value: "24h", label: "24h" },
    { value: "7d", label: "7d" },
    { value: "30d", label: "30d" },
    { value: "all", label: "All" },
];

const DEFAULT_ROW_ORDER = [
    "liveTiles",
    "liveReads",
    "throughput",
    "activity",
    "latency",
    "errorsSessions",
    "providers",
    "indexers",
    "indexerApiUsage",
    "recordsCatalogue",
    "lifetime",
] as const;

export async function loader() {
    const stats = await backendClient.getOverviewStats("24h");
    return { stats };
}

export default function Overview({ loaderData }: Route.ComponentProps) {
    const [stats, setStats] = useState<OverviewStatsResponse>(loaderData.stats);
    const [window, setWindow] = useState<OverviewWindow>("24h");
    const [editMode, setEditMode] = useState(false);
    const { order, save, reset } = useRowOrder(DEFAULT_ROW_ORDER);

    const liveTiles = stats.tiles;
    const isLongWindow = window === "30d" || window === "all";

    // Re-fetch on window change + every 30s so chart, heatmap, providers, etc.
    // stay fresh without manual refresh. Skipped when the tab is hidden so
    // background tabs don't churn the backend; an immediate refetch fires when
    // the tab becomes visible again.
    useEffect(() => {
        let cancelled = false;
        const refetch = async () => {
            if (typeof document !== "undefined" && document.hidden) return;
            try {
                const res = await fetch(`/api/get-overview-stats?window=${window}`);
                if (!res.ok || cancelled) return;
                const data: OverviewStatsResponse = await res.json();
                if (!cancelled) setStats(data);
            } catch { /* network blip, retry next tick */ }
        };
        refetch();
        const interval = setInterval(refetch, 30_000);
        const onVisible = () => { if (!document.hidden) refetch(); };
        document.addEventListener("visibilitychange", onVisible);
        return () => {
            cancelled = true;
            clearInterval(interval);
            document.removeEventListener("visibilitychange", onVisible);
        };
    }, [window]);

    const onWsMessage = useCallback((topic: string, message: string) => {
        if (topic !== topicNames.liveStats) return;
        try {
            const live: LiveStatsMessage = JSON.parse(message);
            setStats(s => ({
                ...s,
                tiles: {
                    activeReads: live.activeReads,
                    articlesPerMinute: live.articlesPerMinute,
                    errorsPerMinute: live.errorsPerMinute,
                    bytesServedPerMinute: live.bytesServedPerMinute,
                }
            }));
        } catch { /* ignore */ }
    }, []);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(globalThis.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage(onWsMessage);
            ws.onopen = () => { ws.send(JSON.stringify(topicSubscriptions)); };
            ws.onclose = () => { if (!disposed) setTimeout(connect, 1000); };
            ws.onerror = () => { ws.close(); };
        }
        connect();
        return () => { disposed = true; ws?.close(); };
    }, [onWsMessage]);

    const rowContent = useMemo<Record<string, ReactNode>>(() => ({
        liveTiles: <LiveTiles tiles={liveTiles} />,
        liveReads: <LiveReadsPanel />,
        throughput: (
            <ThroughputChart
                points={stats.throughput}
                totalArticles={stats.totalArticles}
                totalErrors={stats.totalErrors}
                totalBytesServed={stats.sessions.totalBytesServed}
                window={window}
            />
        ),
        activity: (
            <ActivityHeatmap
                maxCell={stats.heatmap.maxCell}
                mode={stats.heatmap.mode}
                windowStartMs={stats.heatmap.windowStartMs}
                windowEndMs={stats.heatmap.windowEndMs}
                bucketSizeMs={stats.heatmap.bucketSizeMs}
                cells={stats.heatmap.cells}
            />
        ),
        latency: !isLongWindow
            ? (
                <LatencyHistogram
                    p50Ms={stats.latency.p50Ms}
                    p95Ms={stats.latency.p95Ms}
                    p99Ms={stats.latency.p99Ms}
                    samples={stats.latency.samples}
                    buckets={stats.latency.buckets}
                />
            )
            : null,
        errorsSessions: (
            <div className={styles.twoCol}>
                {!isLongWindow && <ErrorDonut errors={stats.errors} />}
                <SessionsBlock sessions={stats.sessions} window={window} />
            </div>
        ),
        providers: <ProviderScoreboard providers={stats.providers} window={window} />,
        indexers: <IndexerScoreboard indexers={stats.indexers} />,
        indexerApiUsage: <IndexerApiUsage rows={stats.indexerApiUsage} />,
        recordsCatalogue: (
            <div className={styles.twoCol}>
                <RecordsBlock records={stats.records} />
                <CatalogueBlock catalogue={stats.catalogue} />
            </div>
        ),
        lifetime: <LifetimeBlock lifetime={stats.lifetime} />,
    }), [liveTiles, stats, window, isLongWindow]);

    const sensors = useSensors(
        useSensor(PointerSensor, { activationConstraint: { distance: 4 } }),
        useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
    );

    const onDragEnd = (event: DragEndEvent) => {
        const { active, over } = event;
        if (!over || active.id === over.id) return;
        const oldIndex = order.indexOf(String(active.id));
        const newIndex = order.indexOf(String(over.id));
        if (oldIndex < 0 || newIndex < 0) return;
        save(arrayMove(order, oldIndex, newIndex));
    };

    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h2 className={styles.title}>Overview</h2>
                <div className={styles.headerActions}>
                    {editMode && (
                        <button
                            type="button"
                            className={styles.resetBtn}
                            onClick={reset}
                            title="Restore default order">
                            Reset
                        </button>
                    )}
                    <button
                        type="button"
                        className={editMode ? styles.editBtnActive : styles.editBtn}
                        onClick={() => setEditMode(v => !v)}
                        aria-pressed={editMode}
                        title={editMode ? "Done editing layout" : "Reorder widgets"}>
                        {editMode ? "Done" : "Edit layout"}
                    </button>
                    <div className={styles.windowToggle} role="tablist">
                        {WINDOWS.map(w => (
                            <button
                                key={w.value}
                                role="tab"
                                aria-selected={window === w.value}
                                className={window === w.value ? styles.windowActive : styles.windowOption}
                                onClick={() => setWindow(w.value)}>{w.label}</button>
                        ))}
                    </div>
                </div>
            </div>

            <DndContext sensors={sensors} collisionDetection={closestCenter} onDragEnd={onDragEnd}>
                <SortableContext items={order} strategy={verticalListSortingStrategy}>
                    {order.map(id => {
                        const content = rowContent[id];
                        if (!content) return null;
                        return (
                            <SortableRow key={id} id={id} editMode={editMode}>
                                {content}
                            </SortableRow>
                        );
                    })}
                </SortableContext>
            </DndContext>
        </div>
    );
}
