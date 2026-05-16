import type { Route } from "./+types/route";
import { Form, useFetcher, useNavigation } from "react-router";
import { Button, Spinner } from "react-bootstrap";
import { backendClient, type SearchIndexersResponse } from "~/clients/backend-client.server";
import { formatFileSize } from "~/utils/file-size";
import styles from "./route.module.css";

export async function loader({ request }: Route.LoaderArgs) {
    const url = new URL(request.url);
    const q = url.searchParams.get("q")?.trim() ?? "";
    if (!q) return { q: "", data: null as SearchIndexersResponse | null };
    const data = await backendClient.searchIndexers(q, 100);
    return { q, data };
}

export async function action({ request }: Route.ActionArgs) {
    const formData = await request.formData();
    const nzbUrl = formData.get("nzbUrl")?.toString() ?? "";
    const nzbName = formData.get("nzbName")?.toString() ?? "";
    if (!nzbUrl || !nzbName) return { ok: false, error: "Missing nzbUrl or nzbName" };
    try {
        const nzoId = await backendClient.addNzbFromUrl(nzbUrl, nzbName);
        return { ok: true, nzoId };
    } catch (e: any) {
        return { ok: false, error: e?.message ?? "Failed to add" };
    }
}

export default function Search({ loaderData }: Route.ComponentProps) {
    const navigation = useNavigation();
    const isSearching = navigation.state === "loading" && navigation.location?.pathname === "/search";
    const { q, data } = loaderData;

    return (
        <div className={styles.container}>
            <Form method="get" className={styles.searchBar}>
                <input
                    name="q"
                    defaultValue={q}
                    placeholder="Search your indexers..."
                    className="form-control"
                    autoFocus
                />
                <Button type="submit" variant="primary" disabled={isSearching}>
                    {isSearching ? <Spinner animation="border" size="sm" /> : "Search"}
                </Button>
            </Form>

            {data && (
                <div className={styles.statusRow}>
                    {data.indexers.map(i => (
                        <span key={i.name} className={i.ok ? styles.statusOk : styles.statusFail}>
                            {i.name}: {i.ok ? `${i.resultCount} results` : "failed"} ({i.elapsedMs}ms)
                            {i.error && <span className={styles.statusError}> — {i.error}</span>}
                        </span>
                    ))}
                </div>
            )}

            {data === null && (
                <p className={styles.empty}>
                    Type a query above to search your configured Newznab indexers.
                    Configure indexers under Settings → Indexers.
                </p>
            )}

            {data && data.results.length === 0 && (
                <p className={styles.empty}>No results for "{q}".</p>
            )}

            {data && data.results.length > 0 && (
                <ul className={styles.results}>
                    {data.results.map((r, idx) => (
                        <ResultRow key={`${r.nzbUrl}-${idx}`} result={r} />
                    ))}
                </ul>
            )}
        </div>
    );
}

function ResultRow({ result }: { result: { indexer: string; title: string; nzbUrl: string; size: number; posted: string | null } }) {
    const fetcher = useFetcher<typeof action>();
    const submitting = fetcher.state !== "idle";
    const done = fetcher.data?.ok === true;
    const failed = fetcher.data && fetcher.data.ok === false;

    return (
        <li className={styles.result}>
            <div className={styles.title}>
                <div>{result.title}</div>
                <div className={styles.meta}>
                    {result.indexer} · {formatFileSize(result.size)}
                    {result.posted && ` · ${new Date(result.posted).toLocaleDateString()}`}
                </div>
            </div>
            <fetcher.Form method="post">
                <input type="hidden" name="nzbUrl" value={result.nzbUrl} />
                <input type="hidden" name="nzbName" value={result.title} />
                <Button
                    type="submit"
                    size="sm"
                    variant={done ? "success" : failed ? "danger" : "primary"}
                    disabled={submitting || done}
                    className={styles.mountBtn}
                    title={failed ? fetcher.data?.error : undefined}
                >
                    {submitting ? <Spinner animation="border" size="sm" />
                        : done ? "Mounted"
                        : failed ? "Failed"
                        : "Mount"}
                </Button>
            </fetcher.Form>
        </li>
    );
}
