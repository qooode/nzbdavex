import { Form, InputGroup } from "react-bootstrap";
import styles from "./webdav.module.css"
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Label htmlFor="webdav-user-input">WebDAV User</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidUser(config["webdav.user"]) && styles.error])}
                    type="text"
                    id="webdav-user-input"
                    aria-describedby="webdav-user-help"
                    placeholder="admin"
                    value={config["webdav.user"]}
                    onChange={e => setNewConfig({ ...config, "webdav.user": e.target.value })} />
                <Form.Text id="webdav-user-help" muted>
                    Use this user to connect to the webdav. Only letters, numbers, dashes, and underscores allowed.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="webdav-pass-input">WebDAV Password</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="password"
                    id="webdav-pass-input"
                    aria-describedby="webdav-pass-help"
                    value={config["webdav.pass"]}
                    onChange={e => setNewConfig({ ...config, "webdav.pass": e.target.value })} />
                <Form.Text id="webdav-pass-help" muted>
                    Use this password to connect to the webdav.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="max-download-connections-input">Max Download Connections</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidMaxDownloadConnections(config["usenet.max-download-connections"]) && styles.error])}
                    type="text"
                    id="max-download-connections-input"
                    aria-describedby="max-download-connections-help"
                    placeholder="15"
                    value={config["usenet.max-download-connections"]}
                    onChange={e => setNewConfig({ ...config, "usenet.max-download-connections": e.target.value })} />
                <Form.Text id="max-download-connections-help" muted>
                    The maximum number of connections used for <strong>webdav streaming</strong> (playback).
                    Set this to the minimum number of connections that fully saturates your streaming bandwidth.
                    Queue imports use their own budget — see Queue Download Connections below.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="max-queue-connections-input">Queue Download Connections</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidMaxQueueConnections(config["usenet.max-queue-connections"]) && styles.error])}
                    type="text"
                    id="max-queue-connections-input"
                    aria-describedby="max-queue-connections-help"
                    placeholder="Auto (all connections)"
                    value={config["usenet.max-queue-connections"]}
                    onChange={e => setNewConfig({ ...config, "usenet.max-queue-connections": e.target.value })} />
                <Form.Text id="max-queue-connections-help" muted>
                    Connections the queue may use while importing an nzb (fetching names/sizes, parsing par2/rar).
                    Higher = faster imports. This is independent of streaming, so you can push imports hard without
                    touching the streaming budget. It's always capped at your provider's connection limit, so the
                    queue can never open more connections than your plan allows. Leave blank for Auto (use all your
                    provider connections); lower it if you'd rather keep imports gentle or reserve connections for
                    simultaneous streaming.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="streaming-priority-input">Streaming Priority (vs Queue)</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        className={!isValidStreamingPriority(config["usenet.streaming-priority"]) ? styles.error : undefined}
                        type="text"
                        id="streaming-priority-input"
                        aria-describedby="streaming-priority-help"
                        placeholder="80"
                        value={config["usenet.streaming-priority"]}
                        onChange={e => setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })} />
                    <InputGroup.Text>%</InputGroup.Text>
                </InputGroup>
                <Form.Text id="streaming-priority-help" muted>
                    When streaming from the webdav while the queue is also active, how much bandwidth should be dedicated to streaming?
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="article-buffer-size-input">Article Buffer Size</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidArticleBufferSize(config["usenet.article-buffer-size"]) && styles.error])}
                    type="text"
                    id="article-buffer-size-input"
                    aria-describedby="article-buffer-size-help"
                    placeholder="40"
                    value={config["usenet.article-buffer-size"]}
                    onChange={e => setNewConfig({ ...config, "usenet.article-buffer-size": e.target.value })} />
                <Form.Text id="article-buffer-size-help" muted>
                    The number of articles to buffer ahead, per stream, when reading from the webdav.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="switch"
                    id="segment-cache-enabled-checkbox"
                    aria-describedby="segment-cache-enabled-help"
                    label={`Enable Segment Cache`}
                    checked={config["usenet.segment-cache.enabled"] === "true"}
                    onChange={e => setNewConfig({ ...config, "usenet.segment-cache.enabled": "" + e.target.checked })} />
                <Form.Text id="segment-cache-enabled-help" muted>
                    When enabled, decoded segments are stored on disk so repeated reads (re-watches, seeks, multiple viewers) skip the
                    network and don't count against provider limits. Takes effect after a restart.
                </Form.Text>
            </Form.Group>
            {config["usenet.segment-cache.enabled"] === "true" && (
                <>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="segment-cache-path-input">Segment Cache Path</Form.Label>
                        <Form.Control
                            {...className([styles.input, !isValidSegmentCachePath(config["usenet.segment-cache.path"]) && styles.error])}
                            type="text"
                            id="segment-cache-path-input"
                            aria-describedby="segment-cache-path-help"
                            placeholder="/config/segment-cache"
                            value={config["usenet.segment-cache.path"]}
                            onChange={e => setNewConfig({ ...config, "usenet.segment-cache.path": e.target.value })} />
                        <Form.Text id="segment-cache-path-help" muted>
                            Directory where cached segments are stored. Use a fast local disk (SSD/NVMe) for best results.
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="segment-cache-max-gb-input">Maximum Cache Size</Form.Label>
                        <InputGroup className={styles.input}>
                            <Form.Control
                                className={!isPositiveInteger(config["usenet.segment-cache.max-gb"]) ? styles.error : undefined}
                                type="text"
                                id="segment-cache-max-gb-input"
                                aria-describedby="segment-cache-max-gb-help"
                                placeholder="10"
                                value={config["usenet.segment-cache.max-gb"]}
                                onChange={e => setNewConfig({ ...config, "usenet.segment-cache.max-gb": e.target.value })} />
                            <InputGroup.Text>GB</InputGroup.Text>
                        </InputGroup>
                        <Form.Text id="segment-cache-max-gb-help" muted>
                            Maximum disk space the cache may use before evicting least-recently-used segments. Keep this below your free disk space.
                        </Form.Text>
                    </Form.Group>
                </>
            )}
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="switch"
                    id="readonly-checkbox"
                    aria-describedby="readonly-help"
                    label={`Enforce Read-Only`}
                    checked={config["webdav.enforce-readonly"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.enforce-readonly": "" + e.target.checked })} />
                <Form.Text id="readonly-help" muted>
                    The WebDAV `/content` folder will be readonly when checked. WebDAV clients will not be able to delete files within this directory.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="switch"
                    id="show-hidden-files-checkbox"
                    aria-describedby="show-hidden-files-help"
                    label={`Show hidden files on Dav Explorer`}
                    checked={config["webdav.show-hidden-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.show-hidden-files": "" + e.target.checked })} />
                <Form.Text id="show-hidden-files-help" muted>
                    Hidden files or directories are those whose names are prefixed by a period.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="switch"
                    id="preview-par2-files-checkbox"
                    aria-describedby="preview-par2-files-help"
                    label={`Preview par2 files on Dav Explorer`}
                    checked={config["webdav.preview-par2-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.preview-par2-files": "" + e.target.checked })} />
                <Form.Text id="preview-par2-files-help" muted>
                    When enabled, par2 files will be rendered as text files on the Dav Explorer page, displaying all File-Descriptor entries.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isWebdavSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["webdav.user"] !== newConfig["webdav.user"]
        || config["webdav.pass"] !== newConfig["webdav.pass"]
        || config["usenet.max-download-connections"] !== newConfig["usenet.max-download-connections"]
        || config["usenet.max-queue-connections"] !== newConfig["usenet.max-queue-connections"]
        || config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"]
        || config["usenet.article-buffer-size"] !== newConfig["usenet.article-buffer-size"]
        || config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"]
        || config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"]
        || config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"]
        || config["usenet.segment-cache.enabled"] !== newConfig["usenet.segment-cache.enabled"]
        || config["usenet.segment-cache.path"] !== newConfig["usenet.segment-cache.path"]
        || config["usenet.segment-cache.max-gb"] !== newConfig["usenet.segment-cache.max-gb"]
}

export function isWebdavSettingsValid(newConfig: Record<string, string>) {
    const segmentCacheValid = newConfig["usenet.segment-cache.enabled"] !== "true"
        || (isValidSegmentCachePath(newConfig["usenet.segment-cache.path"])
            && isPositiveInteger(newConfig["usenet.segment-cache.max-gb"]));
    return isValidUser(newConfig["webdav.user"])
        && isValidMaxDownloadConnections(newConfig["usenet.max-download-connections"])
        && isValidMaxQueueConnections(newConfig["usenet.max-queue-connections"])
        && isValidStreamingPriority(newConfig["usenet.streaming-priority"])
        && isValidArticleBufferSize(newConfig["usenet.article-buffer-size"])
        && segmentCacheValid;
}

function isValidSegmentCachePath(value: string): boolean {
    return value.trim().length > 0;
}

function isValidUser(user: string): boolean {
    const regex = /^[A-Za-z0-9_-]+$/;
    return regex.test(user);
}

function isValidMaxDownloadConnections(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidMaxQueueConnections(value: string): boolean {
    return value.trim() === "" || isPositiveInteger(value);
}

function isValidStreamingPriority(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 100;
}

function isValidArticleBufferSize(value: string): boolean {
    return isPositiveInteger(value);
}