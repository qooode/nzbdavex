import { Form } from "react-bootstrap";
import { Link } from "react-router";
import { type Dispatch, type SetStateAction, useMemo } from "react";
import styles from "./watchdog.module.css";

type WatchdogSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

type PatternIssue = { line: number, pattern: string, error: string };

function validateExcludePatterns(raw: string): PatternIssue[] {
    const issues: PatternIssue[] = [];
    const lines = raw.split("\n");
    for (let i = 0; i < lines.length; i++) {
        const trimmed = lines[i].trim();
        if (trimmed.length === 0 || trimmed.startsWith("#")) continue;
        try {
            new RegExp(trimmed, "i");
        } catch (e: any) {
            issues.push({ line: i + 1, pattern: trimmed, error: e?.message ?? "invalid regex" });
        }
    }
    return issues;
}

export function WatchdogSettings({ config, setNewConfig }: WatchdogSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const verifyMode = config["play.verify-mode"] ?? "stat";
    const enabled = (config["play.watchdog-enabled"] ?? "true") === "true";
    const excludePatterns = config["play.exclude-patterns"] ?? "";
    const patternIssues = useMemo(() => validateExcludePatterns(excludePatterns), [excludePatterns]);

    const variantsMode = config["variants.mode"] ?? "off";
    const variantsEnabled = variantsMode !== "off";
    const variantsFallback = (config["variants.fallback-on-failure"] ?? "true") === "true";

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
                <Form.Check
                    type="switch"
                    id="play-watchdog-enabled"
                    label="Enable playback watchdog"
                    checked={enabled}
                    onChange={e => set("play.watchdog-enabled", String(e.target.checked))} />
                <p className={styles.hint}>
                    When off, a Play click just processes the single chosen release (legacy behavior).
                    When on, the watchdog tries alternative releases on failure and dedupes in-flight queue items.
                    {enabled && <> Live reports appear in the <Link to="/watchdog">Watchdog</Link> tab in the sidebar.</>}
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Total budget (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={3}
                    max={180}
                    value={config["play.total-budget-seconds"] ?? "30"}
                    onChange={e => set("play.total-budget-seconds", e.target.value)} />
                <p className={styles.hint}>
                    Hard ceiling for a Play click. Big UHD releases need ~15–30s for the queue to extract
                    file metadata. If exceeded, the player gets a retry-able error; the queue item keeps
                    processing in the background and a re-click resolves it. Default 30.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Hedge delay (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={30}
                    disabled={!enabled}
                    value={config["play.hedge-delay-seconds"] ?? "2"}
                    onChange={e => set("play.hedge-delay-seconds", e.target.value)} />
                <p className={styles.hint}>
                    If the primary candidate hasn't passed verification by this many seconds, backup
                    candidates start in parallel. Lower = more eager fallback, slightly higher provider load. Default 2.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Parallel candidates per batch</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={10}
                    disabled={!enabled}
                    value={config["play.max-candidates"] ?? "1"}
                    onChange={e => set("play.max-candidates", e.target.value)} />
                <p className={styles.hint}>
                    How many candidates run at the same time in one round. Higher means faster
                    failover when a candidate fails, but more simultaneous indexer requests — too
                    many in parallel can look like spamming and risk a ban. Default 1.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Total candidates per click</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={200}
                    disabled={!enabled}
                    value={config["play.max-attempts"] ?? "15"}
                    onChange={e => set("play.max-attempts", e.target.value)} />
                <p className={styles.hint}>
                    The most candidates one attempt will try in total before giving up. With the
                    defaults (1 per batch, 15 total) it tries up to 15 candidates one at a time,
                    then stops. Also stops sooner if the total budget runs out. Default 15.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Verify mode</Form.Label>
                <Form.Select
                    className={styles.input}
                    disabled={!enabled}
                    value={verifyMode}
                    onChange={e => set("play.verify-mode", e.target.value)}>
                    <option value="stat">stat — STAT first segment (~0.2s; skips candidates flagged dead, recommended)</option>
                    <option value="body">body — strict, downloads first article (~1–2s)</option>
                    <option value="none">none — no pre-check, enqueue right away</option>
                </Form.Select>
                <p className={styles.hint}>
                    `stat` is the default: a cheap NNTP check against your provider weeds out dead releases
                    before the queue commits, which avoids re-fetching their NZB from the indexer on every
                    click. `none` skips the check (faster, but every candidate gets enqueued).
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Negative-cache TTL (minutes)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={1}
                    max={1440}
                    disabled={!enabled}
                    value={config["play.candidate-negative-cache-minutes"] ?? "30"}
                    onChange={e => set("play.candidate-negative-cache-minutes", e.target.value)} />
                <p className={styles.hint}>
                    How long a recently-failed release is skipped on subsequent clicks, so we don't hammer
                    the same dead release (and its indexer) over and over. Default 30.
                </p>
            </Form.Group>

            <div className={styles.section}>
                <div className={styles.sectionTitle}>Variants</div>
                <div className={styles.sectionDescription}>
                    Keep multiple size copies of the same item. When you pick a different
                    size for something nzbdav already has, it can fetch that size too, then
                    on future picks serve the copy closest to whatever size you just selected.
                    Off by default.
                </div>
            </div>

            <Form.Group className={styles.section}>
                <Form.Label>Mode</Form.Label>
                <Form.Select
                    className={styles.input}
                    value={variantsMode}
                    onChange={e => set("variants.mode", e.target.value)}>
                    <option value="off">off — always reuse existing, biggest copy (today's behavior)</option>
                    <option value="smart">smart — reuse if size is close enough, else fetch the new variant</option>
                    <option value="collect-all">collect-all — every meaningfully different size adds a new copy</option>
                </Form.Select>
                <p className={styles.hint}>
                    `smart` is the recommended default once enabled. `collect-all` adds a new
                    copy for every distinct size you pick (no near-exact match) — usually fine
                    since files are mounted, not stored locally; only the metadata grows.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Size tolerance (%)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={0}
                    max={100}
                    disabled={variantsMode !== "smart"}
                    value={config["variants.tolerance-pct"] ?? "25"}
                    onChange={e => set("variants.tolerance-pct", e.target.value)} />
                <p className={styles.hint}>
                    `smart` mode only. Existing copy is reused if its size is within ±N% of
                    what you selected. Outside that → fetch the new variant and keep both.
                    Default 25 (generous to absorb indexer-vs-actual size drift).
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Max copies per group</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={0}
                    max={50}
                    disabled={!variantsEnabled}
                    value={config["variants.max-per-group"] ?? "3"}
                    onChange={e => set("variants.max-per-group", e.target.value)} />
                <p className={styles.hint}>
                    Cap on how many size copies of the same item to keep at once. When the
                    cap is hit, the eviction strategy below decides which to drop. Set to 0
                    for unlimited. Default 3.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Selection strategy</Form.Label>
                <Form.Select
                    className={styles.input}
                    disabled={!variantsEnabled}
                    value={config["variants.replay-strategy"] ?? "closest-to-click"}
                    onChange={e => set("variants.replay-strategy", e.target.value)}>
                    <option value="closest-to-click">closest-to-selection — match the size I picked (recommended)</option>
                    <option value="largest">largest — always pick the biggest copy, ignore my selection</option>
                    <option value="smallest">smallest — always pick the smallest copy, ignore my selection</option>
                </Form.Select>
                <p className={styles.hint}>
                    When multiple copies exist for the same group, which one to serve.
                    `closest-to-selection` uses what you just picked as the intent signal.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Check
                    type="switch"
                    id="variants-fallback-on-failure"
                    label="Fallback to closest existing on fetch failure"
                    disabled={!variantsEnabled}
                    checked={variantsFallback}
                    onChange={e => set("variants.fallback-on-failure", String(e.target.checked))} />
                <p className={styles.hint}>
                    When you pick a size we don't have AND no working source can be fetched,
                    serve the closest existing copy instead of returning an error. Strictly
                    safer than today's behavior. On by default.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Eviction strategy</Form.Label>
                <Form.Select
                    className={styles.input}
                    disabled={!variantsEnabled}
                    value={config["variants.eviction-strategy"] ?? "lru"}
                    onChange={e => set("variants.eviction-strategy", e.target.value)}>
                    <option value="lru">lru — least recently used first (recommended)</option>
                    <option value="largest-first">largest-first — drop biggest first, keep small copies</option>
                    <option value="smallest-first">smallest-first — drop smallest first, keep big copies</option>
                    <option value="never">never — never auto-remove; new copies exceed cap and stay</option>
                </Form.Select>
                <p className={styles.hint}>
                    Decides which copy is removed when `max copies per group` is hit. LRU is the
                    safe default. `never` means you remove copies manually from the History view.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Active-use grace (seconds)</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="number"
                    min={0}
                    max={300}
                    disabled={!variantsEnabled}
                    value={config["variants.eviction-active-grace-seconds"] ?? "60"}
                    onChange={e => set("variants.eviction-active-grace-seconds", e.target.value)} />
                <p className={styles.hint}>
                    Eviction skips any copy used within the last N seconds. Safety net so we
                    never remove an item that's still being accessed. Default 60.
                </p>
            </Form.Group>

            <Form.Group className={styles.section}>
                <Form.Label>Exclude result patterns</Form.Label>
                <Form.Control
                    as="textarea"
                    rows={6}
                    spellCheck={false}
                    className={`${styles.input} ${styles.patternInput} ${patternIssues.length > 0 ? styles.patternInputInvalid : ""}`}
                    placeholder={"# one regex per line\n# lines starting with # are comments"}
                    value={excludePatterns}
                    onChange={e => set("play.exclude-patterns", e.target.value)} />
                {patternIssues.length > 0 && (
                    <div className={styles.patternErrors}>
                        {patternIssues.map((iss, i) => (
                            <div key={i} className={styles.patternError}>
                                <span className={styles.patternErrorLine}>Line {iss.line}</span>
                                <code className={styles.patternErrorPattern}>{iss.pattern}</code>
                                <span className={styles.patternErrorMessage}>— {iss.error}</span>
                            </div>
                        ))}
                    </div>
                )}
                <p className={styles.hint}>
                    One JavaScript-style regex per line. Candidates whose title matches any pattern are
                    skipped before NZB fetch and appear in the <Link to="/watchdog">Watchdog</Link> log as
                    "excluded". Case-insensitive by default — use <code>(?-i:Foo)</code> for case-sensitive.
                    Lines starting with <code>#</code> are comments. Use this to skip releases your setup
                    can't handle, whatever the reason.
                </p>
            </Form.Group>
        </div>
    );
}

export function isWatchdogSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["play.watchdog-enabled"] !== newConfig["play.watchdog-enabled"]
        || config["play.total-budget-seconds"] !== newConfig["play.total-budget-seconds"]
        || config["play.hedge-delay-seconds"] !== newConfig["play.hedge-delay-seconds"]
        || config["play.max-candidates"] !== newConfig["play.max-candidates"]
        || config["play.max-attempts"] !== newConfig["play.max-attempts"]
        || config["play.verify-mode"] !== newConfig["play.verify-mode"]
        || config["play.candidate-negative-cache-minutes"] !== newConfig["play.candidate-negative-cache-minutes"]
        || (config["play.exclude-patterns"] ?? "") !== (newConfig["play.exclude-patterns"] ?? "")
        || config["variants.mode"] !== newConfig["variants.mode"]
        || config["variants.tolerance-pct"] !== newConfig["variants.tolerance-pct"]
        || config["variants.max-per-group"] !== newConfig["variants.max-per-group"]
        || config["variants.replay-strategy"] !== newConfig["variants.replay-strategy"]
        || config["variants.fallback-on-failure"] !== newConfig["variants.fallback-on-failure"]
        || config["variants.eviction-strategy"] !== newConfig["variants.eviction-strategy"]
        || config["variants.eviction-active-grace-seconds"] !== newConfig["variants.eviction-active-grace-seconds"];
}

export function isWatchdogSettingsValid(config: Record<string, string>) {
    return validateExcludePatterns(config["play.exclude-patterns"] ?? "").length === 0;
}
