
const STYLE_ID = 'themed-select-menu-style';
const MENU_CSS = `
.themed-select-menu {
    position: fixed;
    z-index: 100000;
    box-sizing: border-box;
    margin: 0;
    padding: 0;
    overflow-y: auto;
    background: var(--bg-surface-light);
    color: var(--text-main);
    border: 1px solid var(--border-color);
    border-radius: 4px;
    box-shadow: 0 6px 16px rgba(0, 0, 0, 0.4);
    font-family: inherit;
    user-select: none;
}
.themed-select-menu-item {
    padding: 3px 10px;
    white-space: nowrap;
    cursor: default;
}
.themed-select-menu-item.active {
    background: var(--primary-green);
    color: var(--on-primary);
}
.themed-select-menu-item.disabled {
    color: var(--text-muted);
}
`;

let menu = null;
let owner = null;
let activeIndex = -1;
let installed = false;

function isThemable(select) {
    return select instanceof HTMLSelectElement
        && !select.disabled
        && !select.multiple
        && select.size <= 1
        && select.options.length > 0;
}

function items() {
    return menu ? [...menu.querySelectorAll('.themed-select-menu-item')] : [];
}

function setActive(index, scroll = false) {
    const rows = items();
    if (!rows.length) return;
    activeIndex = Math.max(0, Math.min(index, rows.length - 1));
    rows.forEach((row, i) => row.classList.toggle('active', i === activeIndex));
    if (scroll) rows[activeIndex].scrollIntoView({ block: 'nearest' });
}

function moveActive(step) {
    const rows = items();
    let i = activeIndex;
    for (let n = 0; n < rows.length; n++) {
        i += step;
        if (i < 0 || i >= rows.length) return;
        if (!rows[i].classList.contains('disabled')) {
            setActive(i, true);
            return;
        }
    }
}

function closeMenu({ refocus = false } = {}) {
    if (!menu) return;
    menu.remove();
    menu = null;
    const select = owner;
    owner = null;
    activeIndex = -1;
    if (refocus && select?.isConnected) select.focus({ preventScroll: true });
}

function choose(index) {
    const select = owner;
    const row = items()[index];
    if (!select || !row || row.classList.contains('disabled')) return;
    const optionIndex = Number(row.dataset.optionIndex);
    closeMenu({ refocus: true });
    if (select.selectedIndex === optionIndex) return;
    select.selectedIndex = optionIndex;
    select.dispatchEvent(new Event('input', { bubbles: true }));
    select.dispatchEvent(new Event('change', { bubbles: true }));
}

function positionMenu(select) {
    const rect = select.getBoundingClientRect();
    const margin = 4;
    menu.style.minWidth = `${Math.round(rect.width)}px`;
    menu.style.left = `${Math.round(rect.left)}px`;

    const below = window.innerHeight - rect.bottom - margin;
    const above = rect.top - margin;
    const natural = menu.scrollHeight;
    const openUp = natural > below && above > below;
    const maxHeight = Math.max(80, openUp ? above : below);
    menu.style.maxHeight = `${Math.floor(maxHeight)}px`;
    menu.style.top = openUp
        ? `${Math.round(rect.top - Math.min(natural, maxHeight))}px`
        : `${Math.round(rect.bottom)}px`;

    const overflowX = menu.getBoundingClientRect().right - (window.innerWidth - margin);
    if (overflowX > 0) menu.style.left = `${Math.max(margin, Math.round(rect.left - overflowX))}px`;
}

function openMenu(select) {
    closeMenu();
    owner = select;
    menu = document.createElement('div');
    menu.className = 'themed-select-menu';
    menu.setAttribute('role', 'listbox');
    const cs = getComputedStyle(select);
    menu.style.fontSize = cs.fontSize;
    menu.style.fontWeight = cs.fontWeight;

    [...select.options].forEach((opt, i) => {
        if (opt.hidden) return;
        const row = document.createElement('div');
        row.className = 'themed-select-menu-item';
        if (opt.disabled) row.classList.add('disabled');
        row.setAttribute('role', 'option');
        row.dataset.optionIndex = String(i);
        row.textContent = opt.textContent;
        menu.appendChild(row);
    });

    menu.addEventListener('mousemove', (e) => {
        const row = e.target.closest('.themed-select-menu-item');
        if (!row || row.classList.contains('disabled')) return;
        const index = items().indexOf(row);
        if (index !== activeIndex) setActive(index);
    });
    menu.addEventListener('mousedown', (e) => e.preventDefault());
    menu.addEventListener('click', (e) => {
        const row = e.target.closest('.themed-select-menu-item');
        if (row) choose(items().indexOf(row));
    });

    document.body.appendChild(menu);
    positionMenu(select);
    const current = items().findIndex((row) => Number(row.dataset.optionIndex) === select.selectedIndex);
    setActive(current >= 0 ? current : 0, true);
}

function onMouseDown(e) {
    if (menu && menu.contains(e.target)) return;
    const select = e.target instanceof Element ? e.target.closest('select') : null;
    if (e.button !== 0 || !isThemable(select)) {
        if (menu) closeMenu();
        return;
    }
    e.preventDefault();
    if (owner === select) {
        closeMenu({ refocus: true });
        return;
    }
    select.focus({ preventScroll: true });
    openMenu(select);
}

function onKeyDown(e) {
    if (menu) {
        switch (e.key) {
            case 'ArrowDown': moveActive(1); break;
            case 'ArrowUp': moveActive(-1); break;
            case 'Home': setActive(0, true); break;
            case 'End': setActive(items().length - 1, true); break;
            case 'Enter':
            case ' ':
                choose(activeIndex); break;
            case 'Escape':
            case 'Tab':
                closeMenu({ refocus: e.key === 'Escape' });
                if (e.key === 'Tab') return;
                break;
            default:
                return;
        }
        e.preventDefault();
        e.stopPropagation();
        return;
    }

    const select = e.target;
    if (!isThemable(select)) return;
    const opens = e.key === 'F4'
        || (e.altKey && (e.key === 'ArrowDown' || e.key === 'ArrowUp'))
        || e.key === ' '
        || e.key === 'Enter';
    if (!opens) return;
    e.preventDefault();
    e.stopPropagation();
    openMenu(select);
}

function onScroll(e) {
    if (menu && !menu.contains(e.target)) closeMenu();
}

export function installThemedSelectMenus() {
    if (installed) return;
    installed = true;
    if (!document.getElementById(STYLE_ID)) {
        const style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = MENU_CSS;
        document.head.appendChild(style);
    }
    document.addEventListener('mousedown', onMouseDown, true);
    document.addEventListener('keydown', onKeyDown, true);
    document.addEventListener('scroll', onScroll, true);
    window.addEventListener('resize', () => closeMenu());
    window.addEventListener('blur', () => closeMenu());
}
