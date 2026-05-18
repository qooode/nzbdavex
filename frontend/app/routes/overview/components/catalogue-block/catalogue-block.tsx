import styles from "./catalogue-block.module.css";
import { formatBytes, formatNumber } from "../../utils/format";

export type CatalogueBlockProps = {
    catalogue: {
        fileCount: number,
        totalBytes: number,
        largestFileBytes: number,
        addedLast7Days: number,
    },
}

export function CatalogueBlock({ catalogue }: CatalogueBlockProps) {
    return (
        <div className={styles.container}>
            <div className={styles.header}>
                <h3 className={styles.title}>Catalogue</h3>
                <div className={styles.sub}>Your mounted library</div>
            </div>

            <div className={styles.grid}>
                <Cell label="Files" value={formatNumber(catalogue.fileCount)} />
                <Cell label="Total size" value={formatBytes(catalogue.totalBytes)} />
                <Cell label="Largest file" value={formatBytes(catalogue.largestFileBytes)} />
                <Cell label="Added 7d" value={formatNumber(catalogue.addedLast7Days)} accent={catalogue.addedLast7Days > 0 ? "good" : undefined} />
            </div>
        </div>
    );
}

function Cell({ label, value, accent }: { label: string, value: string, accent?: "good" }) {
    return (
        <div className={`${styles.cell} ${accent === "good" ? styles.good : ""}`}>
            <div className={styles.label}>{label}</div>
            <div className={styles.value}>{value}</div>
        </div>
    );
}
