import { Form } from "react-bootstrap";
import { type Dispatch, type SetStateAction } from "react";
import styles from "./watchdog.module.css";

type WatchdogSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WatchdogSettings({ config, setNewConfig }: WatchdogSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const verifyMode = config["play.verify-mode"] ?? "stat";

    return (
        <div className={styles.container}>
            <div className={styles.section}>
                <div className={styles.sectionTitle}>Playback fast-fail</div>
                <div className={styles.sectionDescription}>
                    When a user clicks Play, nzbdav tries the top-ranked release first; if it can't deliver
                    fast enough, alternatives are tried automatically. These knobs control how aggressive
                    that fallback is, so the player never hangs on a dead release.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Label>Total budget (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={3}
                    max={60}
                    value={config["play.total-budget-seconds"] ?? "10"}
                    onChange={e => set("play.total-budget-seconds", e.target.value)} />
                <p className={styles.hint}>
                    Hard ceiling for a Play click. If nothing works in this many seconds, the player gets
                    an error and moves on. Default 10.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Hedge delay (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={30}
                    value={config["play.hedge-delay-seconds"] ?? "3"}
                    onChange={e => set("play.hedge-delay-seconds", e.target.value)} />
                <p className={styles.hint}>
                    If the primary candidate hasn't passed verification by this many seconds, backup
                    candidates start in parallel. Lower = more eager fallback, slightly higher provider load. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Max candidates per click</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={10}
                    value={config["play.max-candidates"] ?? "3"}
                    onChange={e => set("play.max-candidates", e.target.value)} />
                <p className={styles.hint}>
                    How many alternative releases to try per click before giving up. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Verify mode</Form.Label>
                <Form.Select
                    className={styles.input}
                    value={verifyMode}
                    onChange={e => set("play.verify-mode", e.target.value)}>
                    <option value="stat">stat — fast (~0.2s per candidate)</option>
                    <option value="body">body — strict, downloads first article (~1-2s)</option>
                    <option value="none">none — skip verify, fastest but no pre-filtering</option>
                </Form.Select>
                <p className={styles.hint}>
                    How candidates are pre-checked before committing. `stat` is recommended; `body` is stricter at the cost of ~1s per candidate.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Negative-cache TTL (minutes)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={1440}
                    value={config["play.candidate-negative-cache-minutes"] ?? "5"}
                    onChange={e => set("play.candidate-negative-cache-minutes", e.target.value)} />
                <p className={styles.hint}>
                    How long a recently-failed release is skipped on subsequent clicks, so we don't hammer
                    the same dead release. Default 5.
                </p>
            </Form.Group>
        </div>
    );
}

export function isWatchdogSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["play.total-budget-seconds"] !== newConfig["play.total-budget-seconds"]
        || config["play.hedge-delay-seconds"] !== newConfig["play.hedge-delay-seconds"]
        || config["play.max-candidates"] !== newConfig["play.max-candidates"]
        || config["play.verify-mode"] !== newConfig["play.verify-mode"]
        || config["play.candidate-negative-cache-minutes"] !== newConfig["play.candidate-negative-cache-minutes"];
}
