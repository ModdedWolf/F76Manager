import { UI_THEMES } from '../themes/registry.js';

export const DEFAULT_LOGO_LAYOUT = {
    width: 44,
    height: 0,
    scale: 1,
    offsetX: 0,
    offsetY: 0,
    objectFit: 'cover',
    objectPosition: 'center bottom',
    opacity: 1,
    shadowY: 4,
    shadowBlur: 12,
    shadowOpacity: 0.5,
    collapsedScale: 1,
    collapsedOffsetX: 0,
};

const OBJECT_FITS = new Set(['cover', 'contain', 'fill', 'none', 'scale-down']);

export function normalizeLogoLayout(raw) {
    const base = { ...DEFAULT_LOGO_LAYOUT };
    if (!raw || typeof raw !== 'object') return base;

    const n = (key, min, max, fallback) => {
        const v = Number(raw[key]);
        if (!Number.isFinite(v)) return fallback;
        return Math.min(max, Math.max(min, v));
    };

    base.width = n('width', 16, 120, base.width);
    base.height = n('height', 0, 120, base.height);
    base.scale = n('scale', 0.25, 3, base.scale);
    base.offsetX = n('offsetX', -40, 40, base.offsetX);
    base.offsetY = n('offsetY', -40, 40, base.offsetY);
    base.opacity = n('opacity', 0, 1, base.opacity);
    base.shadowY = n('shadowY', 0, 24, base.shadowY);
    base.shadowBlur = n('shadowBlur', 0, 48, base.shadowBlur);
    base.shadowOpacity = n('shadowOpacity', 0, 1, base.shadowOpacity);
    base.collapsedScale = n('collapsedScale', 0.25, 3, base.collapsedScale);
    base.collapsedOffsetX = n('collapsedOffsetX', -40, 40, base.collapsedOffsetX);

    const fit = String(raw.objectFit || base.objectFit).toLowerCase();
    base.objectFit = OBJECT_FITS.has(fit) ? fit : base.objectFit;

    const pos = String(raw.objectPosition || base.objectPosition).trim();
    base.objectPosition = pos || base.objectPosition;

    return base;
}

export function applyLogoLayout(layout) {
    const L = normalizeLogoLayout(layout);
    const root = document.documentElement;

    root.style.setProperty('--logo-width', `${L.width}px`);
    root.style.setProperty('--logo-height', L.height > 0 ? `${L.height}px` : 'var(--topbar-height)');
    root.style.setProperty('--logo-scale', String(L.scale));
    root.style.setProperty('--logo-offset-x', `${L.offsetX}px`);
    root.style.setProperty('--logo-offset-y', `${L.offsetY}px`);
    root.style.setProperty('--logo-object-fit', L.objectFit);
    root.style.setProperty('--logo-object-position', L.objectPosition);
    root.style.setProperty('--logo-opacity', String(L.opacity));
    root.style.setProperty('--logo-shadow-y', `${L.shadowY}px`);
    root.style.setProperty('--logo-shadow-blur', `${L.shadowBlur}px`);
    root.style.setProperty('--logo-shadow-opacity', String(L.shadowOpacity));
    root.style.setProperty('--logo-collapsed-scale', String(L.collapsedScale));
    root.style.setProperty('--logo-collapsed-offset-x', `${L.collapsedOffsetX}px`);
    root.style.setProperty('--logo-collapsed-margin', `${-L.width / 2}px`);

    return L;
}

export function getLogoLayoutForTheme(themeId) {
    const entry = UI_THEMES.find((t) => t.id === themeId);
    return normalizeLogoLayout(entry?.logoLayout);
}

