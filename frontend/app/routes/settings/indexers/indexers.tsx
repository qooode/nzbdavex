import { Button, Spinner } from "react-bootstrap";
import styles from "./indexers.module.css";
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect, useMemo } from "react";

type IndexersSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

interface ConnectionDetails {
    Name: string;
    Url: string;
    ApiKey: string;
    Enabled: boolean;
    UserAgent?: string;
    MaxRequestsPerMinute?: number;
    EnableStrictMatching?: boolean;
}

interface IndexerConfig {
    Indexers: ConnectionDetails[];
}

function parseConfig(raw: string): IndexerConfig {
    try {
        const parsed = JSON.parse(raw || "{}");
        return { Indexers: parsed.Indexers ?? [] };
    } catch {
        return { Indexers: [] };
    }
}

function serializeConfig(c: IndexerConfig): string {
    return JSON.stringify(c);
}

export function IndexersSettings({ config, setNewConfig }: IndexersSettingsProps) {
    const indexerConfig = useMemo(() => parseConfig(config["indexers.instances"]), [config]);
    const [showModal, setShowModal] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);

    const handleAdd = useCallback(() => {
        setEditingIndex(null);
        setShowModal(true);
    }, []);

    const handleEdit = useCallback((index: number) => {
        setEditingIndex(index);
        setShowModal(true);
    }, []);

    const handleDelete = useCallback((index: number) => {
        const next: IndexerConfig = {
            Indexers: indexerConfig.Indexers.filter((_, i) => i !== index),
        };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleToggle = useCallback((index: number) => {
        const next: IndexerConfig = {
            Indexers: indexerConfig.Indexers.map((x, i) =>
                i === index ? { ...x, Enabled: !x.Enabled } : x
            ),
        };
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
    }, [config, indexerConfig, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSave = useCallback((indexer: ConnectionDetails) => {
        const next: IndexerConfig = { Indexers: [...indexerConfig.Indexers] };
        if (editingIndex !== null) {
            next.Indexers[editingIndex] = indexer;
        } else {
            next.Indexers.push(indexer);
        }
        setNewConfig({ ...config, "indexers.instances": serializeConfig(next) });
        handleCloseModal();
    }, [config, indexerConfig, editingIndex, setNewConfig, handleCloseModal]);

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Newznab Indexers</div>
                    <Button variant="primary" size="sm" onClick={handleAdd}>Add</Button>
                </div>

                {indexerConfig.Indexers.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No indexers configured. Add a Newznab-compatible indexer (e.g. NZBgeek, NZBHydra2, Prowlarr) to enable search.
                    </p>
                ) : (
                    <div className={styles["indexers-grid"]}>
                        {indexerConfig.Indexers.map((indexer, index) => (
                            <IndexerCard
                                key={index}
                                indexer={indexer}
                                onEdit={() => handleEdit(index)}
                                onToggle={() => handleToggle(index)}
                                onDelete={() => handleDelete(index)}
                            />
                        ))}
                    </div>
                )}
            </div>

            <IndexerModal
                show={showModal}
                indexer={editingIndex !== null ? indexerConfig.Indexers[editingIndex] : null}
                onClose={handleCloseModal}
                onSave={handleSave}
            />
        </div>
    );
}

type IndexerCardProps = {
    indexer: ConnectionDetails;
    onEdit: () => void;
    onToggle: () => void;
    onDelete: () => void;
};

function IndexerCard({ indexer, onEdit, onToggle, onDelete }: IndexerCardProps) {
    const isDisabled = !indexer.Enabled;
    const host = (() => {
        try { return new URL(indexer.Url).host; }
        catch { return indexer.Url || "—"; }
    })();
    const rateLimit = indexer.MaxRequestsPerMinute && indexer.MaxRequestsPerMinute > 0
        ? `${indexer.MaxRequestsPerMinute} / min`
        : "Unlimited";
    const userAgent = indexer.UserAgent?.trim() ? indexer.UserAgent : "Default";

    return (
        <div className={`${styles["indexer-card"]} ${isDisabled ? styles["indexer-card-disabled"] : ""}`}>
            <div className={styles["indexer-card-inner"]}>
                <div className={styles["indexer-header"]}>
                    <div className={styles["indexer-header-content"]}>
                        <div className={styles["indexer-name"]}>
                            {indexer.Name || "(unnamed)"}
                            {isDisabled && <span className={styles["indexer-disabled-badge"]}>Disabled</span>}
                        </div>
                        <div className={styles["indexer-host"]}>{host}</div>
                    </div>
                    <div className={styles["indexer-header-actions"]}>
                        <button
                            className={`${styles["header-action-button"]} ${styles["toggle"]} ${isDisabled ? styles["toggle-off"] : styles["toggle-on"]}`}
                            onClick={onToggle}
                            title={isDisabled ? "Enable Indexer" : "Disable Indexer"}
                            aria-pressed={!isDisabled}
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                                <line x1="12" y1="2" x2="12" y2="12" />
                            </svg>
                        </button>
                        <button
                            className={styles["header-action-button"]}
                            onClick={onEdit}
                            title="Edit Indexer"
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                                <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                            </svg>
                        </button>
                        <button
                            className={`${styles["header-action-button"]} ${styles["delete"]}`}
                            onClick={onDelete}
                            title="Delete Indexer"
                        >
                            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                <polyline points="3 6 5 6 21 6" />
                                <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                            </svg>
                        </button>
                    </div>
                </div>

                <div className={styles["indexer-details"]}>
                    <div className={styles["indexer-detail-row"]}>
                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="12" cy="12" r="10" />
                                    <line x1="2" y1="12" x2="22" y2="12" />
                                    <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Host</span>
                                <span className={styles["indexer-detail-value"]} title={indexer.Url}>{host}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <circle cx="12" cy="12" r="10" />
                                    <polyline points="12 6 12 12 16 14" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Rate limit</span>
                                <span className={styles["indexer-detail-value"]}>{rateLimit}</span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M9 12l2 2 4-4" />
                                    <path d="M21 12c0 4.97-4.03 9-9 9s-9-4.03-9-9 4.03-9 9-9 9 4.03 9 9z" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>Strict matching</span>
                                <span className={styles["indexer-detail-value"]}>
                                    {indexer.EnableStrictMatching ? "Enabled" : "Disabled"}
                                </span>
                            </div>
                        </div>

                        <div className={styles["indexer-detail-item"]}>
                            <div className={styles["indexer-detail-icon"]}>
                                <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                    <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
                                    <circle cx="12" cy="7" r="4" />
                                </svg>
                            </div>
                            <div className={styles["indexer-detail-content"]}>
                                <span className={styles["indexer-detail-label"]}>User-Agent</span>
                                <span className={styles["indexer-detail-value"]} title={indexer.UserAgent ?? ""}>{userAgent}</span>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    );
}

type IndexerModalProps = {
    show: boolean;
    indexer: ConnectionDetails | null;
    onClose: () => void;
    onSave: (indexer: ConnectionDetails) => void;
};

function IndexerModal({ show, indexer, onClose, onSave }: IndexerModalProps) {
    const [name, setName] = useState("");
    const [url, setUrl] = useState("");
    const [apiKey, setApiKey] = useState("");
    const [userAgent, setUserAgent] = useState("");
    const [maxRpm, setMaxRpm] = useState("0");
    const [enabled, setEnabled] = useState(true);
    const [strict, setStrict] = useState(false);

    const [testState, setTestState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');

    useEffect(() => {
        if (show) {
            setName(indexer?.Name || "");
            setUrl(indexer?.Url || "");
            setApiKey(indexer?.ApiKey || "");
            setUserAgent(indexer?.UserAgent || "");
            setMaxRpm((indexer?.MaxRequestsPerMinute ?? 0).toString());
            setEnabled(indexer?.Enabled ?? true);
            setStrict(indexer?.EnableStrictMatching ?? false);
            setTestState('idle');
        }
    }, [show, indexer]);

    useEffect(() => { setTestState('idle'); }, [url, apiKey, userAgent]);

    useEffect(() => {
        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && show) onClose();
        };
        if (show) {
            document.addEventListener('keydown', handleEscape);
            return () => document.removeEventListener('keydown', handleEscape);
        }
    }, [show, onClose]);

    const handleTest = useCallback(async () => {
        if (!url.trim() || !apiKey.trim()) return;
        setTestState('testing');
        try {
            const fd = new FormData();
            fd.append('url', url);
            fd.append('apiKey', apiKey);
            if (userAgent.trim()) fd.append('userAgent', userAgent);
            const r = await fetch('/api/test-indexer-connection', { method: 'POST', body: fd });
            const data = await r.json();
            setTestState(data.status && data.connected ? 'success' : 'error');
        } catch {
            setTestState('error');
        }
    }, [url, apiKey, userAgent]);

    const handleSave = useCallback(() => {
        const rpm = parseInt(maxRpm || "0", 10);
        onSave({
            Name: name.trim(),
            Url: url.trim(),
            ApiKey: apiKey.trim(),
            Enabled: enabled,
            UserAgent: userAgent.trim() || undefined,
            MaxRequestsPerMinute: Number.isFinite(rpm) && rpm > 0 ? rpm : 0,
            EnableStrictMatching: strict,
        });
    }, [name, url, apiKey, userAgent, maxRpm, enabled, strict, onSave]);

    const handleOverlayClick = useCallback((e: React.MouseEvent) => {
        if (e.target === e.currentTarget) onClose();
    }, [onClose]);

    const isUrlValid = (() => {
        if (!url.trim()) return false;
        try { new URL(url); return true; } catch { return false; }
    })();
    const isRpmValid = (() => {
        const n = Number(maxRpm);
        return Number.isInteger(n) && n >= 0 && maxRpm.trim() === n.toString();
    })();
    const isFormValid = name.trim() !== "" && isUrlValid && apiKey.trim() !== "" && isRpmValid;

    if (!show) return null;

    return (
        <div className={styles["modal-overlay"]} onClick={handleOverlayClick}>
            <div className={styles["modal-container"]}>
                <div className={styles["modal-header"]}>
                    <h2 className={styles["modal-title"]}>
                        {indexer ? "Edit Indexer" : "Add Indexer"}
                    </h2>
                    <button className={styles["modal-close"]} onClick={onClose} aria-label="Close">×</button>
                </div>

                <div className={styles["modal-body"]}>
                    <div className={styles["form-grid"]}>
                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-name" className={styles["form-label"]}>Name</label>
                            <input
                                type="text"
                                id="indexer-name"
                                className={styles["form-input"]}
                                placeholder="e.g. NZBgeek"
                                value={name}
                                onChange={e => setName(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-url" className={styles["form-label"]}>URL</label>
                            <input
                                type="text"
                                id="indexer-url"
                                className={`${styles["form-input"]} ${!isUrlValid && url !== "" ? styles.error : ""}`}
                                placeholder="https://api.nzbgeek.info"
                                value={url}
                                onChange={e => setUrl(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-apikey" className={styles["form-label"]}>API Key</label>
                            <input
                                type="password"
                                id="indexer-apikey"
                                className={styles["form-input"]}
                                value={apiKey}
                                onChange={e => setApiKey(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <label htmlFor="indexer-ua" className={styles["form-label"]}>
                                User-Agent <span className={styles["label-hint"]}>(optional)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-ua"
                                className={styles["form-input"]}
                                placeholder="Leave blank to use global default"
                                value={userAgent}
                                onChange={e => setUserAgent(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="indexer-rpm" className={styles["form-label"]}>
                                Max requests / minute <span className={styles["label-hint"]}>(0 = unlimited)</span>
                            </label>
                            <input
                                type="text"
                                id="indexer-rpm"
                                className={`${styles["form-input"]} ${!isRpmValid && maxRpm !== "" ? styles.error : ""}`}
                                placeholder="0"
                                value={maxRpm}
                                onChange={e => setMaxRpm(e.target.value)}
                            />
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="indexer-enabled"
                                    className={styles["form-checkbox"]}
                                    checked={enabled}
                                    onChange={e => setEnabled(e.target.checked)}
                                />
                                <label htmlFor="indexer-enabled" className={styles["form-checkbox-label"]}>
                                    Enabled
                                </label>
                            </div>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="indexer-strict"
                                    className={styles["form-checkbox"]}
                                    checked={strict}
                                    onChange={e => setStrict(e.target.checked)}
                                />
                                <label htmlFor="indexer-strict" className={styles["form-checkbox-label"]}>
                                    Strict matching <span className={styles["label-hint"]}>(drop results whose title doesn't match the request)</span>
                                </label>
                            </div>
                        </div>
                    </div>

                    {testState === 'error' && (
                        <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: '16px' }}>
                            Connection test failed
                        </div>
                    )}

                    {testState === 'success' && (
                        <div className={`${styles.alert} ${styles["alert-success"]}`} style={{ marginTop: '16px' }}>
                            Connection test successful!
                        </div>
                    )}
                </div>

                <div className={styles["modal-footer"]}>
                    <div className={styles["modal-footer-left"]}>
                        <Button
                            variant={testState === 'success' ? 'success' : testState === 'error' ? 'danger' : 'secondary'}
                            onClick={handleTest}
                            disabled={!isUrlValid || !apiKey.trim() || testState === 'testing'}
                        >
                            {testState === 'testing'
                                ? <Spinner animation="border" size="sm" />
                                : testState === 'success'
                                    ? '✓ Tested'
                                    : testState === 'error'
                                        ? '✗ Failed'
                                        : 'Test Connection'}
                        </Button>
                    </div>
                    <div className={styles["modal-footer-right"]}>
                        <Button variant="secondary" onClick={onClose}>Cancel</Button>
                        <Button variant="primary" onClick={handleSave} disabled={!isFormValid}>
                            {indexer ? "Save Indexer" : "Add Indexer"}
                        </Button>
                    </div>
                </div>
            </div>
        </div>
    );
}

export function isIndexersSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["indexers.instances"] !== newConfig["indexers.instances"];
}

export function isIndexersSettingsValid(newConfig: Record<string, string>) {
    try {
        const c = parseConfig(newConfig["indexers.instances"]);
        for (const i of c.Indexers) {
            if (!i.Name.trim()) return false;
            if (!i.ApiKey.trim()) return false;
            try { new URL(i.Url); } catch { return false; }
        }
        return true;
    } catch {
        return false;
    }
}
