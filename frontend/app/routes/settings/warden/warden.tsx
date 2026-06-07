import { Alert, Badge, Button, Form, Modal, Spinner } from "react-bootstrap";
import { type ChangeEvent, type DragEvent, type Dispatch, type SetStateAction, useEffect, useRef, useState } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";

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
    observe: "Watch only — never filters",
};

function ago(sec?: number) {
    if (!sec) return "never";
    const d = Date.now() / 1000 - sec;
    if (d < 60) return "just now";
    if (d < 3600) return `${Math.floor(d / 60)}m ago`;
    if (d < 86400) return `${Math.floor(d / 3600)}h ago`;
    return `${Math.floor(d / 86400)}d ago`;
}

function kindBadge(kind: Source["kind"]) {
    if (kind === "local") return <Badge bg="primary">Local</Badge>;
    if (kind === "remote") return <Badge bg="info">Remote</Badge>;
    return <Badge bg="secondary">Imported</Badge>;
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
            const msg = (data.message ?? "").startsWith("error")
                ? { text: `Added, but the first fetch failed: ${data.message}`, variant: "danger" as const }
                : { text: `Remote list added (${data.message}).`, variant: "success" as const };
            setMessage(msg);
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
        <div style={{ padding: 16, maxWidth: 820 }}>
            <div style={{ marginBottom: 16 }}>
                <div style={{ fontSize: 18, fontWeight: 600 }}>Warden</div>
                <div style={{ opacity: 0.7, fontSize: 14 }}>
                    A portable filter list of dead-release fingerprints. Your own list fills in
                    automatically and stays independent. You can also add remote lists from a URL —
                    they refresh on their own schedule, never touch your own list, and you decide how
                    much each one is trusted. Fingerprints are universal: identical on any provider,
                    indexer, or server, and free of credentials.
                </div>
            </div>

            <Form.Group style={{ marginBottom: 16 }}>
                <Form.Check
                    type="switch"
                    id="warden-hide-dead"
                    label="Filter out anything on the list"
                    checked={hideDead}
                    onChange={e => set("warden.hide-dead", String(e.target.checked))} />
                <Form.Text muted>
                    When on, anything whose fingerprint is filtered by your sources is removed from
                    what your search profiles return. If everything matches, results are shown anyway
                    as a last resort.
                </Form.Text>
            </Form.Group>

            <Form.Group style={{ marginBottom: 16, maxWidth: 360 }}>
                <Form.Label style={{ marginBottom: 4, fontSize: 14, fontWeight: 600 }}>
                    Agreement needed for shared lists
                </Form.Label>
                <Form.Control
                    type="number"
                    min={1}
                    max={20}
                    value={quorum}
                    onChange={e => set("warden.quorum", String(Math.max(1, parseInt(e.target.value || "1", 10))))} />
                <Form.Text muted>
                    A fingerprint from a “corroborate” source only filters when at least this many
                    independent sources agree. Your own list and “full”-trust sources always filter
                    on their own.
                </Form.Text>
            </Form.Group>

            <hr />

            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", marginBottom: 12, flexWrap: "wrap", gap: 8 }}>
                <div style={{ fontWeight: 600 }}>
                    {snap === null
                        ? "Loading sources…"
                        : `Filtering now: ${snap.effectiveCount.toLocaleString()} fingerprint${snap.effectiveCount === 1 ? "" : "s"}`}
                </div>
                {snap !== null &&
                    <div style={{ fontSize: 13, opacity: 0.6 }}>
                        {snap.totalRows.toLocaleString()} across all sources
                    </div>}
            </div>

            <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                {sources.map(s => {
                    const isLocal = s.kind === "local";
                    const rowBusy = busy === "src-" + s.id;
                    const statusErr = (s.status ?? "").startsWith("error");
                    return (
                        <div key={s.id} style={{
                            border: "1px solid rgba(128,128,128,0.3)",
                            borderRadius: 8,
                            padding: "10px 12px",
                            opacity: s.enabled ? 1 : 0.55,
                        }}>
                            <div style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
                                <span style={{ fontWeight: 600 }}>{s.name}</span>
                                {kindBadge(s.kind)}
                                <span style={{ fontSize: 13, opacity: 0.7 }}>{s.count.toLocaleString()} fingerprints</span>
                                {rowBusy && <Spinner as="span" animation="border" size="sm" />}
                                <div style={{ marginLeft: "auto", display: "flex", gap: 6, alignItems: "center" }}>
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

                            {s.url &&
                                <div style={{ fontSize: 12, opacity: 0.55, marginTop: 4, wordBreak: "break-all" }}>{s.url}</div>}

                            <div style={{ display: "flex", gap: 16, alignItems: "center", flexWrap: "wrap", marginTop: 8 }}>
                                {isLocal
                                    ? <span style={{ fontSize: 13, opacity: 0.7 }}>Trust: full (always on)</span>
                                    : <Form.Group style={{ display: "flex", alignItems: "center", gap: 6 }}>
                                        <Form.Label style={{ margin: 0, fontSize: 13 }}>Trust</Form.Label>
                                        <Form.Select size="sm" style={{ width: "auto" }} value={s.trust} disabled={rowBusy}
                                            onChange={e => updateSource(s.id, { trust: e.target.value })}>
                                            <option value="full">full</option>
                                            <option value="corroborate">corroborate</option>
                                            <option value="observe">observe</option>
                                        </Form.Select>
                                        <span style={{ fontSize: 12, opacity: 0.55 }}>{TRUST_HELP[s.trust]}</span>
                                    </Form.Group>}

                                {!isLocal &&
                                    <Form.Check
                                        type="switch"
                                        id={`enabled-${s.id}`}
                                        label="Enabled"
                                        checked={s.enabled}
                                        disabled={rowBusy}
                                        onChange={e => updateSource(s.id, { enabled: String(e.target.checked) })} />}

                                {s.kind === "remote" &&
                                    <span style={{ fontSize: 12, marginLeft: "auto", color: statusErr ? "#dc3545" : undefined, opacity: statusErr ? 1 : 0.55 }}>
                                        every {s.refreshHours}h · updated {ago(s.lastUpdated)}{s.status ? ` · ${s.status}` : ""}
                                    </span>}
                            </div>
                        </div>
                    );
                })}
            </div>

            <div
                onDragOver={e => { e.preventDefault(); if (!busy) setDragOver(true); }}
                onDragLeave={e => { e.preventDefault(); setDragOver(false); }}
                onDrop={onDrop}
                style={{
                    border: `1px dashed ${dragOver ? "#6ea8fe" : "rgba(128,128,128,0.45)"}`,
                    borderRadius: 8,
                    padding: 12,
                    marginTop: 14,
                    background: dragOver ? "rgba(110,168,254,0.10)" : "transparent",
                    transition: "border-color 120ms ease, background 120ms ease",
                }}>
                <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                    <Button variant="primary" size="sm" onClick={() => setShowAddRemote(true)}>Add remote list…</Button>
                    <Button variant="secondary" size="sm" disabled={busy !== null}
                        onClick={() => { setPendingFile(null); setShowImport(true); }}>
                        Import…
                    </Button>
                    <Button variant="secondary" size="sm" disabled={!snap || snap.totalRows === 0}
                        onClick={() => {
                            setExportScope("local");
                            setExportSources(new Set(sources.map(s => s.id)));
                            setShowExport(true);
                        }}>
                        Export…
                    </Button>
                </div>
                <div style={{ marginTop: 8, fontSize: 13, opacity: dragOver ? 0.95 : 0.55 }}>
                    {dragOver ? "Drop to import" : "…or drag & drop a warden file here to import."}
                </div>
            </div>

            {message &&
                <Alert variant={message.variant} dismissible onClose={() => setMessage(null)}
                    style={{ marginTop: 12, marginBottom: 0, fontSize: 14, padding: "8px 12px" }}>
                    {message.text}
                </Alert>}

            <Modal show={showAddRemote} onHide={() => setShowAddRemote(false)} centered>
                <Modal.Header closeButton><Modal.Title style={{ fontSize: 18 }}>Add a remote list</Modal.Title></Modal.Header>
                <Modal.Body>
                    <Form.Group style={{ marginBottom: 12 }}>
                        <Form.Label style={{ fontSize: 14 }}>List URL</Form.Label>
                        <Form.Control type="url" placeholder="https://raw.githubusercontent.com/…/warden.ndjson.gz"
                            value={remoteUrl} onChange={e => setRemoteUrl(e.target.value)} />
                        <Form.Text muted>A raw .ndjson or .ndjson.gz file. GitHub raw URLs work great.</Form.Text>
                    </Form.Group>
                    <Form.Group style={{ marginBottom: 12 }}>
                        <Form.Label style={{ fontSize: 14 }}>Name (optional)</Form.Label>
                        <Form.Control value={remoteName} onChange={e => setRemoteName(e.target.value)} placeholder="Shared list" />
                    </Form.Group>
                    <div style={{ display: "flex", gap: 12 }}>
                        <Form.Group style={{ flex: 1 }}>
                            <Form.Label style={{ fontSize: 14 }}>Trust</Form.Label>
                            <Form.Select value={remoteTrust} onChange={e => setRemoteTrust(e.target.value as Trust)}>
                                <option value="corroborate">corroborate (recommended)</option>
                                <option value="full">full</option>
                                <option value="observe">observe</option>
                            </Form.Select>
                            <Form.Text muted>{TRUST_HELP[remoteTrust]}</Form.Text>
                        </Form.Group>
                        <Form.Group style={{ width: 130 }}>
                            <Form.Label style={{ fontSize: 14 }}>Refresh (h)</Form.Label>
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
                <Modal.Header closeButton><Modal.Title style={{ fontSize: 18 }}>Import a warden file</Modal.Title></Modal.Header>
                <Modal.Body>
                    <Form.Check type="radio" name="import-target" id="import-separate" label="Keep as a separate source (recommended)"
                        checked={importTarget === "separate"} onChange={() => setImportTarget("separate")} />
                    <div style={{ fontSize: 13, opacity: 0.6, margin: "2px 0 10px 24px" }}>
                        Stays isolated and reversible — one click to remove later. Best for lists from other people.
                    </div>
                    <Form.Check type="radio" name="import-target" id="import-merge" label="Merge into my list"
                        checked={importTarget === "merge"} onChange={() => setImportTarget("merge")} />
                    <div style={{ fontSize: 13, opacity: 0.6, margin: "2px 0 10px 24px" }}>
                        Folds the fingerprints into your own list. Can’t be un-merged.
                    </div>

                    {importTarget === "separate" &&
                        <div style={{ display: "flex", gap: 12, marginBottom: 12 }}>
                            <Form.Group style={{ flex: 1 }}>
                                <Form.Label style={{ fontSize: 14 }}>Name</Form.Label>
                                <Form.Control value={importName} onChange={e => setImportName(e.target.value)} placeholder="Imported list" />
                            </Form.Group>
                            <Form.Group style={{ width: 160 }}>
                                <Form.Label style={{ fontSize: 14 }}>Trust</Form.Label>
                                <Form.Select value={importTrust} onChange={e => setImportTrust(e.target.value as Trust)}>
                                    <option value="corroborate">corroborate</option>
                                    <option value="full">full</option>
                                    <option value="observe">observe</option>
                                </Form.Select>
                            </Form.Group>
                        </div>}

                    <input ref={fileRef} type="file" accept=".gz,.ndjson,.json,application/gzip,application/json"
                        style={{ display: "none" }} onChange={onFilePicked} />
                    <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                        <Button variant="outline-secondary" size="sm" onClick={() => fileRef.current?.click()}>Choose file…</Button>
                        <span style={{ fontSize: 13, opacity: 0.7 }}>{pendingFile ? pendingFile.name : "No file selected"}</span>
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
                <Modal.Header closeButton><Modal.Title style={{ fontSize: 18 }}>Export</Modal.Title></Modal.Header>
                <Modal.Body>
                    <Form.Check type="radio" name="export-scope" id="export-local" label="My list only"
                        checked={exportScope === "local"} onChange={() => setExportScope("local")} />
                    <div style={{ fontSize: 13, opacity: 0.6, margin: "2px 0 10px 24px" }}>
                        Just your own verdicts — the clean file others can trust. Publish it and share the URL.
                    </div>
                    <Form.Check type="radio" name="export-scope" id="export-merged" label="Merged from selected sources"
                        checked={exportScope === "merged"} onChange={() => setExportScope("merged")} />
                    {exportScope === "merged" &&
                        <div style={{ margin: "8px 0 10px 24px" }}>
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
