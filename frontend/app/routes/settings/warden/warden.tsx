import { Alert, Button, Form, Modal, Spinner } from "react-bootstrap";
import { type ChangeEvent, type DragEvent, type Dispatch, type SetStateAction, useEffect, useRef, useState } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import styles from "./warden.module.css";

type WardenSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

type Trust = "full" | "corroborate" | "observe";

type Source = {
    id: string;
    kind: "local" | "remote" | "imported";
    name: string;
    url?: string | null;
    enabled: boolean;
    trust: Trust;
    refreshHours: number;
    lastChecked: number;
    lastUpdated: number;
    status?: string | null;
    count: number;
};

type Snapshot = {
    quorum: number;
    localCount: number;
    effectiveCount: number;
    totalRows: number;
    sources: Source[];
};

type Status = { text: string; variant: "success" | "danger" } | null;

const TRUST_HELP: Record<Trust, string> = {
    full: "Filters on its own",
    corroborate: "Filters only when enough sources agree",
    observe: "Never filters (watch only)",
};

function ago(sec?: number) {
    if (!sec) return "never";
    const d = Date.now() / 1000 - sec;
    if (d < 60) return "just now";
    if (d < 3600) return `${Math.floor(d / 60)}m ago`;
    if (d < 86400) return `${Math.floor(d / 3600)}h ago`;
    return `${Math.floor(d / 86400)}d ago`;
}

function kindMeta(kind: Source["kind"]) {
    if (kind === "local") return { label: "Local", cls: styles.kindLocal };
    if (kind === "remote") return { label: "Remote", cls: styles.kindRemote };
    return { label: "Imported", cls: "" };
}

export function WardenSettings({ config, setNewConfig }: WardenSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const hideDead = (config["warden.hide-dead"] ?? "true") === "true";
    const quorum = config["warden.quorum"] ?? "2";

    const [snap, setSnap] = useState<Snapshot | null>(null);
    const [busy, setBusy] = useState<string | null>(null);
    const [message, setMessage] = useState<Status>(null);
    const [dragOver, setDragOver] = useState(false);
    const fileRef = useRef<HTMLInputElement>(null);

    const [showImport, setShowImport] = useState(false);
    const [importTarget, setImportTarget] = useState<"merge" | "separate">("separate");
    const [importName, setImportName] = useState("");
    const [importTrust, setImportTrust] = useState<Trust>("corroborate");
    const [pendingFile, setPendingFile] = useState<File | null>(null);

    const [showExport, setShowExport] = useState(false);
    const [exportScope, setExportScope] = useState<"local" | "merged">("local");
    const [exportSources, setExportSources] = useState<Set<string>>(new Set());
    const [exportDedup, setExportDedup] = useState(true);

    const [showAddRemote, setShowAddRemote] = useState(false);
    const [remoteUrl, setRemoteUrl] = useState("");
    const [remoteName, setRemoteName] = useState("");
    const [remoteInterval, setRemoteInterval] = useState("24");
    const [remoteTrust, setRemoteTrust] = useState<Trust>("corroborate");

    const [confirm, setConfirm] = useState<{ kind: "remove" | "clear"; source: Source } | null>(null);

    const refresh = async () => {
        try {
            const res = await fetch("/api/warden-sources");
            if (res.ok) setSnap(await res.json());
        } catch { /* ignore */ }
    };

    useEffect(() => { refresh(); }, []);

    useEffect(() => {
        if (message?.variant !== "success") return;
        const t = setTimeout(() => setMessage(null), 4000);
        return () => clearTimeout(t);
    }, [message]);

    const post = async (url: string, form: FormData): Promise<any> => {
        const res = await fetch(url, { method: "POST", body: form });
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(data.error || "Request failed.");
        return data;
    };

    const submitImport = async () => {
        if (!pendingFile) return;
        setBusy("import");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("file", pendingFile);
            form.append("target", importTarget);
            if (importTarget === "separate") {
                form.append("name", importName);
                form.append("trust", importTrust);
            }
            const data = await post("/api/warden-import", form);
            setMessage({ text: `Imported ${(data.added ?? 0).toLocaleString()} fingerprint${data.added === 1 ? "" : "s"}.`, variant: "success" });
            setShowImport(false);
            setPendingFile(null);
            setImportName("");
            await refresh();
        } catch (err: any) {
            setMessage({ text: err?.message || "Import failed.", variant: "danger" });
        } finally {
            setBusy(null);
            if (fileRef.current) fileRef.current.value = "";
        }
    };

    const onFilePicked = (e: ChangeEvent<HTMLInputElement>) => {
        const file = e.target.files?.[0];
        if (file) setPendingFile(file);
    };

    const onDrop = (e: DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        setDragOver(false);
        if (busy) return;
        const file = e.dataTransfer.files?.[0];
        if (file) {
            setPendingFile(file);
            setImportTarget("separate");
            setShowImport(true);
        }
    };

    const doExport = () => {
        const params = new URLSearchParams();
        if (exportScope === "merged") {
            params.set("scope", "merged");
            const ids = [...exportSources];
            if (ids.length) params.set("sources", ids.join(","));
        }
        params.set("dedup", exportDedup ? "1" : "0");
        const a = document.createElement("a");
        a.href = `/api/warden-export?${params.toString()}`;
        a.download = "warden.ndjson.gz";
        document.body.appendChild(a);
        a.click();
        a.remove();
        setShowExport(false);
    };

    const addRemoteSource = async () => {
        setBusy("add-remote");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("url", remoteUrl.trim());
            form.append("name", remoteName.trim());
            form.append("trust", remoteTrust);
            form.append("refreshHours", remoteInterval);
            const data = await post("/api/warden-source-add", form);
            const failed = (data.message ?? "").startsWith("error");
            setMessage(failed
                ? { text: `Added, but the first fetch failed: ${data.message}`, variant: "danger" }
                : { text: `Remote list added (${data.message}).`, variant: "success" });
            setShowAddRemote(false);
            setRemoteUrl("");
            setRemoteName("");
            await refresh();
        } catch (err: any) {
            setMessage({ text: err?.message || "Could not add the remote list.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const updateSource = async (id: string, fields: Record<string, string>) => {
        setBusy("src-" + id);
        try {
            const form = new FormData();
            form.append("id", id);
            for (const [k, v] of Object.entries(fields)) form.append(k, v);
            await post("/api/warden-source-update", form);
            await refresh();
        } catch (err: any) {
            setMessage({ text: err?.message || "Update failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const refreshSource = async (id: string) => {
        setBusy("src-" + id);
        setMessage(null);
        try {
            const form = new FormData();
            form.append("id", id);
            const data = await post("/api/warden-source-refresh", form);
            const failed = (data.message ?? "").startsWith("error");
            setMessage({ text: `Refresh: ${data.message}`, variant: failed ? "danger" : "success" });
            await refresh();
        } catch (err: any) {
            setMessage({ text: err?.message || "Refresh failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const runConfirm = async () => {
        if (!confirm) return;
        const { kind, source } = confirm;
        setConfirm(null);
        setBusy("src-" + source.id);
        setMessage(null);
        try {
            const form = new FormData();
            form.append("id", source.id);
            if (kind === "clear") form.append("action", "clear");
            const data = await post("/api/warden-source-remove", form);
            setMessage({
                text: kind === "clear"
                    ? `Cleared ${(data.removed ?? 0).toLocaleString()} fingerprint${data.removed === 1 ? "" : "s"}.`
                    : `Removed “${source.name}”.`,
                variant: "success",
            });
            await refresh();
        } catch (err: any) {
            setMessage({ text: err?.message || "Action failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const sources = snap?.sources ?? [];

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionTitle}>Warden</div>
                <div className={styles.sectionDescription}>
                    A portable filter list of dead-release fingerprints. Your own list fills in
                    automatically and stays independent. You can also add remote lists from a URL;
                    they refresh on their own schedule, never touch your own list, and you decide how
                    much each one is trusted. Fingerprints are universal: identical on any provider,
                    indexer, or server, and free of credentials.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Check
                    type="switch"
                    id="warden-hide-dead"
                    label="Filter out anything on the list"
                    checked={hideDead}
                    onChange={e => set("warden.hide-dead", String(e.target.checked))} />
                <p className={styles.hint}>
                    When on, anything whose fingerprint is filtered by your sources is removed from
                    what your search profiles return. If everything matches, results are shown anyway
                    as a last resort.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Agreement needed for shared lists</Form.Label>
                <Form.Control className={styles.input} type="number" min={1} max={20}
                    value={quorum}
                    onChange={e => set("warden.quorum", String(Math.max(1, parseInt(e.target.value || "1", 10))))} />
                <p className={styles.hint}>
                    A fingerprint from a “corroborate” source only filters when at least this many
                    independent sources agree. Your own list and “full”-trust sources always filter
                    on their own.
                </p>
            </Form.Group>

            <div className={`${styles.section} ${styles.sourcesSection}`}>
                <div className={styles.sectionHeader}>
                    <div>
                        <div className={styles.sectionTitle}>Sources</div>
                        <div className={styles.sectionDescription}>
                            {snap === null
                                ? "Loading…"
                                : `Filtering now: ${snap.effectiveCount.toLocaleString()} · ${snap.totalRows.toLocaleString()} total across all sources`}
                        </div>
                    </div>
                    <div className={styles.headerActions}>
                        <Button variant="primary" size="sm" onClick={() => setShowAddRemote(true)}>Add remote list</Button>
                        <Button variant="outline-secondary" size="sm" disabled={busy !== null}
                            onClick={() => { setPendingFile(null); setShowImport(true); }}>
                            Import
                        </Button>
                        <Button variant="outline-secondary" size="sm" disabled={!snap || snap.totalRows === 0}
                            onClick={() => {
                                setExportScope("local");
                                setExportSources(new Set(sources.map(s => s.id)));
                                setShowExport(true);
                            }}>
                            Export
                        </Button>
                    </div>
                </div>

                <p className={styles.hint}>
                    Each source has a trust level. <b>full</b> filters on its own;{" "}
                    <b>corroborate</b> filters only when enough sources agree (the number above);{" "}
                    <b>observe</b> keeps the list but never filters.
                </p>

                <div
                    className={`${styles.list} ${dragOver ? styles.listDrop : ""}`}
                    onDragOver={e => { e.preventDefault(); if (!busy) setDragOver(true); }}
                    onDragLeave={e => { e.preventDefault(); setDragOver(false); }}
                    onDrop={onDrop}>
                    {sources.map(s => {
                        const isLocal = s.kind === "local";
                        const rowBusy = busy === "src-" + s.id;
                        const statusErr = (s.status ?? "").startsWith("error");
                        const km = kindMeta(s.kind);
                        return (
                            <div key={s.id} className={`${styles.card} ${!s.enabled ? styles.cardDisabled : ""}`}>
                                <div className={styles.cardHeader}>
                                    <div className={styles.cardTitle}>
                                        <span className={styles.name}>{s.name}</span>
                                        <span className={`${styles.kind} ${km.cls}`}>{km.label}</span>
                                        {rowBusy && <Spinner as="span" animation="border" size="sm" />}
                                    </div>
                                    <div className={styles.actions}>
                                        {s.kind === "remote" &&
                                            <Button variant="outline-secondary" size="sm" disabled={rowBusy} onClick={() => refreshSource(s.id)}>
                                                Refresh
                                            </Button>}
                                        <Button variant="outline-secondary" size="sm" disabled={rowBusy || s.count === 0}
                                            onClick={() => setConfirm({ kind: "clear", source: s })}>
                                            Clear
                                        </Button>
                                        {!isLocal &&
                                            <Button variant="outline-danger" size="sm" disabled={rowBusy}
                                                onClick={() => setConfirm({ kind: "remove", source: s })}>
                                                Remove
                                            </Button>}
                                    </div>
                                </div>

                                {s.url && <div className={styles.url}>{s.url}</div>}

                                <div className={styles.meta}>
                                    <span className={styles.count}>{s.count.toLocaleString()} fingerprints</span>
                                    {isLocal
                                        ? <span className={styles.metaLabel}>Trust: full · always on</span>
                                        : <div className={styles.metaItem}>
                                            <span className={styles.metaLabel}>Trust</span>
                                            <Form.Select size="sm" className={styles.trustSelect} value={s.trust} disabled={rowBusy}
                                                onChange={e => updateSource(s.id, { trust: e.target.value })}>
                                                <option value="full">full</option>
                                                <option value="corroborate">corroborate</option>
                                                <option value="observe">observe</option>
                                            </Form.Select>
                                            <span className={styles.metaLabel}>{TRUST_HELP[s.trust]}</span>
                                        </div>}

                                    {!isLocal &&
                                        <Form.Check type="switch" id={`enabled-${s.id}`} label="Enabled"
                                            checked={s.enabled} disabled={rowBusy}
                                            onChange={e => updateSource(s.id, { enabled: String(e.target.checked) })} />}

                                    {s.kind === "remote" &&
                                        <span className={`${styles.status} ${statusErr ? styles.statusError : ""}`}>
                                            every {s.refreshHours}h · updated {ago(s.lastUpdated)}{s.status ? ` · ${s.status}` : ""}
                                        </span>}
                                </div>
                            </div>
                        );
                    })}
                </div>

                <p className={styles.dropHint}>
                    {dragOver ? "Drop to import" : "Drag & drop a warden file here to import."}
                </p>
            </div>

            {message &&
                <Alert variant={message.variant} dismissible onClose={() => setMessage(null)} className={styles.alert}>
                    {message.text}
                </Alert>}

            <input ref={fileRef} type="file" accept=".gz,.ndjson,.json,application/gzip,application/json"
                style={{ display: "none" }} onChange={onFilePicked} />

            <Modal show={showAddRemote} onHide={() => setShowAddRemote(false)} centered>
                <Modal.Header closeButton><Modal.Title>Add a remote list</Modal.Title></Modal.Header>
                <Modal.Body>
                    <Form.Group className={styles.modalGroup}>
                        <Form.Label>List URL</Form.Label>
                        <Form.Control type="url" placeholder="https://raw.githubusercontent.com/…/warden.ndjson.gz"
                            value={remoteUrl} onChange={e => setRemoteUrl(e.target.value)} />
                        <Form.Text muted>A raw .ndjson or .ndjson.gz file. GitHub raw URLs work great.</Form.Text>
                    </Form.Group>
                    <Form.Group className={styles.modalGroup}>
                        <Form.Label>Name (optional)</Form.Label>
                        <Form.Control value={remoteName} onChange={e => setRemoteName(e.target.value)} placeholder="Shared list" />
                    </Form.Group>
                    <div className={styles.modalRow}>
                        <Form.Group style={{ flex: 1 }}>
                            <Form.Label>Trust</Form.Label>
                            <Form.Select value={remoteTrust} onChange={e => setRemoteTrust(e.target.value as Trust)}>
                                <option value="corroborate">corroborate (recommended)</option>
                                <option value="full">full</option>
                                <option value="observe">observe</option>
                            </Form.Select>
                            <Form.Text muted>{TRUST_HELP[remoteTrust]}</Form.Text>
                        </Form.Group>
                        <Form.Group style={{ width: 130 }}>
                            <Form.Label>Refresh (h)</Form.Label>
                            <Form.Control type="number" min={1} max={720} value={remoteInterval}
                                onChange={e => setRemoteInterval(e.target.value)} />
                        </Form.Group>
                    </div>
                </Modal.Body>
                <Modal.Footer>
                    <Button variant="secondary" onClick={() => setShowAddRemote(false)}>Cancel</Button>
                    <Button variant="primary" disabled={busy === "add-remote" || !remoteUrl.trim()} onClick={addRemoteSource}>
                        {busy === "add-remote" ? <><Spinner as="span" animation="border" size="sm" /> Adding…</> : "Add & fetch"}
                    </Button>
                </Modal.Footer>
            </Modal>

            <Modal show={showImport} onHide={() => setShowImport(false)} centered>
                <Modal.Header closeButton><Modal.Title>Import a warden file</Modal.Title></Modal.Header>
                <Modal.Body>
                    <Form.Check type="radio" name="import-target" id="import-separate" label="Keep as a separate source (recommended)"
                        checked={importTarget === "separate"} onChange={() => setImportTarget("separate")} />
                    <div className={styles.choiceHint}>
                        Stays isolated and reversible: one click to remove later. Best for lists from other people.
                    </div>
                    <Form.Check type="radio" name="import-target" id="import-merge" label="Merge into my list"
                        checked={importTarget === "merge"} onChange={() => setImportTarget("merge")} />
                    <div className={styles.choiceHint}>
                        Folds the fingerprints into your own list. Can’t be un-merged.
                    </div>

                    {importTarget === "separate" &&
                        <div className={styles.modalRow} style={{ marginBottom: 14 }}>
                            <Form.Group style={{ flex: 1 }}>
                                <Form.Label>Name</Form.Label>
                                <Form.Control value={importName} onChange={e => setImportName(e.target.value)} placeholder="Imported list" />
                            </Form.Group>
                            <Form.Group style={{ width: 180 }}>
                                <Form.Label>Trust</Form.Label>
                                <Form.Select value={importTrust} onChange={e => setImportTrust(e.target.value as Trust)}>
                                    <option value="corroborate">corroborate</option>
                                    <option value="full">full</option>
                                    <option value="observe">observe</option>
                                </Form.Select>
                                <Form.Text muted>{TRUST_HELP[importTrust]}</Form.Text>
                            </Form.Group>
                        </div>}

                    <div className={styles.fileRow}>
                        <Button variant="outline-secondary" size="sm" onClick={() => fileRef.current?.click()}>Choose file…</Button>
                        <span className={styles.fileName}>{pendingFile ? pendingFile.name : "No file selected"}</span>
                    </div>
                </Modal.Body>
                <Modal.Footer>
                    <Button variant="secondary" onClick={() => setShowImport(false)}>Cancel</Button>
                    <Button variant="primary" disabled={busy === "import" || !pendingFile} onClick={submitImport}>
                        {busy === "import" ? <><Spinner as="span" animation="border" size="sm" /> Importing…</> : "Import"}
                    </Button>
                </Modal.Footer>
            </Modal>

            <Modal show={showExport} onHide={() => setShowExport(false)} centered>
                <Modal.Header closeButton><Modal.Title>Export</Modal.Title></Modal.Header>
                <Modal.Body>
                    <Form.Check type="radio" name="export-scope" id="export-local" label="My list only"
                        checked={exportScope === "local"} onChange={() => setExportScope("local")} />
                    <div className={styles.choiceHint}>
                        Just your own verdicts: the clean file others can trust. Publish it and share the URL.
                    </div>
                    <Form.Check type="radio" name="export-scope" id="export-merged" label="Merged from selected sources"
                        checked={exportScope === "merged"} onChange={() => setExportScope("merged")} />
                    {exportScope === "merged" &&
                        <div style={{ margin: "8px 0 12px 26px" }}>
                            {sources.map(s =>
                                <Form.Check key={s.id} type="checkbox" id={`exp-${s.id}`} label={`${s.name} (${s.count.toLocaleString()})`}
                                    checked={exportSources.has(s.id)}
                                    onChange={e => {
                                        const next = new Set(exportSources);
                                        if (e.target.checked) next.add(s.id); else next.delete(s.id);
                                        setExportSources(next);
                                    }} />)}
                        </div>}
                    <Form.Check type="switch" id="export-dedup" label="Deduplicate identical fingerprints"
                        checked={exportDedup} onChange={e => setExportDedup(e.target.checked)} style={{ marginTop: 8 }} />
                </Modal.Body>
                <Modal.Footer>
                    <Button variant="secondary" onClick={() => setShowExport(false)}>Cancel</Button>
                    <Button variant="primary" disabled={exportScope === "merged" && exportSources.size === 0} onClick={doExport}>
                        Download
                    </Button>
                </Modal.Footer>
            </Modal>

            <ConfirmModal
                show={confirm !== null}
                title={confirm?.kind === "remove" ? "Remove this source?" : "Clear this source?"}
                message={confirm?.kind === "remove"
                    ? `This removes “${confirm?.source.name}” and its ${(confirm?.source.count ?? 0).toLocaleString()} fingerprints. You can add it again later.`
                    : `This empties “${confirm?.source.name}” (${(confirm?.source.count ?? 0).toLocaleString()} fingerprints).${confirm?.source.kind === "local" ? " Your list will repopulate automatically over time." : ""}`}
                cancelText="Cancel"
                confirmText={confirm?.kind === "remove" ? "Remove" : "Clear"}
                onCancel={() => setConfirm(null)}
                onConfirm={runConfirm} />
        </div>
    );
}

export function isWardenSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["warden.hide-dead"] !== newConfig["warden.hide-dead"]
        || config["warden.quorum"] !== newConfig["warden.quorum"];
}
