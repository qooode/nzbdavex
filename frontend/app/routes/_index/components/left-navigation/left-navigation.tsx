import { Form, Link, useLocation, useNavigation } from "react-router";
import styles from "./left-navigation.module.css";
import { className } from "~/utils/styling";
import type React from "react";
import { LiveUsenetConnections } from "../live-usenet-connections/live-usenet-connections";
import { LiveReads } from "../live-reads/live-reads";

export type LeftNavigationProps = {
    version?: string,
    isFrontendAuthDisabled?: boolean,
    isWatchdogEnabled?: boolean,
}

export function LeftNavigation({ version, isFrontendAuthDisabled, isWatchdogEnabled }: LeftNavigationProps) {
    return (
        <div className={styles.container}>
            <Item target="/overview">
                <div className={styles["overview-icon"]} />
                <div className={styles.title}>Overview</div>
            </Item>
            <Item target="/queue">
                <div className={styles["queue-icon"]} />
                <div className={styles.title}>Queue</div>
            </Item>
            {isWatchdogEnabled && (
                <Item target="/watchdog">
                    <div className={styles["watchdog-icon"]} />
                    <div className={styles.title}>Watchdog</div>
                </Item>
            )}
            <Item target="/explore">
                <div className={styles["explore-icon"]} />
                <div className={styles.title}>Files</div>
            </Item>
            <Item target="/health">
                <div className={styles["health-icon"]} />
                <div className={styles.title}>Health</div>
            </Item>
            <Item target="/search">
                <div className={styles["search-icon"]} />
                <div className={styles.title}>Search</div>
            </Item>
            <Item target="/settings">
                <div className={styles["settings-icon"]} />
                <div className={styles.title}>Settings</div>
            </Item>
            <LiveUsenetConnections />
            <LiveReads />

            <div className={styles.footer}>
                <div className={styles["footer-item"]}>
                    <Link to="https://github.com/qooode/nzbdavex" className={styles["github-link"]}>
                        github
                    </Link>
                    <div className={styles["github-icon"]} />
                </div>
                <div className={styles["footer-item"]}>
                    <Link to="https://github.com/qooode/nzbdavex/blob/main/CHANGELOG.md" className={styles["github-link"]}>
                        changelog
                    </Link>
                </div>
                <div className={styles["footer-item"]}>
                    version: {version || 'unknown'}
                </div>
                {!isFrontendAuthDisabled && <>
                    <hr />
                    <Form method="post" action="/logout">
                        <input name="confirm" value="true" type="hidden" />
                        <button className={styles.unstyled + ' ' + styles.item} type="submit">
                            <div className={styles["logout-icon"]} />
                            <div className={styles.title}>Logout</div>
                        </button>
                    </Form>
                </>}
            </div>
        </div>
    );
}

function Item({ target, children }: { target: string, children: React.ReactNode }) {
    const location = useLocation();
    const navigation = useNavigation();
    const pathname = navigation.location?.pathname ?? location.pathname;
    const isSelected = pathname.startsWith(target);
    const classes = [styles.item, isSelected ? styles.selected : null];
    return <>
        <Link {...className(classes)} to={target}>
            {children}
        </Link>
    </>;
}