import { DEFAULT_UI_THEME, UI_THEMES, isBuiltInUiTheme } from '../themes/registry.js';
import { applyLogoLayout, getLogoLayoutForTheme } from './logoLayout.js';

const STORAGE_KEY = 'f76_ui_theme';
const DEFAULT_LOGO = 'assets/Icon-nobg.png';
const VHOST = 'https://f76manager.app/';

function assetVersion() {
    if (typeof window.__F76_ASSET_VERSION === 'string' && window.__F76_ASSET_VERSION) {
        return window.__F76_ASSET_VERSION;
    }
    const link = document.querySelector('link[href*="css/style.css"]');
    const href = link?.getAttribute('href') || '';
    const m = href.match(/[?&]v=([^&]+)/);
    return m ? m[1] : String(Date.now());
}

function withAssetVersion(url) {
    if (!url || url.startsWith('data:')) return url;
    const sep = url.includes('?') ? '&' : '?';
    return `${url}${sep}v=${encodeURIComponent(assetVersion())}`;
}

const userThemesById = new Map();

export function registerUserThemesFromHost(themes) {
    userThemesById.clear();
    if (!Array.isArray(themes)) return;
    for (const t of themes) {
        if (!t?.id) continue;
        userThemesById.set(t.id, {
            id: t.id,
            displayName: t.displayName || t.id,
            logo: t.logo || `user-theme-logo/${t.id}`,
            css: t.css || (typeof window !== 'undefined' && window.__F76_USER_THEME_CSS?.[t.id]) || '',
        });
    }
    if (typeof window !== 'undefined' && window.__F76_USER_THEME_CSS) {
        for (const [id, css] of Object.entries(window.__F76_USER_THEME_CSS)) {
            const entry = userThemesById.get(id);
            if (entry && !entry.css) entry.css = css;
            else if (!entry && css) {
                userThemesById.set(id, { id, displayName: id, logo: `user-theme-logo/${id}`, css });
            }
        }
    }
    for (const [id, t] of userThemesById) {
        if (t.css) ensureUserThemeStyle(id, t.css);
    }
}

function ensureUserThemeStyle(themeId, css) {
    const styleId = `user-theme-${themeId}`;
    let el = document.getElementById(styleId);
    if (!el) {
        el = document.createElement('style');
        el.id = styleId;
        document.head.appendChild(el);
    }
    el.textContent = css;
}

export function getThemeOptionsForSettings(ms) {
    const active = ms?.uiTheme || DEFAULT_UI_THEME;
    const builtin = UI_THEMES.map((th) => ({
        id: th.id,
        labelKey: th.labelKey,
        isUser: false,
        selected: active === th.id,
    }));
    const user = [...userThemesById.values()].map((t) => ({
        id: t.id,
        displayName: t.displayName,
        isUser: true,
        selected: active === t.id,
    }));
    return [...builtin, ...user];
}

function toVirtualHostUrl(path) {
    if (!path || path.startsWith('http') || path.startsWith('data:')) return path;
    return `${VHOST}${path.replace(/^\//, '')}`;
}

function logoForTheme(themeId) {
    const user = userThemesById.get(themeId);
    if (user) {
        return toVirtualHostUrl(user.logo);
    }
    const entry = UI_THEMES.find((t) => t.id === themeId);
    return entry?.logo ? toVirtualHostUrl(entry.logo) : toVirtualHostUrl(DEFAULT_LOGO);
}

function applyThemeLogo(themeId) {
    const el = document.getElementById('app-logo');
    if (!el) return;
    el.src = withAssetVersion(logoForTheme(themeId));
    if (!userThemesById.has(themeId)) {
        applyLogoLayout(getLogoLayoutForTheme(themeId));
    }
}

export function getUiTheme() {
    const fromDom = document.documentElement.dataset.theme;
    if (fromDom && isValidUiTheme(fromDom)) return fromDom;
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored && isValidUiTheme(stored)) return stored;
    } catch {
    }
    return DEFAULT_UI_THEME;
}

export function applyUiTheme(id) {
    const themeId = isValidUiTheme(id) ? id : DEFAULT_UI_THEME;
    document.documentElement.dataset.theme = themeId;
    const user = userThemesById.get(themeId);
    if (user?.css) ensureUserThemeStyle(themeId, user.css);
    applyThemeLogo(themeId);
    try {
        localStorage.setItem(STORAGE_KEY, themeId);
    } catch {
    }
    return themeId;
}

export function applyUiThemeEarly() {
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored && isValidUiTheme(stored)) {
            document.documentElement.dataset.theme = stored;
            const css = window.__F76_USER_THEME_CSS?.[stored];
            if (css) ensureUserThemeStyle(stored, css);
            return;
        }
    } catch {
    }
    document.documentElement.dataset.theme = DEFAULT_UI_THEME;
}

export function isValidUiTheme(id) {
    if (typeof id !== 'string' || !id) return false;
    if (isBuiltInUiTheme(id)) return true;
    return userThemesById.has(id);
}

if (typeof window !== 'undefined') {
    const ids = window.__F76_USER_THEME_IDS;
    if (Array.isArray(ids) && ids.length) {
        registerUserThemesFromHost(ids.map((id) => ({ id })));
    }
}
