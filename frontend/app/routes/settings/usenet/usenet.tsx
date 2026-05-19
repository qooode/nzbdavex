import styles from "./usenet.module.css"
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect, useMemo } from "react";
import { Button } from "react-bootstrap";
import { receiveMessage } from "~/utils/websocket-util";

const usenetConnectionsTopic = {'cxs': 'state'};
const USAGE_POLL_INTERVAL_MS = 10_000;

type UsenetSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

enum ProviderType {
    Disabled = 0,
    Pooled = 1,
    BackupAndStats = 2,
    BackupOnly = 3,
}

type ConnectionDetails = {
    Type: ProviderType;
    Host: string;
    Port: number;
    UseSsl: boolean;
    User: string;
    Pass: string;
    MaxConnections: number;
    PreviousType?: ProviderType;
    // null/0 = uncapped. Stored as bytes; the modal lets the user type a
    // friendlier MB/GB/TB value that gets converted on save.
    ByteLimit?: number | null;
    // Counter adjustment, used for "initial used" on a freshly added block
    // and zeroed on reset. Bytes.
    BytesUsedOffset?: number;
    // unix-ms cutoff. Hourly rows older than this are excluded from the live
    // usage gauge. A reset bumps this to Date.now().
    BytesUsedResetAt?: number;
};

// camelCase matches the JSON wire format — ASP.NET Core MVC defaults to
// camelCase serialization, so we mirror that here instead of fighting it.
type ProviderUsage = {
    index: number;
    host: string;
    bytesUsed: number;
    byteLimit: number | null;
    overLimit: boolean;
    bytesPerDay: number;
    daysRemaining: number | null;
};

function formatDaysRemaining(days: number): string {
    // Friendlier than "0.3 days" or "847 days" — round to the unit that's
    // actually useful at this horizon.
    if (days < 1) {
        const hours = Math.max(1, Math.round(days * 24));
        return `~${hours}h left at this pace`;
    }
    if (days < 60) return `~${Math.round(days)} days left at this pace`;
    const months = days / 30;
    if (months < 24) return `~${Math.round(months)} months left at this pace`;
    return `~${Math.round(months / 12)} years left at this pace`;
}

const BYTE_UNITS = [
    { label: "MB", multiplier: 1_000_000 },
    { label: "GB", multiplier: 1_000_000_000 },
    { label: "TB", multiplier: 1_000_000_000_000 },
] as const;
type ByteUnitLabel = typeof BYTE_UNITS[number]["label"];

function bytesToValueAndUnit(bytes: number | null | undefined): { value: string; unit: ByteUnitLabel } {
    if (!bytes || bytes <= 0) return { value: "", unit: "GB" };
    // Pick the largest unit that keeps the number readable (>= 1).
    const choice = [...BYTE_UNITS].reverse().find(u => bytes >= u.multiplier) ?? BYTE_UNITS[1];
    const v = bytes / choice.multiplier;
    // Trim trailing zeros so "500" doesn't display as "500.000".
    return { value: Number(v.toFixed(3)).toString(), unit: choice.label };
}

function valueAndUnitToBytes(value: string, unit: ByteUnitLabel): number | null {
    const trimmed = value.trim();
    if (trimmed === "") return null;
    const n = Number(trimmed);
    if (!isFinite(n) || n <= 0) return null;
    const u = BYTE_UNITS.find(x => x.label === unit) ?? BYTE_UNITS[1];
    return Math.round(n * u.multiplier);
}

function formatBytes(bytes: number): string {
    if (!isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB", "PB"];
    let i = 0;
    let v = bytes;
    while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
    return v >= 100 ? `${v.toFixed(0)} ${units[i]}` : `${v.toFixed(1)} ${units[i]}`;
}

type ConnectionCounts = {
    live: number;
    active: number;
    max: number;
}

type UsenetProviderConfig = {
    Providers: ConnectionDetails[];
};

const PROVIDER_TYPE_LABELS: Record<ProviderType, string> = {
    [ProviderType.Disabled]: "Disabled",
    [ProviderType.Pooled]: "Pool Connections",
    [ProviderType.BackupAndStats]: "Backup & Health Checks",
    [ProviderType.BackupOnly]: "Backup Only",
};

function parseProviderConfig(jsonString: string): UsenetProviderConfig {
    try {
        if (!jsonString || jsonString.trim() === "") {
            return { Providers: [] };
        }
        return JSON.parse(jsonString);
    } catch {
        return { Providers: [] };
    }
}

function serializeProviderConfig(config: UsenetProviderConfig): string {
    return JSON.stringify(config);
}

export function UsenetSettings({ config, setNewConfig }: UsenetSettingsProps) {
    // state
    const [showModal, setShowModal] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);
    const [connections, setConnections] = useState<{[index: number]: ConnectionCounts}>({});
    const [usage, setUsage] = useState<{[index: number]: ProviderUsage}>({});
    const providerConfig = useMemo(() => parseProviderConfig(config["usenet.providers"]), [config]);

    // handlers
    const handleAddProvider = useCallback(() => {
        setEditingIndex(null);
        setShowModal(true);
    }, []);

    const handleEditProvider = useCallback((index: number) => {
        setEditingIndex(index);
        setShowModal(true);
    }, []);

    const handleDeleteProvider = useCallback((index: number) => {
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.filter((_, i) => i !== index);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleToggleProvider = useCallback((index: number) => {
        const current = providerConfig.Providers[index];
        if (!current) return;
        const isDisabled = current.Type === ProviderType.Disabled;
        const updated: ConnectionDetails = isDisabled
            ? { ...current, Type: current.PreviousType ?? ProviderType.Pooled, PreviousType: undefined }
            : { ...current, Type: ProviderType.Disabled, PreviousType: current.Type };
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.map((p, i) => i === index ? updated : p);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleResetUsage = useCallback((index: number) => {
        const current = providerConfig.Providers[index];
        if (!current) return;
        if (!confirm(`Reset bytes-used counter for "${current.Host}" to zero?\n\nThis only rewinds the gauge for this provider's data cap. Historical metrics and graphs are untouched. Takes effect after you save settings.`)) return;
        const updated: ConnectionDetails = {
            ...current,
            BytesUsedOffset: 0,
            BytesUsedResetAt: Date.now(),
        };
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.map((p, i) => i === index ? updated : p);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSaveProvider = useCallback((provider: ConnectionDetails) => {
        const newProviderConfig = { ...providerConfig };
        if (editingIndex !== null) {
            newProviderConfig.Providers[editingIndex] = provider;
        } else {
            newProviderConfig.Providers.push(provider);
        }
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
        handleCloseModal();
    }, [config, providerConfig, editingIndex, setNewConfig, handleCloseModal]);

    const handleConnectionsMessage = useCallback((message: string) => {
        const parts = (message || "0|0|0|0|1|0").split("|");
        const [index, live, idle, _0, _1, _2] = parts.map((x: any) => Number(x));
        if (showModal) return;
        if (index >= providerConfig.Providers.length) return;
        setConnections(prev => ({...prev, [index]: {
            active: live - idle,
            live: live,
            max: providerConfig.Providers[index]?.MaxConnections || 1
        }}));
    }, [setConnections]);

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage((_, message) => handleConnectionsMessage(message));
            ws.onopen = () => ws.send(JSON.stringify(usenetConnectionsTopic));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            !disposed && setTimeout(() => connect(), 1000);
            setConnections({});
        }
        return connect();
    }, [setConnections, handleConnectionsMessage]);

    // Poll provider usage. Backend computes "bytes since reset + offset" from
    // the persisted hourly rollup plus the in-memory tracker; cheap enough to
    // hit on a 10s tick. We skip while the edit modal is open since the user
    // may be mid-edit and we don't want the card behind the modal flickering.
    useEffect(() => {
        let disposed = false;
        async function fetchUsage() {
            try {
                const response = await fetch('/api/get-provider-usage');
                if (!response.ok || disposed) return;
                const data: { providers?: ProviderUsage[] } = await response.json();
                if (disposed || !data.providers) return;
                const next: {[index: number]: ProviderUsage} = {};
                for (const p of data.providers) next[p.index] = p;
                setUsage(next);
            } catch {
                // network blips are fine — next tick retries.
            }
        }
        fetchUsage();
        if (showModal) return () => { disposed = true; };
        const id = setInterval(fetchUsage, USAGE_POLL_INTERVAL_MS);
        return () => { disposed = true; clearInterval(id); };
    }, [showModal, providerConfig.Providers.length]);

    // view
    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Usenet Providers</div>
                    <Button variant="primary" size="sm" onClick={handleAddProvider}>
                        Add
                    </Button>
                </div>
                {providerConfig.Providers.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No Usenet providers configured.
                        Click on the "Add" button to get started.
                    </p>
                ) : (
                    <div className={styles["providers-grid"]}>
                        {providerConfig.Providers.map((provider, index) => {
                            const isDisabled = provider.Type === ProviderType.Disabled;
                            return (
                            <div key={index} className={`${styles["provider-card"]} ${isDisabled ? styles["provider-card-disabled"] : ""}`}>
                                <div className={styles["provider-card-inner"]}>
                                    <div className={styles["provider-header"]}>
                                        <div className={styles["provider-header-content"]}>
                                            <div className={styles["provider-host"]}>
                                                {provider.Host}
                                                {isDisabled && <span className={styles["provider-disabled-badge"]}>Disabled</span>}
                                            </div>
                                            <div className={styles["provider-port"]}>
                                                Port {provider.Port}
                                            </div>
                                        </div>
                                        <div className={styles["provider-header-actions"]}>
                                            <button
                                                className={`${styles["header-action-button"]} ${styles["toggle"]} ${isDisabled ? styles["toggle-off"] : styles["toggle-on"]}`}
                                                onClick={() => handleToggleProvider(index)}
                                                title={isDisabled ? "Enable Provider" : "Disable Provider"}
                                                aria-pressed={!isDisabled}
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                                    <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                                                    <line x1="12" y1="2" x2="12" y2="12" />
                                                </svg>
                                            </button>
                                            <button
                                                className={styles["header-action-button"]}
                                                onClick={() => handleEditProvider(index)}
                                                title="Edit Provider"
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                    <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                                                    <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                                                </svg>
                                            </button>
                                            <button
                                                className={`${styles["header-action-button"]} ${styles["delete"]}`}
                                                onClick={() => handleDeleteProvider(index)}
                                                title="Delete Provider"
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                    <polyline points="3 6 5 6 21 6" />
                                                    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                                                </svg>
                                            </button>
                                        </div>
                                    </div>

                                    <div className={styles["provider-details"]}>
                                        <div className={styles["provider-detail-row"]}>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
                                                        <circle cx="12" cy="7" r="4" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Username</span>
                                                    <span className={styles["provider-detail-value"]}>{provider.User}</span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Connections</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {connections[index]
                                                            ? `${connections[index].live} / ${provider.MaxConnections} max`
                                                            : `${provider.MaxConnections} max`}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    {provider.UseSsl ? (
                                                        // Closed lock icon
                                                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                            <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                                            <path d="M7 11V7a5 5 0 0 1 10 0v4" />
                                                            <circle cx="12" cy="16" r="1" fill="currentColor" />
                                                        </svg>
                                                    ) : (
                                                        // Open lock icon
                                                        <svg width="13" height="13" viewBox="0 -2 24 26" fill="none" stroke="currentColor" strokeWidth="2">
                                                            <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                                            <path d="M7 11V4a5 5 0 0 1 9.9 1" />
                                                            <circle cx="12" cy="16" r="1" fill="currentColor" />
                                                        </svg>
                                                    )}
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Security</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {provider.UseSsl ? "SSL Enabled" : "No SSL"}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.3">
                                                        <text x="12" y="9" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">1</text>
                                                        <text x="6" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">2</text>
                                                        <text x="18" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">3</text>
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Behavior</span>
                                                    <span className={styles["provider-detail-value"]}>{PROVIDER_TYPE_LABELS[provider.Type]}</span>
                                                </div>
                                            </div>

                                        </div>

                                        <UsageRow
                                            provider={provider}
                                            usage={usage[index]}
                                            onReset={() => handleResetUsage(index)}
                                        />
                                    </div>
                                </div>
                            </div>
                            );
                        })}
                    </div>
                )}
            </div>

            <ProviderModal
                show={showModal}
                provider={editingIndex !== null ? providerConfig.Providers[editingIndex] : null}
                onClose={handleCloseModal}
                onSave={handleSaveProvider}
            />
        </div>
    );
}

type UsageRowProps = {
    provider: ConnectionDetails;
    usage: ProviderUsage | undefined;
    onReset: () => void;
};

function UsageRow({ provider, usage, onReset }: UsageRowProps) {
    const limit = provider.ByteLimit ?? null;
    const used = usage?.bytesUsed ?? 0;
    const hasLimit = limit !== null && limit > 0;
    const pct = hasLimit ? Math.min(100, (used / (limit as number)) * 100) : 0;
    // Thresholds match the soft-warning levels the backend would alert on if
    // we wired notifications. Keeping the same numbers here means the colors
    // tell the same story as any future alert email or webhook.
    const tone = hasLimit
        ? (pct >= 100 ? "danger" : pct >= 95 ? "danger" : pct >= 80 ? "warn" : "ok")
        : "neutral";

    const showAnything = hasLimit || used > 0 || usage !== undefined;
    if (!showAnything) return null;

    return (
        <div className={styles["usage-row"]}>
            <div className={styles["usage-header"]}>
                <span className={styles["usage-label"]}>
                    {hasLimit ? "Data Cap" : "Data Used"}
                </span>
                <span className={styles[`usage-value-${tone}`]}>
                    {hasLimit
                        ? `${formatBytes(used)} / ${formatBytes(limit as number)}  ·  ${pct.toFixed(1)}%`
                        : formatBytes(used)}
                </span>
                <button
                    type="button"
                    className={styles["usage-reset"]}
                    onClick={onReset}
                    title="Reset the counter to zero (e.g. after buying a new block)"
                >
                    Reset
                </button>
            </div>
            {hasLimit && (
                <div className={styles["usage-bar-track"]}>
                    <div
                        className={`${styles["usage-bar-fill"]} ${styles[`usage-bar-${tone}`]}`}
                        style={{ width: `${pct}%` }}
                    />
                </div>
            )}
            {usage && usage.daysRemaining !== null && usage.daysRemaining !== undefined && !usage.overLimit && (
                <div className={styles["usage-hint"]}>
                    {formatDaysRemaining(usage.daysRemaining)}
                </div>
            )}
            {usage?.overLimit && (
                <div className={styles["usage-warning"]}>
                    Data cap reached. This provider is paused to keep in-flight fetches from overshooting. Reset the counter or raise the cap to resume.
                </div>
            )}
        </div>
    );
}

type ProviderModalProps = {
    show: boolean;
    provider: ConnectionDetails | null;
    onClose: () => void;
    onSave: (provider: ConnectionDetails) => void;
};

function ProviderModal({ show, provider, onClose, onSave }: ProviderModalProps) {
    const isEditing = provider !== null;
    const initialLimit = bytesToValueAndUnit(provider?.ByteLimit);
    const initialUsed = bytesToValueAndUnit(provider?.BytesUsedOffset);

    const [host, setHost] = useState(provider?.Host || "");
    const [port, setPort] = useState(provider?.Port?.toString() || "");
    const [useSsl, setUseSsl] = useState(provider?.UseSsl ?? true);
    const [user, setUser] = useState(provider?.User || "");
    const [pass, setPass] = useState(provider?.Pass || "");
    const [maxConnections, setMaxConnections] = useState(provider?.MaxConnections?.toString() || "");
    const [type, setType] = useState<ProviderType>(provider?.Type ?? ProviderType.Pooled);
    const [limitValue, setLimitValue] = useState(initialLimit.value);
    const [limitUnit, setLimitUnit] = useState<ByteUnitLabel>(initialLimit.unit);
    const [initialUsedValue, setInitialUsedValue] = useState(initialUsed.value);
    const [initialUsedUnit, setInitialUsedUnit] = useState<ByteUnitLabel>(initialUsed.unit);
    const [isTestingConnection, setIsTestingConnection] = useState(false);
    const [connectionTested, setConnectionTested] = useState(false);
    const [testError, setTestError] = useState<string | null>(null);

    // Reset form when modal opens or provider changes
    useEffect(() => {
        if (show) {
            const lim = bytesToValueAndUnit(provider?.ByteLimit);
            const used = bytesToValueAndUnit(provider?.BytesUsedOffset);
            setHost(provider?.Host || "");
            setPort(provider?.Port?.toString() || "");
            setUseSsl(provider?.UseSsl ?? true);
            setUser(provider?.User || "");
            setPass(provider?.Pass || "");
            setMaxConnections(provider?.MaxConnections?.toString() || "");
            setType(provider?.Type ?? ProviderType.Pooled);
            setLimitValue(lim.value);
            setLimitUnit(lim.unit);
            setInitialUsedValue(used.value);
            setInitialUsedUnit(used.unit);
            setConnectionTested(false);
            setTestError(null);
        }
    }, [show, provider]);

    // Handle Escape key to close modal
    useEffect(() => {
        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && show) {
                onClose();
            }
        };

        if (show) {
            document.addEventListener('keydown', handleEscape);
            return () => document.removeEventListener('keydown', handleEscape);
        }
    }, [show, onClose]);

    const handleTestConnection = useCallback(async () => {
        setIsTestingConnection(true);
        setTestError(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);

            const response = await fetch('/api/test-usenet-connection', {
                method: 'POST',
                body: formData,
            });

            if (response.ok) {
                const data = await response.json();
                if (data.connected) {
                    setConnectionTested(true);
                    setTestError(null);
                } else {
                    setTestError("Connection test failed");
                }
            } else {
                setTestError("Failed to test connection");
            }
        } catch (error) {
            setTestError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
        } finally {
            setIsTestingConnection(false);
        }
    }, [host, port, useSsl, user, pass]);

    const handleSave = useCallback(() => {
        const byteLimit = valueAndUnitToBytes(limitValue, limitUnit);
        const initialUsedBytes = valueAndUnitToBytes(initialUsedValue, initialUsedUnit);

        // On a brand-new provider, an initial-used value also sets ResetAt to
        // now — otherwise the metrics rollup would count any pre-existing
        // history for the same hostname twice. On edit, leave ResetAt alone
        // (the dedicated Reset button is the right surface for that).
        const isNew = !isEditing;
        const offsetToPersist = isNew
            ? (initialUsedBytes ?? 0)
            : (provider?.BytesUsedOffset ?? 0);
        const resetAtToPersist = isNew && initialUsedBytes !== null
            ? Date.now()
            : (provider?.BytesUsedResetAt ?? 0);

        onSave({
            Type: type,
            Host: host,
            Port: parseInt(port, 10),
            UseSsl: useSsl,
            User: user,
            Pass: pass,
            MaxConnections: parseInt(maxConnections, 10),
            PreviousType: type === ProviderType.Disabled ? provider?.PreviousType : undefined,
            ByteLimit: byteLimit,
            BytesUsedOffset: offsetToPersist,
            BytesUsedResetAt: resetAtToPersist,
        });
    }, [type, host, port, useSsl, user, pass, maxConnections, provider, isEditing, limitValue, limitUnit, initialUsedValue, initialUsedUnit, onSave]);

    const handleOverlayClick = useCallback((e: React.MouseEvent) => {
        if (e.target === e.currentTarget) {
            onClose();
        }
    }, [onClose]);

    const isFormValid = host.trim() !== ""
        && isPositiveInteger(port)
        && user.trim() !== ""
        && pass.trim() !== ""
        && isPositiveInteger(maxConnections);

    const canSave = isFormValid && (connectionTested || type == ProviderType.Disabled);

    if (!show) return null;

    return (
        <div className={styles["modal-overlay"]} onClick={handleOverlayClick}>
            <div className={styles["modal-container"]}>
                <div className={styles["modal-header"]}>
                    <h2 className={styles["modal-title"]}>
                        {provider ? "Edit Provider" : "Add Provider"}
                    </h2>
                    <button className={styles["modal-close"]} onClick={onClose} aria-label="Close">
                        ×
                    </button>
                </div>

                <div className={styles["modal-body"]}>
                    <div className={styles["form-grid"]}>
                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-host" className={styles["form-label"]}>
                                Host
                            </label>
                            <input
                                type="text"
                                id="provider-host"
                                className={styles["form-input"]}
                                placeholder="news.provider.com"
                                value={host}
                                onChange={(e) => {
                                    setHost(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-port" className={styles["form-label"]}>
                                Port
                            </label>
                            <input
                                type="text"
                                id="provider-port"
                                className={`${styles["form-input"]} ${!isPositiveInteger(port) && port !== "" ? styles.error : ""}`}
                                placeholder="563"
                                value={port}
                                onChange={(e) => {
                                    setPort(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-user" className={styles["form-label"]}>
                                Username
                            </label>
                            <input
                                type="text"
                                id="provider-user"
                                className={styles["form-input"]}
                                placeholder="username"
                                value={user}
                                onChange={(e) => {
                                    setUser(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-pass" className={styles["form-label"]}>
                                Password
                            </label>
                            <input
                                type="password"
                                id="provider-pass"
                                className={styles["form-input"]}
                                placeholder="password"
                                value={pass}
                                onChange={(e) => {
                                    setPass(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-max-connections" className={styles["form-label"]}>
                                Max Connections
                            </label>
                            <input
                                type="text"
                                id="provider-max-connections"
                                className={`${styles["form-input"]} ${!isPositiveInteger(maxConnections) && maxConnections !== "" ? styles.error : ""}`}
                                placeholder="20"
                                value={maxConnections}
                                onChange={(e) => setMaxConnections(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-type" className={styles["form-label"]}>
                                Type
                            </label>
                            <select
                                id="provider-type"
                                className={styles["form-select"]}
                                value={type}
                                onChange={(e) => setType(parseInt(e.target.value, 10) as ProviderType)}
                            >
                                <option value={ProviderType.Disabled}>Disabled</option>
                                <option value={ProviderType.Pooled}>Pool Connections</option>
                                <option value={ProviderType.BackupOnly}>Backup Only</option>
                            </select>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-ssl"
                                    className={styles["form-checkbox"]}
                                    checked={useSsl}
                                    onChange={(e) => {
                                        setUseSsl(e.target.checked);
                                        setConnectionTested(false);
                                    }}
                                />
                                <label htmlFor="provider-ssl" className={styles["form-checkbox-label"]}>
                                    Use SSL
                                </label>
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label className={styles["form-label"]}>
                                Data Cap (optional)
                            </label>
                            <div className={styles["form-paired-input"]}>
                                <input
                                    type="text"
                                    inputMode="decimal"
                                    className={styles["form-input"]}
                                    placeholder="Leave blank for no cap"
                                    value={limitValue}
                                    onChange={(e) => setLimitValue(e.target.value)}
                                />
                                <select
                                    className={styles["form-select"]}
                                    value={limitUnit}
                                    onChange={(e) => setLimitUnit(e.target.value as ByteUnitLabel)}
                                >
                                    {BYTE_UNITS.map(u => (
                                        <option key={u.label} value={u.label}>{u.label}</option>
                                    ))}
                                </select>
                            </div>
                            <div className={styles["form-hint"]}>
                                For block accounts: total bytes you've purchased. The provider auto-pauses at ~95% of this value to absorb in-flight requests, so set the cap to your full block size. The 5% headroom keeps you from overshooting.
                            </div>
                        </div>

                        {!isEditing && (
                            <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                                <label className={styles["form-label"]}>
                                    Already Used (optional)
                                </label>
                                <div className={styles["form-paired-input"]}>
                                    <input
                                        type="text"
                                        inputMode="decimal"
                                        className={styles["form-input"]}
                                        placeholder="0"
                                        value={initialUsedValue}
                                        onChange={(e) => setInitialUsedValue(e.target.value)}
                                    />
                                    <select
                                        className={styles["form-select"]}
                                        value={initialUsedUnit}
                                        onChange={(e) => setInitialUsedUnit(e.target.value as ByteUnitLabel)}
                                    >
                                        {BYTE_UNITS.map(u => (
                                            <option key={u.label} value={u.label}>{u.label}</option>
                                        ))}
                                    </select>
                                </div>
                                <div className={styles["form-hint"]}>
                                    Seed the counter when migrating a partially-used block from another client. Leave empty for a fresh block.
                                </div>
                            </div>
                        )}
                    </div>

                    {testError && (
                        <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: '16px' }}>
                            {testError}
                        </div>
                    )}

                    {connectionTested && (
                        <div className={`${styles.alert} ${styles["alert-success"]}`} style={{ marginTop: '16px' }}>
                            Connection test successful!
                        </div>
                    )}
                </div>

                <div className={styles["modal-footer"]}>
                    <div className={styles["modal-footer-left"]}></div>
                    <div className={styles["modal-footer-right"]}>
                        <Button variant="secondary" onClick={onClose}>
                            Cancel
                        </Button>
                        {!canSave ? (
                            <Button
                                variant="primary"
                                onClick={handleTestConnection}
                                disabled={!isFormValid || isTestingConnection}
                            >
                                {isTestingConnection ? "Testing..." : "Test Connection"}
                            </Button>
                        ) : (
                            <Button variant="primary" onClick={handleSave} disabled={!canSave}>
                                Save Provider
                            </Button>
                        )}
                    </div>
                </div>
            </div>
        </div>
    );
}

export function isUsenetSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["usenet.providers"] !== newConfig["usenet.providers"]
}

export function isPositiveInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num > 0 && value.trim() === num.toString();
}