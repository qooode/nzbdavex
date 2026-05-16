import styles from "./hamburger-menu.module.css"

export type HamburgerMenuProps = {
    isOpen: boolean
    onClick: () => void,
}

export function HamburgerMenu(props: HamburgerMenuProps) {
    const styleName = props.isOpen ? "close-icon" : "hamburger-icon";
    const icon = <div className={styles[styleName]} onClick={props.onClick} />
    return <div className={styles.container}>{icon}</div>
}