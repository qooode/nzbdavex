import { Button, Form } from "react-bootstrap";
import { type ChangeEvent, type Dispatch, type SetStateAction, useEffect, useRef, useState } from "react";

type WardenSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

export function WardenSettings({ config, setNewConfig }: WardenSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const hideDead = (config["warden.hide-dead"] ?? "true") === "true";

    const [count, setCount] = useState<number | null>(null);
    const [busy, setBusy] = useState(false);
    const [message, setMessage] = useState<string | null>(null);
    const fileRef = useRef<HTMLInputElement>(null);

    const refreshCount = async () => {
        try {
            const res = await fetch("/api/get-warden");
            if (res.ok) {
                const data = await res.json();
                setCount(data.count ?? 0);
            }
        } catch {
            setCount(null);
        }
    };

    useEffect(() => { refreshCount(); }, []);

    const onImport = async (e: ChangeEvent<HTMLInputElement>) => {
        const file = e.target.files?.[0];
        if (!file) return;
        setBusy(true);
        setMessage(null);
        try {
            const form = new FormData();
            form.append("file", file);
            const res = await fetch("/api/warden-import", { method: "POST", body: form });
            const data = await res.json().catch(() => ({}));
            if (res.ok) {
                setMessage(`Imported ${data.added ?? 0} new (total ${data.total ?? 0}).`);
                await refreshCount();
            } else {
                setMessage(data.error || "Import failed.");
            }
        } catch (err: any) {
            setMessage(err?.message || "Import failed.");
        } finally {
            setBusy(false);
            if (fileRef.current) fileRef.current.value = "";
        }
    };

    const onClear = async () => {
        setBusy(true);
        setMessage(null);
        try {
            const form = new FormData();
            form.append("action", "clear");
            const res = await fetch("/api/warden-import", { method: "POST", body: form });
            const data = await res.json().catch(() => ({}));
            if (res.ok) {
                setMessage(`Cleared ${data.cleared ?? 0}.`);
                await refreshCount();
            } else {
                setMessage(data.error || "Clear failed.");
            }
        } catch (err: any) {
            setMessage(err?.message || "Clear failed.");
        } finally {
            setBusy(false);
        }
    };

    return (
        <div style={{ padding: 16, maxWidth: 720 }}>
            <div style={{ marginBottom: 16 }}>
                <div style={{ fontSize: 18, fontWeight: 600 }}>Warden</div>
                <div style={{ opacity: 0.7, fontSize: 14 }}>
                    A portable filter list. It holds fingerprints and keeps anything matching them out
                    of your search-profile results. The fingerprints are universal: identical on any
                    provider, indexer, or server, and free of credentials. It fills in automatically
                    over time. The exported file drops into any other config or server and just works.
                    Import anyone's, export yours.
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
                    When on, anything whose fingerprint is on the list is removed from what your search
                    profiles return. If everything matches, results are shown anyway as a last resort.
                </Form.Text>
            </Form.Group>

            <hr />

            <div style={{ marginBottom: 8, fontWeight: 600 }}>
                Fingerprints on the list: {count === null ? "…" : count}
            </div>
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                <Button href="/api/warden-export" variant="secondary" size="sm">Export</Button>
                <Button variant="secondary" size="sm" disabled={busy} onClick={() => fileRef.current?.click()}>
                    Import
                </Button>
                <input
                    ref={fileRef}
                    type="file"
                    accept="application/json,.json"
                    style={{ display: "none" }}
                    onChange={onImport} />
                <Button variant="outline-danger" size="sm" disabled={busy} onClick={onClear}>Clear</Button>
            </div>
            {message && <div style={{ marginTop: 8, fontSize: 14, opacity: 0.85 }}>{message}</div>}
        </div>
    );
}

export function isWardenSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["warden.hide-dead"] !== newConfig["warden.hide-dead"];
}
