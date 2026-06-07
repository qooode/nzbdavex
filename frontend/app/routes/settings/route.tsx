import type { Route } from "./+types/route";
import styles from "./route.module.css"
import { Tabs, Tab, Button, Accordion } from "react-bootstrap"
import { backendClient } from "~/clients/backend-client.server";
import { isUsenetSettingsUpdated, UsenetSettings } from "./usenet/usenet";
import { isSabnzbdSettingsUpdated, isSabnzbdSettingsValid, SabnzbdSettings } from "./sabnzbd/sabnzbd";
import { isWebdavSettingsUpdated, isWebdavSettingsValid, WebdavSettings } from "./webdav/webdav";
import { isArrsSettingsUpdated, isArrsSettingsValid, ArrsSettings } from "./arrs/arrs";
import { isIndexersSettingsUpdated, isIndexersSettingsValid, IndexersSettings } from "./indexers/indexers";
import { isProfilesSettingsUpdated, isProfilesSettingsValid, ProfilesSettings } from "./profiles/profiles";
import { isMaintenanceSettingsUpdated, Maintenance } from "./maintenance/maintenance";
import { isRepairsSettingsUpdated, RepairsSettings } from "./repairs/repairs";
import { isWatchdogSettingsUpdated, WatchdogSettings } from "./watchdog/watchdog";
import { isPreflightSettingsUpdated, PreflightSettings } from "./preflight/preflight";
import { isWatchtowerSettingsUpdated, WatchtowerSettings } from "./watchtower/watchtower";
import { isWardenSettingsUpdated, WardenSettings } from "./warden/warden";
import { isRcloneSettingsUpdated, RcloneSettings } from "./rclone/rclone";
import { useCallback, useState, type ReactNode } from "react";
import { useBlocker } from "react-router";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";

const defaultConfig = {
    "general.base-url": "",
    "api.key": "",
    "api.categories": "",
    "api.manual-category": "uncategorized",
    "api.ensure-importable-video": "true",
    "api.ensure-article-existence-categories": "",
    "api.ignore-history-limit": "true",
    "api.download-file-blocklist": "*.nfo, *.par2, *.sfv, *sample.mkv",
    "api.duplicate-nzb-behavior": "increment",
    "api.import-strategy": "symlinks",
    "api.completed-downloads-dir": "",
    "api.user-agent": "",
    "usenet.providers": "",
    "usenet.max-download-connections": "15",
    "usenet.streaming-priority": "80",
    "usenet.article-buffer-size": "40",
    "usenet.segment-cache.enabled": "false",
    "usenet.segment-cache.path": "/config/segment-cache",
    "usenet.segment-cache.max-gb": "10",
    "webdav.user": "admin",
    "webdav.pass": "",
    "webdav.show-hidden-files": "false",
    "webdav.enforce-readonly": "true",
    "webdav.preview-par2-files": "false",
    "rclone.rc-enabled": "false",
    "rclone.host": "",
    "rclone.user": "",
    "rclone.pass": "",
    "rclone.mount-dir": "",
    "media.library-dir": "",
    "arr.instances": "{\"RadarrInstances\":[],\"SonarrInstances\":[],\"QueueRules\":[]}",
    "indexers.instances": "{\"Indexers\":[]}",
    "profiles.instances": "{\"Profiles\":[]}",
    "play.watchdog-enabled": "true",
    "play.total-budget-seconds": "30",
    "play.hedge-delay-seconds": "3",
    "play.max-candidates": "3",
    "play.max-attempts": "10",
    "play.verify-mode": "none",
    "play.candidate-negative-cache-minutes": "5",
    "search.exclude-patterns": "",
    "variants.mode": "off",
    "variants.tolerance-pct": "25",
    "variants.max-per-group": "3",
    "variants.replay-strategy": "closest-to-click",
    "variants.fallback-on-failure": "true",
    "variants.eviction-strategy": "lru",
    "variants.eviction-active-grace-seconds": "60",
    "preflight.mode": "off",
    "preflight.max-attempts": "20",
    "preflight.ttl-seconds": "120",
    "preflight.indexer-max-wait-seconds": "5",
    "repair.enable": "false",
    "db.is-startup-vacuum-enabled": "false",
    "maintenance.remove-orphaned-schedule-enabled": "false",
    "maintenance.remove-orphaned-schedule-time": "0",
    "api.nzb-backup-enabled": "false",
    "api.nzb-backup-location": "",
    "watchtower.enabled": "false",
    "watchtower.profile-token": "",
    "watchtower.ranking": "watchdog",
    "watchtower.size-floor-bytes": "524288000",
    "watchtower.size-ceiling-bytes": "0",
    "watchtower.shortlist-depth": "2",
    "watchtower.grab-cap-per-resolve": "3",
    "watchtower.active-set-cap": "100",
    "watchtower.daily-resolve-budget": "60",
    "watchtower.sync-interval-seconds": "3600",
    "watchtower.series-scope": "latest-season",
    "watchtower.season-bundles": "true",
    "watchtower.series-max-episodes": "50",
    "watchtower.series-recent-count": "3",
    "watchtower.season-bundle-fallback": "false",
    "watchtower.season-bundle-fallback-scope": "latest-season",
    "watchtower.season-bundle-fallback-recent-count": "2",
    "watchtower.season-bundle-fallback-max-episodes": "50",
    "warden.hide-dead": "true",
    "warden.quorum": "2",
}

export async function loader({ request }: Route.LoaderArgs) {
    // fetch the config items
    var configItems = await backendClient.getConfig(Object.keys(defaultConfig));

    // transform to a map
    const config: Record<string, string> = defaultConfig;
    for (const item of configItems) {
        config[item.configName] = item.configValue;
    }

    return {
        config: config,
        appVersion: process.env.NZBDAV_VERSION ?? "unknown",
    }
}

export default function Settings(props: Route.ComponentProps) {
    return (
        <Body {...props.loaderData} />
    );
}

type BodyProps = {
    config: Record<string, string>,
    appVersion: string,
};

function Body(props: BodyProps) {
    // stateful variables
    const [config, setConfig] = useState(props.config);
    const [newConfig, setNewConfig] = useState(config);
    const [isSaving, setIsSaving] = useState(false);
    const [isSaved, setIsSaved] = useState(false);
    const [activeTab, setActiveTab] = useState('usenet');

    // derived variables
    const iseUsenetUpdated = isUsenetSettingsUpdated(config, newConfig);
    const isSabnzbdUpdated = isSabnzbdSettingsUpdated(config, newConfig);
    const isWebdavUpdated = isWebdavSettingsUpdated(config, newConfig);
    const isArrsUpdated = isArrsSettingsUpdated(config, newConfig);
    const isIndexersUpdated = isIndexersSettingsUpdated(config, newConfig);
    const isProfilesUpdated = isProfilesSettingsUpdated(config, newConfig);
    const isRepairsUpdated = isRepairsSettingsUpdated(config, newConfig);
    const isWatchdogUpdated = isWatchdogSettingsUpdated(config, newConfig);
    const isPreflightUpdated = isPreflightSettingsUpdated(config, newConfig);
    const isRcloneUpdated = isRcloneSettingsUpdated(config, newConfig);
    const isMaintenanceUpdated = isMaintenanceSettingsUpdated(config, newConfig);
    const isWatchtowerUpdated = isWatchtowerSettingsUpdated(config, newConfig);
    const isWardenUpdated = isWardenSettingsUpdated(config, newConfig);
    const isUpdated = iseUsenetUpdated || isSabnzbdUpdated || isWebdavUpdated || isArrsUpdated || isIndexersUpdated || isProfilesUpdated || isRepairsUpdated || isWatchdogUpdated || isPreflightUpdated || isRcloneUpdated || isMaintenanceUpdated || isWatchtowerUpdated || isWardenUpdated;
    const isAdvancedUpdated = isWebdavUpdated || isSabnzbdUpdated || isArrsUpdated || isRepairsUpdated || isRcloneUpdated || isMaintenanceUpdated;
    const navigationBlocker = useNavigationBlocker(isUpdated);

    const usenetTitle = tabTitle("Usenet", iseUsenetUpdated);
    const indexersTitle = tabTitle("Indexers", isIndexersUpdated);
    const profilesTitle = tabTitle("Search Profiles", isProfilesUpdated);
    const watchdogTitle = tabTitle("Watchdog", isWatchdogUpdated);
    const preflightTitle = tabTitle("Preflight", isPreflightUpdated);
    const watchtowerTitle = tabTitle("Watchtower", isWatchtowerUpdated);
    const wardenTitle = tabTitle("Warden", isWardenUpdated);
    const advancedTitle = tabTitle("Advanced", isAdvancedUpdated);

    const saveButtonLabel = isSaving ? "Saving..."
        : !isUpdated && isSaved ? "Saved ✅"
        : !isUpdated && !isSaved ? "There are no changes to save"
        : isSabnzbdUpdated && !isSabnzbdSettingsValid(newConfig) ? "Invalid SABnzbd settings"
        : isWebdavUpdated && !isWebdavSettingsValid(newConfig) ? "Invalid WebDAV settings"
        : isArrsUpdated && !isArrsSettingsValid(newConfig) ? "Invalid Arrs settings"
        : isIndexersUpdated && !isIndexersSettingsValid(newConfig) ? "Invalid Indexers settings"
        : isProfilesUpdated && !isProfilesSettingsValid(newConfig) ? "Invalid Search Profiles settings"
        : "Save";
    const saveButtonVariant = saveButtonLabel === "Save" ? "primary"
        : saveButtonLabel === "Saved ✅" ? "success"
        : "secondary";
    const isSaveButtonDisabled = saveButtonLabel !== "Save";

    // events
    const onClear = useCallback(() => {
        setNewConfig(config);
        setIsSaved(false);
    }, [config, setNewConfig]);

    const onSave = useCallback(async () => {
        setIsSaving(true);
        setIsSaved(false);
        const response = await fetch("/settings/update", {
            method: "POST",
            body: (() => {
                const form = new FormData();
                const changedConfig = getChangedConfig(config, newConfig);
                form.append("config", JSON.stringify(changedConfig));
                return form;
            })()
        });
        if (response.ok) {
            setConfig(newConfig);
        }
        setIsSaving(false);
        setIsSaved(true);
    }, [config, newConfig, setIsSaving, setIsSaved, setConfig]);

    return (
        <div className={styles.container}>
            <Tabs
                activeKey={activeTab}
                onSelect={x => setActiveTab(x!)}
                className={styles.tabs}
            >
                <Tab eventKey="usenet" title={usenetTitle}>
                    <UsenetSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="indexers" title={indexersTitle}>
                    <IndexersSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="profiles" title={profilesTitle}>
                    <ProfilesSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="watchdog" title={watchdogTitle}>
                    <WatchdogSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="preflight" title={preflightTitle}>
                    <PreflightSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="watchtower" title={watchtowerTitle}>
                    <WatchtowerSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="warden" title={wardenTitle}>
                    <WardenSettings config={newConfig} setNewConfig={setNewConfig} />
                </Tab>
                <Tab eventKey="advanced" title={advancedTitle}>
                    <div className={styles.advanced}>
                        <div className={styles.advancedIntro}>
                            <div className={styles.advancedTitle}>Advanced Settings</div>
                            <div className={styles.advancedSubtitle}>
                                Integrations, server access, and maintenance. Expand a section to view its options.
                            </div>
                        </div>
                        <Accordion className={styles.advancedAccordion} alwaysOpen>
                            <AdvancedItem
                                eventKey="webdav"
                                title="WebDAV"
                                description="Remote file access — credentials, hidden files, and read-only mode."
                                isUpdated={isWebdavUpdated}>
                                <WebdavSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="sabnzbd"
                                title="SABnzbd"
                                description="Compatible API for *arr apps — API key, categories, import strategy."
                                isUpdated={isSabnzbdUpdated}>
                                <SabnzbdSettings config={newConfig} setNewConfig={setNewConfig} appVersion={props.appVersion} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="arrs"
                                title="Radarr / Sonarr"
                                description="Connect Radarr and Sonarr instances and configure queue rules."
                                isUpdated={isArrsUpdated}>
                                <ArrsSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="repairs"
                                title="Repairs"
                                description="Automatic repair of failed downloads and broken media library entries."
                                isUpdated={isRepairsUpdated}>
                                <RepairsSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="rclone"
                                title="Rclone Server"
                                description="Remote control protocol settings for mounting nzbdav via rclone."
                                isUpdated={isRcloneUpdated}>
                                <RcloneSettings config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                            <AdvancedItem
                                eventKey="maintenance"
                                title="Maintenance"
                                description="Database vacuum, scheduled cleanup, and one-off migration tasks."
                                isUpdated={isMaintenanceUpdated}>
                                <Maintenance savedConfig={config} config={newConfig} setNewConfig={setNewConfig} />
                            </AdvancedItem>
                        </Accordion>
                    </div>
                </Tab>
            </Tabs>
            <hr />
            {isUpdated && <Button
                className={styles.button}
                variant="secondary"
                disabled={!isUpdated}
                onClick={() => onClear()}>
                Clear
            </Button>}
            <Button
                className={styles.button}
                variant={saveButtonVariant}
                disabled={isSaveButtonDisabled}
                onClick={onSave}>
                {saveButtonLabel}
            </Button>
            <ConfirmModal
                show={navigationBlocker.showConfirmation}
                title="Unsaved Changes"
                message={<>You have unsaved changes.<br/>Are you sure you want to leave this page?</>}
                cancelText="Stay"
                confirmText="Leave"
                onCancel={navigationBlocker.onCancelNavigation}
                onConfirm={navigationBlocker.onConfirmNavigation}
            />
        </div>
    );
}

function tabTitle(label: string, isDirty: boolean): ReactNode {
    return (
        <span className={styles.tabLabel}>
            {isDirty && <PencilIcon />}
            {label}
        </span>
    );
}

function PencilIcon() {
    return (
        <svg
            className={styles.tabIcon}
            width="12"
            height="12"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
        >
            <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
            <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
        </svg>
    );
}

type AdvancedItemProps = {
    eventKey: string,
    title: string,
    description: string,
    isUpdated: boolean,
    children: ReactNode,
};

function AdvancedItem({ eventKey, title, description, isUpdated, children }: AdvancedItemProps) {
    return (
        <Accordion.Item eventKey={eventKey} className={styles.advancedItem}>
            <Accordion.Header className={styles.advancedItemHeader}>
                <div className={styles.advancedItemHeaderInner}>
                    <div className={styles.advancedItemTitleRow}>
                        <span className={styles.advancedItemTitle}>{title}</span>
                        {isUpdated && (
                            <span className={styles.advancedItemBadge}>Unsaved</span>
                        )}
                    </div>
                    <span className={styles.advancedItemDescription}>{description}</span>
                </div>
            </Accordion.Header>
            <Accordion.Body className={styles.advancedItemBody}>
                {children}
            </Accordion.Body>
        </Accordion.Item>
    );
}

function getChangedConfig(
    config: Record<string, string>,
    newConfig: Record<string, string>
): Record<string, string> {
    let changedConfig: Record<string, string> = {};
    let configKeys = Object.keys(defaultConfig);
    for (const configKey of configKeys) {
        if (config[configKey] !== newConfig[configKey]) {
            changedConfig[configKey] = newConfig[configKey];
        }
    }
    return changedConfig;
}

function useNavigationBlocker(isConfigUpdated: boolean) {
    const blocker = useBlocker(isConfigUpdated);

    const onConfirmNavigation = useCallback(() => {
        if (blocker.state === "blocked") {
            blocker.proceed();
        }
    }, [blocker]);

    const onCancelNavigation = useCallback(() => {
        if (blocker.state === "blocked") {
            blocker.reset();
        }
    }, [blocker]);

    return {
        showConfirmation: blocker.state === "blocked",
        onConfirmNavigation,
        onCancelNavigation
    }
}