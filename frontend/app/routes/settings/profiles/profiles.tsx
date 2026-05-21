import { Button, Form, Card } from "react-bootstrap";
import styles from "./profiles.module.css";
import { type Dispatch, type SetStateAction, useCallback, useMemo, useState } from "react";
import { MultiCheckboxInput } from "~/components/multi-checkbox-input/multi-checkbox-input";

type ProfilesSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

interface Profile {
    Token: string;
    Name: string;
    IndexerNames: string[];
}

interface ProfileConfig {
    Profiles: Profile[];
}

interface IndexerSummary {
    Name: string;
    Enabled: boolean;
}

function parseProfileConfig(raw: string): ProfileConfig {
    try {
        const parsed = JSON.parse(raw || "{}");
        return { Profiles: parsed.Profiles ?? [] };
    } catch {
        return { Profiles: [] };
    }
}

function parseIndexerNames(raw: string): string[] {
    try {
        const parsed = JSON.parse(raw || "{}");
        const list: IndexerSummary[] = parsed.Indexers ?? [];
        return list.filter(i => i.Enabled && i.Name?.trim()).map(i => i.Name);
    } catch {
        return [];
    }
}

function makeToken(): string {
    const bytes = new Uint8Array(12);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, b => b.toString(16).padStart(2, "0")).join("");
}

export function ProfilesSettings({ config, setNewConfig }: ProfilesSettingsProps) {
    const profileConfig = useMemo(() => parseProfileConfig(config["profiles.instances"]), [config]);
    const availableIndexers = useMemo(() => parseIndexerNames(config["indexers.instances"]), [config]);

    const update = useCallback((next: ProfileConfig) => {
        setNewConfig({ ...config, "profiles.instances": JSON.stringify(next) });
    }, [config, setNewConfig]);

    const add = useCallback(() => {
        update({
            Profiles: [
                ...profileConfig.Profiles,
                { Token: makeToken(), Name: "", IndexerNames: [] }
            ]
        });
    }, [profileConfig, update]);

    const remove = useCallback((index: number) => {
        update({ Profiles: profileConfig.Profiles.filter((_, i) => i !== index) });
    }, [profileConfig, update]);

    const change = useCallback((index: number, patch: Partial<Profile>) => {
        update({
            Profiles: profileConfig.Profiles.map((x, i) =>
                i === index ? { ...x, ...patch } : x
            )
        });
    }, [profileConfig, update]);

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Search Profiles</div>
                    <Button variant="primary" size="sm" onClick={add}>Add</Button>
                </div>
                {profileConfig.Profiles.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No search profiles configured. Each profile gets its own search-API URL with a custom indexer selection, usable by any compatible client.
                    </p>
                ) : (
                    profileConfig.Profiles.map((profile, index) => (
                        <ProfileForm
                            key={profile.Token}
                            profile={profile}
                            index={index}
                            availableIndexers={availableIndexers}
                            onChange={change}
                            onRemove={remove}
                        />
                    ))
                )}
            </div>
        </div>
    );
}

interface ProfileFormProps {
    profile: Profile;
    index: number;
    availableIndexers: string[];
    onChange: (index: number, patch: Partial<Profile>) => void;
    onRemove: (index: number) => void;
}

function ProfileForm({ profile, index, availableIndexers, onChange, onRemove }: ProfileFormProps) {
    const [copied, setCopied] = useState(false);

    const installUrl = useMemo(() => {
        if (typeof window === "undefined") return "";
        return `${window.location.origin}/p/${profile.Token}/manifest.json`;
    }, [profile.Token]);

    const onCopy = useCallback(async () => {
        try {
            await navigator.clipboard.writeText(installUrl);
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
        } catch {}
    }, [installUrl]);

    const indexersCsv = profile.IndexerNames.join(", ");

    return (
        <Card className={styles.instanceCard}>
            <button className={styles.closeButton} onClick={() => onRemove(index)} aria-label="Remove">×</button>
            <Card.Body>
                <Form.Group>
                    <Form.Label>Name</Form.Label>
                    <Form.Control
                        type="text"
                        className={styles.input}
                        placeholder="e.g. Movies"
                        value={profile.Name}
                        onChange={e => onChange(index, { Name: e.target.value })} />
                </Form.Group>
                <Form.Group>
                    <Form.Label>Indexers <span style={{ opacity: 0.6, fontWeight: 'normal' }}>(leave all unchecked to use every enabled indexer)</span></Form.Label>
                    {availableIndexers.length === 0 ? (
                        <p className={styles.hint}>No indexers configured yet. Add some under the Indexers tab.</p>
                    ) : (
                        <MultiCheckboxInput
                            options={availableIndexers}
                            value={indexersCsv}
                            onChange={v => onChange(index, {
                                IndexerNames: v.split(",").map(s => s.trim()).filter(Boolean)
                            })}
                        />
                    )}
                </Form.Group>
                <Form.Group>
                    <Form.Label>Install URL</Form.Label>
                    <div className={styles.urlBox}>
                        <Form.Control
                            className={styles.urlInput}
                            type="text"
                            readOnly
                            value={installUrl}
                            onFocus={e => e.currentTarget.select()}
                        />
                        <Button variant={copied ? "success" : "secondary"} size="sm" onClick={onCopy}>
                            {copied ? "Copied" : "Copy"}
                        </Button>
                    </div>
                    <p className={styles.hint}>Search-API endpoint URL. Use it from any compatible external client to query your configured indexers under this profile.</p>
                </Form.Group>
            </Card.Body>
        </Card>
    );
}

export function isProfilesSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["profiles.instances"] !== newConfig["profiles.instances"];
}

export function isProfilesSettingsValid(newConfig: Record<string, string>) {
    try {
        const c = parseProfileConfig(newConfig["profiles.instances"]);
        const tokens = new Set<string>();
        for (const p of c.Profiles) {
            if (!p.Token?.trim()) return false;
            if (tokens.has(p.Token)) return false;
            tokens.add(p.Token);
            if (!p.Name?.trim()) return false;
        }
        return true;
    } catch {
        return false;
    }
}
