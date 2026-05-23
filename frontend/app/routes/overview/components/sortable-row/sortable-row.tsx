import type { ReactNode } from "react";
import { useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";
import styles from "./sortable-row.module.css";

type Props = {
    id: string;
    editMode: boolean;
    children: ReactNode;
};

export function SortableRow({ id, editMode, children }: Props) {
    const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id, disabled: !editMode });

    const style = {
        transform: CSS.Transform.toString(transform),
        transition,
    };

    const rowClass = [
        styles.row,
        editMode ? styles.rowEditing : "",
        isDragging ? styles.rowDragging : "",
    ].filter(Boolean).join(" ");

    return (
        <div ref={setNodeRef} style={style} className={rowClass}>
            {editMode && (
                <button
                    type="button"
                    className={styles.handle}
                    aria-label="Drag to reorder"
                    {...attributes}
                    {...listeners}>
                    <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
                        <circle cx="5" cy="3" r="1.4" />
                        <circle cx="11" cy="3" r="1.4" />
                        <circle cx="5" cy="8" r="1.4" />
                        <circle cx="11" cy="8" r="1.4" />
                        <circle cx="5" cy="13" r="1.4" />
                        <circle cx="11" cy="13" r="1.4" />
                    </svg>
                </button>
            )}
            <div className={styles.rowContent}>
                {children}
            </div>
        </div>
    );
}
