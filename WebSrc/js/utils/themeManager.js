import { DEFAULT_UI_THEME, UI_THEMES, isBuiltInUiTheme } from '../themes/registry.js';
import { applyLogoLayout, getLogoLayoutForTheme, normalizeLogoLayout, DEFAULT_LOGO_LAYOUT } from './logoLayout.js';
import { setSharpLogo, refreshSharpLogos } from './logoSharpen.js';

const STORAGE_KEY = 'f76_ui_theme';
const DEFAULT_LOGO = 'assets/Icon-nobg.png';
const DEFAULT_HERO_BANNER = 'assets/dashboard_hero.jpg';
const VHOST = 'https://f76manager.app/';
const PREVIEW_THEME_ID = '__draft';
const PREVIEW_STYLE_ID = 'user-theme-__draft';
const LOGO_INLINE_VARS = [
    '--logo-width', '--logo-height', '--logo-scale',
    '--logo-offset-x', '--logo-offset-y',
    '--logo-object-fit', '--logo-object-position', '--logo-opacity',
    '--logo-shadow-y', '--logo-shadow-blur', '--logo-shadow-opacity',
    '--logo-collapsed-scale', '--logo-collapsed-offset-x', '--logo-collapsed-margin',
];

function assetVersion() {
    if (typeof window.__F76_ASSET_VERSION === 'string' && window.__F76_ASSET_VERSION) {
        return window.__F76_ASSET_VERSION;
    }
    const link = document.querySelector('link[href*="css/style.css"]');
    const href = link?.getAttribute('href') || '';
    const m = href.match(/[?&]v=([^&]+)/);
    return m ? m[1] : String(Date.now());
}

export const DEFAULT_BANNER_LAYOUT = Object.freeze({ x: 50, y: 35, zoom: 1 });

export function normalizeBannerLayout(layout) {
    const num = (v, lo, hi, def) => {
        const n = Number(v);
        return Number.isFinite(n) ? Math.min(hi, Math.max(lo, n)) : def;
    };
    const L = layout || {};
    return {
        x: num(L.x, 0, 100, DEFAULT_BANNER_LAYOUT.x),
        y: num(L.y, 0, 100, DEFAULT_BANNER_LAYOUT.y),
        zoom: num(L.zoom, 1, 4, DEFAULT_BANNER_LAYOUT.zoom),
    };
}

export function heroBannerStyle(layout) {
    const L = normalizeBannerLayout(layout);
    const pos = `${L.x}% ${L.y}%`;
    return `object-position: ${pos}; transform-origin: ${pos}; transform: scale(${L.zoom});`;
}

function withAssetVersion(url) {
    if (!url || url.startsWith('data:')) return url;
    const sep = url.includes('?') ? '&' : '?';
    return `${url}${sep}v=${encodeURIComponent(assetVersion())}`;
}

const userThemesById = new Map();

let previewState = null;

export function isThemePreviewActive() {
    return previewState != null;
}

export function registerUserThemesFromHost(themes, { authoritative = false } = {}) {
    if (!Array.isArray(themes)) return;
    const previous = new Map(userThemesById);
    userThemesById.clear();
    const bootCss = typeof window !== 'undefined' ? window.__F76_USER_THEME_CSS : null;
    const bootBanners = typeof window !== 'undefined' ? window.__F76_USER_THEME_BANNERS : null;
    if (authoritative) pruneRemovedUserThemes(previous, themes, bootCss);
    for (const t of themes) {
        if (!t?.id) continue;
        const prev = previous.get(t.id);
        if (t.css && bootCss) bootCss[t.id] = t.css;
        userThemesById.set(t.id, {
            id: t.id,
            displayName: t.displayName || prev?.displayName || t.id,
            logo: t.logo || prev?.logo || `user-theme-logo/${t.id}`,
            banner: t.banner !== undefined ? t.banner : (prev?.banner || bootBanners?.[t.id]?.banner || ''),
            bannerLayout: normalizeBannerLayout(t.bannerLayout || prev?.bannerLayout || bootBanners?.[t.id]?.bannerLayout),
            css: t.css || prev?.css || bootCss?.[t.id] || '',
        });
    }
    if (!authoritative && bootCss) {
        for (const [id, css] of Object.entries(bootCss)) {
            const entry = userThemesById.get(id);
            if (entry && !entry.css) entry.css = css;
            else if (!entry && css) {
                userThemesById.set(id, { id, displayName: id, logo: `user-theme-logo/${id}`, banner: '', bannerLayout: normalizeBannerLayout(null), css });
            }
        }
    }
    for (const [id, t] of userThemesById) {
        if (t.css) ensureUserThemeStyle(id, t.css);
    }
    if (authoritative && !previewState) {
        const applied = document.documentElement.dataset.theme;
        if (applied && !isBuiltInUiTheme(applied) && applied !== PREVIEW_THEME_ID && !userThemesById.has(applied)) {
            applyUiTheme(DEFAULT_UI_THEME);
        }
    }
}

function pruneRemovedUserThemes(previous, themes, bootCss) {
    const keep = new Set(themes.filter((t) => t?.id).map((t) => t.id));
    const stale = new Set([...previous.keys(), ...Object.keys(bootCss || {})]);
    for (const id of stale) {
        if (keep.has(id)) continue;
        if (bootCss) delete bootCss[id];
        document.getElementById(`user-theme-${id}`)?.remove();
        if (previewState?.previousThemeId === id) previewState.previousThemeId = DEFAULT_UI_THEME;
    }
    if (typeof window !== 'undefined' && Array.isArray(window.__F76_USER_THEME_IDS)) {
        window.__F76_USER_THEME_IDS = window.__F76_USER_THEME_IDS.filter((id) => keep.has(id));
    }
}

function ensureUserThemeStyle(themeId, css) {
    const styleId = `user-theme-${themeId}`;
    let el = document.getElementById(styleId);
    if (!el) {
        el = document.createElement('style');
        el.id = styleId;
    }
    document.head.appendChild(el);
    el.textContent = css;
}

export function unregisterUserTheme(themeId) {
    if (!themeId) return;
    userThemesById.delete(themeId);
    if (typeof window !== 'undefined' && window.__F76_USER_THEME_CSS) {
        delete window.__F76_USER_THEME_CSS[themeId];
    }
    if (typeof window !== 'undefined' && Array.isArray(window.__F76_USER_THEME_IDS)) {
        window.__F76_USER_THEME_IDS = window.__F76_USER_THEME_IDS.filter((id) => id !== themeId);
    }
    document.getElementById(`user-theme-${themeId}`)?.remove();
    if (previewState?.previousThemeId === themeId) previewState.previousThemeId = DEFAULT_UI_THEME;
    try {
        if (localStorage.getItem(STORAGE_KEY) === themeId) localStorage.removeItem(STORAGE_KEY);
    } catch {
    }
}

export function isUserThemeId(themeId) {
    return !!themeId && userThemesById.has(themeId);
}

export function getRegisteredUserThemes() {
    return [...userThemesById.values()];
}

function getAppliedThemeId() {
    const id = previewState ? previewState.previousThemeId : document.documentElement.dataset.theme;
    return id && id !== PREVIEW_THEME_ID && isValidUiTheme(id) ? id : null;
}

export function getThemeOptionsForSettings(ms, { followApplied = false } = {}) {
    const active = (followApplied && getAppliedThemeId()) || ms?.uiTheme || DEFAULT_UI_THEME;
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

function clearInlineLogoVars() {
    const root = document.documentElement;
    for (const name of LOGO_INLINE_VARS) {
        root.style.removeProperty(name);
    }
}

function applyThemeLogo(themeId) {
    const el = document.getElementById('app-logo');
    if (!el) return;
    setSharpLogo(el, withAssetVersion(logoForTheme(themeId)));
    if (userThemesById.has(themeId)) {
        clearInlineLogoVars();
    } else {
        applyLogoLayout(getLogoLayoutForTheme(themeId));
    }
    refreshSharpLogos();
}

export function getHeroBannerUrl() {
    if (previewState && previewState.bannerUrl !== undefined) {
        return previewState.bannerUrl
            ? toVirtualHostUrl(previewState.bannerUrl)
            : toVirtualHostUrl(DEFAULT_HERO_BANNER);
    }
    const themeId = getAppliedThemeId();
    const user = themeId ? userThemesById.get(themeId) : null;
    if (user?.banner) return toVirtualHostUrl(user.banner);
    return toVirtualHostUrl(DEFAULT_HERO_BANNER);
}

export function getBannerForTheme(themeId) {
    const user = themeId ? userThemesById.get(themeId) : null;
    if (!user?.banner) return null;
    return { url: toVirtualHostUrl(user.banner), layout: normalizeBannerLayout(user.bannerLayout) };
}

export function getHeroBannerLayout() {
    if (previewState && previewState.bannerLayout) return normalizeBannerLayout(previewState.bannerLayout);
    const themeId = getAppliedThemeId();
    const user = themeId ? userThemesById.get(themeId) : null;
    return normalizeBannerLayout(user?.banner ? user.bannerLayout : null);
}

export function applyHeroBanner(root = document) {
    const url = getHeroBannerUrl();
    const next = url.startsWith('data:') ? url : withAssetVersion(url);
    const style = heroBannerStyle(getHeroBannerLayout());
    root.querySelectorAll('.hero-img').forEach((img) => {
        if (img.getAttribute('src') !== next) img.src = next;
        img.style.cssText = style;
    });
}

export function getUiTheme() {
    if (previewState) return PREVIEW_THEME_ID;
    const fromDom = document.documentElement.dataset.theme;
    if (fromDom && fromDom !== PREVIEW_THEME_ID && isValidUiTheme(fromDom)) return fromDom;
    try {
        const stored = localStorage.getItem(STORAGE_KEY);
        if (stored && isValidUiTheme(stored)) return stored;
    } catch {
    }
    return DEFAULT_UI_THEME;
}

export function applyUiTheme(id) {
    if (previewState) {
        if (id && id !== PREVIEW_THEME_ID && isValidUiTheme(id)) previewState.previousThemeId = id;
        return previewState.previousThemeId || DEFAULT_UI_THEME;
    }
    const themeId = isValidUiTheme(id) ? id : DEFAULT_UI_THEME;
    document.documentElement.dataset.theme = themeId;
    const user = userThemesById.get(themeId);
    if (user?.css) ensureUserThemeStyle(themeId, user.css);
    applyThemeLogo(themeId);
    applyHeroBanner();
    try {
        localStorage.setItem(STORAGE_KEY, themeId);
    } catch {
    }
    postWindowChromeColors();
    return themeId;
}

function cssColorToHex(value) {
    if (!value) return null;
    const v = String(value).trim();
    if (v.startsWith('#')) {
        if (v.length === 4) {
            return `#${v[1]}${v[1]}${v[2]}${v[2]}${v[3]}${v[3]}`.toLowerCase();
        }
        return v.slice(0, 7).toLowerCase();
    }
    const m = v.match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
    if (!m) return null;
    const hex = (n) => Number(n).toString(16).padStart(2, '0');
    return `#${hex(m[1])}${hex(m[2])}${hex(m[3])}`;
}

function postWindowChromeColors() {
    try {
        if (!window.chrome?.webview?.postMessage) return;
        const styles = getComputedStyle(document.documentElement);
        const caption = cssColorToHex(styles.getPropertyValue('--bg-dark')) || '#121212';
        const text = cssColorToHex(styles.getPropertyValue('--text-main')) || '#e0e0e0';
        const border = cssColorToHex(styles.getPropertyValue('--border-color')) || '#333333';
        const themeId = document.documentElement.dataset.theme || getUiTheme();
        const styleEl = document.getElementById(`user-theme-${themeId}`);
        const css = styleEl?.textContent || '';
        window.chrome.webview.postMessage({
            type: 'SET_WINDOW_CHROME',
            caption,
            text,
            border,
            themeId,
            css,
        });
    } catch {
    }
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
    if (id === PREVIEW_THEME_ID) return true;
    if (isBuiltInUiTheme(id)) return true;
    return userThemesById.has(id);
}

export function buildDraftThemeCss(themeId, tokens, logoLayout) {
    const L = normalizeLogoLayout(logoLayout);
    const lines = [`:root[data-theme="${themeId}"] {`];
    for (const [key, val] of Object.entries(tokens || {})) {
        if (val == null || val === '') continue;
        lines.push(`  --${key}: ${val};`);
    }
    if (tokens?.['primary-rgb']) {
        lines.push(`  --primary-green-dim: rgba(${tokens['primary-rgb']}, 0.7);`);
    }
    const width = L.width;
    lines.push(`  --logo-width: ${width}px;`);
    lines.push(`  --logo-height: ${L.height > 0 ? `${L.height}px` : 'var(--topbar-height)'};`);
    lines.push(`  --logo-scale: ${L.scale};`);
    lines.push(`  --logo-offset-x: ${L.offsetX}px;`);
    lines.push(`  --logo-offset-y: ${L.offsetY}px;`);
    lines.push(`  --logo-object-fit: ${L.objectFit};`);
    lines.push(`  --logo-object-position: ${L.objectPosition};`);
    lines.push(`  --logo-opacity: ${L.opacity};`);
    lines.push(`  --logo-shadow-y: ${L.shadowY}px;`);
    lines.push(`  --logo-shadow-blur: ${L.shadowBlur}px;`);
    lines.push(`  --logo-shadow-opacity: ${L.shadowOpacity};`);
    lines.push(`  --logo-collapsed-scale: ${L.collapsedScale};`);
    lines.push(`  --logo-collapsed-offset-x: ${L.collapsedOffsetX}px;`);
    lines.push(`  --logo-collapsed-margin: ${-width / 2}px;`);
    lines.push('}');
    return lines.join('\n');
}

export function beginThemePreview(css, logoUrl, layout, bannerUrl, bannerLayout) {
    if (!previewState) {
        const logoEl = document.getElementById('app-logo');
        previewState = {
            previousThemeId: document.documentElement.dataset.theme || getUiTheme(),
            previousLogoSrc: logoEl?.getAttribute('src') || null,
            bannerUrl: undefined,
            bannerLayout: null,
        };
        if (previewState.previousThemeId === PREVIEW_THEME_ID) {
            previewState.previousThemeId = DEFAULT_UI_THEME;
        }
    }
    updateThemePreview(css, logoUrl, layout, bannerUrl, bannerLayout);
}

export function updateThemePreview(css, logoUrl, layout, bannerUrl, bannerLayout) {
    if (!previewState) {
        beginThemePreview(css, logoUrl, layout, bannerUrl, bannerLayout);
        return;
    }
    if (bannerLayout) previewState.bannerLayout = normalizeBannerLayout(bannerLayout);
    ensureUserThemeStyle(PREVIEW_THEME_ID, css || '');
    document.documentElement.dataset.theme = PREVIEW_THEME_ID;
    clearInlineLogoVars();
    if (layout) applyLogoLayout(layout);
    refreshSharpLogos();
    const logoEl = document.getElementById('app-logo');
    if (logoEl && logoUrl) {
        setSharpLogo(logoEl, withAssetVersion(logoUrl));
    }
    if (bannerUrl !== undefined) previewState.bannerUrl = bannerUrl || '';
    applyHeroBanner();
    postWindowChromeColors();
}

let lastHeroSizeKey = '';
let heroResizeTimer = null;
let heroResizeBound = false;

export function reportHeroBannerSize() {
    if (!heroResizeBound && typeof window !== 'undefined') {
        heroResizeBound = true;
        window.addEventListener('resize', () => {
            if (!previewState) return;
            clearTimeout(heroResizeTimer);
            heroResizeTimer = setTimeout(() => reportHeroBannerSize(), 150);
        });
    }
    const el = document.querySelector('#content-area .hero-banner');
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const width = Math.round(rect.width);
    const height = Math.round(rect.height);
    if (width <= 0 || height <= 0) return;
    const key = `${width}x${height}`;
    if (key === lastHeroSizeKey) return;
    lastHeroSizeKey = key;
    try {
        window.chrome?.webview?.postMessage({ type: 'HERO_BANNER_SIZE', width, height });
    } catch {  }
}

export function endThemePreview({ restore = true } = {}) {
    const state = previewState;
    previewState = null;
    lastHeroSizeKey = '';
    const styleEl = document.getElementById(PREVIEW_STYLE_ID);
    if (styleEl) styleEl.remove();

    if (!restore || !state) {
        applyHeroBanner();
        return;
    }

    const themeId = isValidUiTheme(state.previousThemeId) && state.previousThemeId !== PREVIEW_THEME_ID
        ? state.previousThemeId
        : DEFAULT_UI_THEME;
    applyUiTheme(themeId);
}

export function readLiveThemeTokens(themeId) {
    const prev = document.documentElement.dataset.theme;
    const keys = [
        'bg-dark', 'bg-surface', 'bg-surface-light', 'bg-elevated', 'bg-inset',
        'primary-green', 'primary-rgb', 'primary-hover',
        'accent-amber', 'text-main', 'text-muted', 'border-color',
        'danger-red', 'danger-red-soft', 'success-green', 'warning-yellow', 'on-primary',
        'slider-track',
    ];
    try {
        if (themeId && themeId !== PREVIEW_THEME_ID) {
            document.documentElement.dataset.theme = themeId;
            const user = userThemesById.get(themeId);
            if (user?.css) ensureUserThemeStyle(themeId, user.css);
        }
        void document.documentElement.offsetHeight;
        const styles = getComputedStyle(document.documentElement);
        const tokens = {};
        for (const key of keys) {
            const raw = styles.getPropertyValue(`--${key}`).trim();
            tokens[key] = raw || '';
        }
        return tokens;
    } finally {
        document.documentElement.dataset.theme = prev;
    }
}

export function getLogoUrlForTheme(themeId) {
    return withAssetVersion(logoForTheme(themeId));
}

export function getBuiltInLogoOptions() {
    return [
        ...UI_THEMES.map((t) => ({
            id: t.id,
            labelKey: t.labelKey,
            url: withAssetVersion(toVirtualHostUrl(t.logo)),
        })),
        {
            id: 'app-icon',
            labelKey: 'theme_creator_logo_app_icon',
            url: withAssetVersion(toVirtualHostUrl(DEFAULT_LOGO)),
        },
    ];
}

export function getDefaultLogoLayout() {
    return { ...DEFAULT_LOGO_LAYOUT };
}

if (typeof window !== 'undefined') {
    const ids = window.__F76_USER_THEME_IDS;
    if (Array.isArray(ids) && ids.length) {
        registerUserThemesFromHost(ids.map((id) => ({ id })));
    }
}
