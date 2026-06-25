const STORAGE_KEY = 'mods_type_badge_theme';
const STYLE_ID = 'mods-type-badge-theme';

const DEFAULTS_BY_EXT = {
    ba2: { bg: '#1a2832', fg: '#90caf9', glow: false },
    esp: { bg: '#1c281e', fg: '#a5d6a7', glow: false },
    esm: { bg: '#2a2318', fg: '#ffcc80', glow: false },
    strings: { bg: '#261d2a', fg: '#e1bee7', glow: false },
    dlstrings: { bg: '#261d2a', fg: '#e1bee7', glow: false },
    ilstrings: { bg: '#261d2a', fg: '#e1bee7', glow: false },
    ini: { bg: '#1e2430', fg: '#7eb8da', glow: false },
    json: { bg: '#1a2a1e', fg: '#8bc34a', glow: false },
    txt: { bg: '#2a2520', fg: '#d7ccc8', glow: false },
};

const FALLBACK_DEFAULT = { bg: '#2a2a2e', fg: '#a0a0a8', glow: false };

const CONFIG_TAB_TYPES = new Set(['ini', 'json', 'txt']);

export const MODS_TAB_TYPE_KEYS = Object.keys(DEFAULTS_BY_EXT)
    .filter((k) => !CONFIG_TAB_TYPES.has(k))
    .sort((a, b) => a.localeCompare(b));

export const CONFIGS_TAB_TYPE_KEYS = ['ini', 'json', 'txt'];

export function sanitizeExt(raw) {
    const s = String(raw || '').toLowerCase().trim();
    if (!/^[a-z0-9_-]+$/.test(s)) return '';
    return s;
}

export function getDefaultsForType(ext) {
    const key = sanitizeExt(ext) || 'unknown';
    if (key === 'unknown') return { ...FALLBACK_DEFAULT };
    return { ...(DEFAULTS_BY_EXT[key] || FALLBACK_DEFAULT) };
}

function normalizeHex(hex) {
    if (!hex || typeof hex !== 'string') return '#000000';
    let h = hex.trim();
    if (!/^#/.test(h)) h = `#${h}`;
    if (/^#[0-9a-fA-F]{3}$/.test(h)) {
        const r = h[1], g = h[2], b = h[3];
        h = `#${r}${r}${g}${g}${b}${b}`;
    }
    if (!/^#[0-9a-fA-F]{6}$/.test(h)) return '#000000';
    return h.toLowerCase();
}

function hexToRgba(hex, alpha) {
    const h = normalizeHex(hex);
    const n = parseInt(h.slice(1), 16);
    const r = (n >> 16) & 255;
    const g = (n >> 8) & 255;
    const b = n & 255;
    return `rgba(${r},${g},${b},${alpha})`;
}

export function loadMap() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (!raw) return {};
        const parsed = JSON.parse(raw);
        return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
    } catch (_) {
        return {};
    }
}

export function saveMap(map) {
    try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(map));
    } catch (_) { }
}

export function getEffectiveTheme(ext) {
    const key = sanitizeExt(ext);
    if (!key) return { ...FALLBACK_DEFAULT };
    const map = loadMap();
    const custom = map[key];
    const base = getDefaultsForType(key);
    if (!custom || typeof custom !== 'object') return { ...base };
    return {
        bg: normalizeHex(custom.bg != null && String(custom.bg).trim() !== '' ? custom.bg : base.bg),
        fg: normalizeHex(custom.fg != null && String(custom.fg).trim() !== '' ? custom.fg : base.fg),
        glow: Boolean(custom.glow),
    };
}

export function themeForStorage(ext, theme) {
    const key = sanitizeExt(ext);
    if (!key) return null;
    const def = getDefaultsForType(key);
    const bg = normalizeHex(theme.bg);
    const fg = normalizeHex(theme.fg);
    const glow = Boolean(theme.glow);
    if (bg === normalizeHex(def.bg) && fg === normalizeHex(def.fg) && glow === def.glow) {
        return null;
    }
    return { bg, fg, glow };
}

export function removeTypeFromStorage(ext) {
    const key = sanitizeExt(ext);
    if (!key) return;
    const map = loadMap();
    if (!map[key]) return;
    delete map[key];
    saveMap(map);
}

export function serializeTheme(t) {
    return JSON.stringify({
        bg: normalizeHex(t.bg),
        fg: normalizeHex(t.fg),
        glow: !!t.glow,
    });
}

function badgeRuleForType(key) {
    const theme = getEffectiveTheme(key);
    const bg = normalizeHex(theme.bg);
    const fg = normalizeHex(theme.fg);
    const shadow = theme.glow ? `0 0 10px 2px ${hexToRgba(fg, 0.45)}` : 'none';
    return `.mods-table .type-cell .badge.badge-${key}{background:${bg}!important;color:${fg}!important;box-shadow:${shadow}!important;}\n`;
}

export function applyStylesheet() {
    const map = loadMap();
    const keys = new Set(Object.keys(DEFAULTS_BY_EXT));
    for (const rawKey of Object.keys(map)) {
        const key = sanitizeExt(rawKey);
        if (key) keys.add(key);
    }

    let css = '';
    for (const key of keys) {
        css += badgeRuleForType(key);
    }

    let el = document.getElementById(STYLE_ID);
    if (!el) {
        el = document.createElement('style');
        el.id = STYLE_ID;
        document.head.appendChild(el);
    }
    el.textContent = css;
}
