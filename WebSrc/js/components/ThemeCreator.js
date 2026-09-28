import { escapeHtml, escapeAttr } from '../utils/htmlSafe.js';
import { DEFAULT_UI_THEME, isBuiltInUiTheme } from '../themes/registry.js';
import { DEFAULT_LOGO_LAYOUT, getLogoLayoutForTheme, normalizeLogoLayout } from '../utils/logoLayout.js';
import {
    beginThemePreview,
    updateThemePreview,
    isThemePreviewActive,
    buildDraftThemeCss,
    readLiveThemeTokens,
    getLogoUrlForTheme,
    getThemeOptionsForSettings,
    getBuiltInLogoOptions,
} from '../utils/themeManager.js';
import { appConfirm } from '../utils/appConfirm.js';
import { setSharpLogo } from '../utils/logoSharpen.js';

const DRAFT_ID = '__draft';
const MAX_LOGO_DIM = 512;
const MAX_LOGO_BYTES = 2 * 1024 * 1024;

const COLOR_TOKENS = [
    { key: 'bg-dark', labelKey: 'theme_token_bg_dark', derived: false },
    { key: 'bg-surface', labelKey: 'theme_token_bg_surface', derived: false },
    { key: 'bg-surface-light', labelKey: 'theme_token_bg_surface_light', derived: false },
    { key: 'bg-elevated', labelKey: 'theme_token_bg_elevated', derived: true },
    { key: 'bg-inset', labelKey: 'theme_token_bg_inset', derived: true },
    { key: 'primary-green', labelKey: 'theme_token_primary', derived: false },
    { key: 'primary-hover', labelKey: 'theme_token_primary_hover', derived: true },
    { key: 'accent-amber', labelKey: 'theme_token_accent', derived: false },
    { key: 'text-main', labelKey: 'theme_token_text_main', derived: false },
    { key: 'text-muted', labelKey: 'theme_token_text_muted', derived: false },
    { key: 'border-color', labelKey: 'theme_token_border', derived: false },
    { key: 'danger-red', labelKey: 'theme_token_danger', derived: false },
    { key: 'danger-red-soft', labelKey: 'theme_token_danger_soft', derived: true },
    { key: 'success-green', labelKey: 'theme_token_success', derived: false },
    { key: 'warning-yellow', labelKey: 'theme_token_warning', derived: false },
    { key: 'on-primary', labelKey: 'theme_token_on_primary', derived: false },
    { key: 'slider-track', labelKey: 'theme_token_slider_track', derived: true },
];

const DEFAULT_AUTO_KEYS = ['bg-elevated', 'bg-inset', 'primary-hover', 'danger-red-soft', 'slider-track'];

const OBJECT_FITS = ['cover', 'contain', 'fill', 'none', 'scale-down'];

const RANDOM_MODES = [
    { id: 'harmonious', labelKey: 'theme_creator_random_harmonious', fallback: 'Harmonious' },
    { id: 'vibrant', labelKey: 'theme_creator_random_vibrant', fallback: 'Vibrant' },
    { id: 'chaos', labelKey: 'theme_creator_random_chaos', fallback: 'Chaos' },
];

function t(key, ...args) {
    return window._t?.(key, ...args) || key;
}

function tOr(key, fallback) {
    const v = t(key);
    return v && v !== key ? v : fallback;
}

function slugify(name) {
    return String(name || '')
        .toLowerCase()
        .normalize('NFKD')
        .replace(/[\u0300-\u036f]/g, '')
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .replace(/-+/g, '-')
        .slice(0, 48);
}

function isValidThemeId(id) {
    return /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/.test(id) && id !== 'my-theme' && id !== 'default';
}

function parseColor(value) {
    const v = String(value || '').trim();
    let m = v.match(/^#([0-9a-f]{6})$/i);
    if (m) {
        return {
            r: parseInt(m[1].slice(0, 2), 16),
            g: parseInt(m[1].slice(2, 4), 16),
            b: parseInt(m[1].slice(4, 6), 16),
            a: 1,
        };
    }
    m = v.match(/^#([0-9a-f]{8})$/i);
    if (m) {
        return {
            r: parseInt(m[1].slice(0, 2), 16),
            g: parseInt(m[1].slice(2, 4), 16),
            b: parseInt(m[1].slice(4, 6), 16),
            a: parseInt(m[1].slice(6, 8), 16) / 255,
        };
    }
    m = v.match(/^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*(0|1|0?\.\d+|1\.0+))?\s*\)$/i);
    if (m) {
        return {
            r: Math.min(255, +m[1]),
            g: Math.min(255, +m[2]),
            b: Math.min(255, +m[3]),
            a: m[4] != null ? Math.min(1, Math.max(0, +m[4])) : 1,
        };
    }
    return null;
}

function toHex6({ r, g, b }) {
    const h = (n) => Math.max(0, Math.min(255, Math.round(n))).toString(16).padStart(2, '0');
    return `#${h(r)}${h(g)}${h(b)}`;
}

function toCssColor({ r, g, b, a }) {
    if (a == null || a >= 0.999) return toHex6({ r, g, b });
    const aa = Math.round(a * 1000) / 1000;
    return `rgba(${Math.round(r)}, ${Math.round(g)}, ${Math.round(b)}, ${aa})`;
}

function lighten(color, amount) {
    const c = parseColor(color);
    if (!c) return color;
    return toHex6({
        r: c.r + (255 - c.r) * amount,
        g: c.g + (255 - c.g) * amount,
        b: c.b + (255 - c.b) * amount,
    });
}

function darken(color, amount) {
    const c = parseColor(color);
    if (!c) return color;
    return toHex6({
        r: c.r * (1 - amount),
        g: c.g * (1 - amount),
        b: c.b * (1 - amount),
    });
}

function mix(a, b, tFrac) {
    const ca = parseColor(a);
    const cb = parseColor(b);
    if (!ca || !cb) return a;
    return toHex6({
        r: ca.r + (cb.r - ca.r) * tFrac,
        g: ca.g + (cb.g - ca.g) * tFrac,
        b: ca.b + (cb.b - ca.b) * tFrac,
    });
}

function hslToHex(h, s, l) {
    const hh = ((h % 360) + 360) % 360;
    const ss = Math.max(0, Math.min(100, s)) / 100;
    const ll = Math.max(0, Math.min(100, l)) / 100;
    const k = (n) => (n + hh / 30) % 12;
    const a = ss * Math.min(ll, 1 - ll);
    const f = (n) => ll - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
    return toHex6({ r: f(0) * 255, g: f(8) * 255, b: f(4) * 255 });
}

function relativeLuminance(color) {
    const c = parseColor(color);
    if (!c) return 0;
    const ch = (v) => {
        const x = v / 255;
        return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * ch(c.r) + 0.7152 * ch(c.g) + 0.0722 * ch(c.b);
}

function contrastRatio(a, b) {
    const la = relativeLuminance(a);
    const lb = relativeLuminance(b);
    return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

function rand(min, max) {
    return min + Math.random() * (max - min);
}

function pick(arr) {
    return arr[Math.floor(Math.random() * arr.length)];
}

function hslWithContrast(h, s, l, bg, minRatio, towardLight) {
    let light = l;
    let color = hslToHex(h, s, light);
    for (let i = 0; i < 50 && contrastRatio(color, bg) < minRatio; i++) {
        light += towardLight ? 2 : -2;
        if (light < 0 || light > 100) break;
        color = hslToHex(h, s, light);
    }
    return color;
}

function randomHex() {
    return toHex6({ r: rand(0, 256), g: rand(0, 256), b: rand(0, 256) });
}

function generateRandomPalette(mode) {
    if (mode === 'chaos') {
        const out = {};
        for (const { key } of COLOR_TOKENS) out[key] = randomHex();
        return out;
    }

    const vibrant = mode === 'vibrant';
    const dark = vibrant || Math.random() < 0.7;
    const hue = rand(0, 360);
    const bgSat = vibrant ? rand(35, 60) : rand(8, 22);
    const base = dark ? rand(6, 9) : rand(92, 96);
    const bgL = dark ? [base, base + 4, base + 10] : [base, base - 5, base - 11];
    const bgDark = hslToHex(hue, bgSat, bgL[0]);

    const primaryHue = hue + pick([0, 30, -30, 150, 180, 210]);
    const primarySat = vibrant ? rand(85, 100) : rand(35, 60);
    const primaryL = dark ? (vibrant ? rand(55, 65) : rand(60, 72)) : rand(32, 42);
    const primary = hslWithContrast(primaryHue, primarySat, primaryL, bgDark, 3, dark);

    const accentHue = primaryHue + pick([60, -60, 120, -120, 180]);
    const accent = hslWithContrast(accentHue, primarySat, primaryL, bgDark, 3, dark);

    const statusSat = vibrant ? rand(80, 100) : rand(55, 80);
    const statusL = dark ? rand(52, 62) : rand(38, 46);
    const status = (h) => hslWithContrast(h, statusSat, statusL, bgDark, 3, dark);

    const onPrimary = contrastRatio(primary, '#121212') >= contrastRatio(primary, '#ffffff')
        ? '#121212'
        : '#ffffff';

    return {
        'bg-dark': bgDark,
        'bg-surface': hslToHex(hue, bgSat, bgL[1]),
        'bg-surface-light': hslToHex(hue, bgSat, bgL[2]),
        'primary-green': primary,
        'accent-amber': accent,
        'text-main': hslWithContrast(hue, rand(4, 12), dark ? rand(88, 94) : rand(8, 14), bgDark, 7, dark),
        'text-muted': hslWithContrast(hue, rand(5, 14), dark ? rand(62, 70) : rand(38, 46), bgDark, 4.5, dark),
        'border-color': hslToHex(hue, bgSat, dark ? bgL[2] + 6 : bgL[2] - 6),
        'danger-red': status(rand(-5, 10)),
        'success-green': status(rand(115, 145)),
        'warning-yellow': status(rand(42, 55)),
        'on-primary': onPrimary,
    };
}

function deriveToken(key, tokens) {
    switch (key) {
        case 'primary-hover':
            return lighten(tokens['primary-green'] || '#b8c5a4', 0.12);
        case 'bg-elevated':
            return mix(tokens['bg-surface'] || '#1e1e1e', tokens['bg-surface-light'] || '#2d2d2d', 0.5);
        case 'bg-inset':
            return darken(tokens['bg-dark'] || '#121212', 0.04);
        case 'slider-track':
            return mix(tokens['bg-surface-light'] || '#2d2d2d', tokens['primary-green'] || '#b8c5a4', 0.35);
        case 'danger-red-soft': {
            const c = parseColor(tokens['danger-red'] || '#e0564c') || { r: 224, g: 86, b: 76, a: 1 };
            return `rgba(${c.r}, ${c.g}, ${c.b}, 0.18)`;
        }
        default:
            return tokens[key] || '';
    }
}

function syncRangeFill(input) {
    const min = Number(input.min) || 0;
    const max = Number(input.max) || 100;
    const pct = max > min ? ((Number(input.value) - min) / (max - min)) * 100 : 0;
    input.style.setProperty('--value', `${Math.min(100, Math.max(0, pct))}%`);
}

function rgbTripletFromPrimary(primary) {
    const c = parseColor(primary);
    if (!c) return '184, 197, 164';
    return `${Math.round(c.r)}, ${Math.round(c.g)}, ${Math.round(c.b)}`;
}

function readFileAsDataUrl(blob) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = () => reject(new Error('read failed'));
        reader.readAsDataURL(blob);
    });
}

function isAnimatedFriendlyMime(file) {
    const type = (file.type || '').toLowerCase();
    const name = (file.name || '').toLowerCase();
    return type === 'image/gif' || type === 'image/webp'
        || name.endsWith('.gif') || name.endsWith('.webp');
}

async function resizeLogoFile(file) {
    if (file.size > MAX_LOGO_BYTES) {
        throw new Error(t('theme_creator_logo_too_large'));
    }

    if (isAnimatedFriendlyMime(file)) {
        const dataUrl = await readFileAsDataUrl(file);
        return { dataUrl, mime: file.type || 'image/gif' };
    }

    const bitmap = await createImageBitmap(file);
    const scale = Math.min(1, MAX_LOGO_DIM / Math.max(bitmap.width, bitmap.height));
    const w = Math.max(1, Math.round(bitmap.width * scale));
    const h = Math.max(1, Math.round(bitmap.height * scale));
    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext('2d');
    ctx.drawImage(bitmap, 0, 0, w, h);
    bitmap.close?.();

    const toBlob = (type, quality) => new Promise((resolve) => canvas.toBlob(resolve, type, quality));
    let blob = await toBlob('image/png');
    if (blob && blob.size > MAX_LOGO_BYTES) blob = await toBlob('image/jpeg', 0.88);
    if (blob && blob.size > MAX_LOGO_BYTES) blob = await toBlob('image/jpeg', 0.72);
    if (!blob || blob.size > MAX_LOGO_BYTES) {
        throw new Error(t('theme_creator_logo_too_large'));
    }
    const dataUrl = await readFileAsDataUrl(blob);
    return { dataUrl, mime: blob.type };
}

export class ThemeCreator {
    constructor(root) {
        this.root = root;
        this.activeTab = 'general';
        this.dirty = false;
        this.idManual = false;
        this.nameTouched = false;
        this.idTouched = false;
        this.autoKeys = new Set(DEFAULT_AUTO_KEYS);
        this.tokens = {};
        this.logoLayout = { ...DEFAULT_LOGO_LAYOUT };
        this.logoDataUrl = '';
        this._pendingLogoFetch = '';
        this.displayName = '';
        this.themeId = '';
        this.startFrom = DEFAULT_UI_THEME;
        this.editMode = false;
        this._abort = null;
        this._logoDrag = null;
        this._closing = false;
        this._status = { type: '', text: '' };
        this.randomMode = 'harmonious';
    }

    mount() {
        this._seedFromTheme(window.__F76_BOOT_UI_THEME || DEFAULT_UI_THEME, { keepIdentity: false });
        this._render();
        this._bind();
        this._pushPreview();
        if (window.lucide) window.lucide.createIcons();
    }

    loadForEdit(data) {
        if (!data?.id) return;
        this.editMode = true;
        this.startFrom = data.id;
        this.themeId = data.id;
        this.displayName = data.displayName || data.id;
        this.idManual = true;
        this.nameTouched = true;
        this.idTouched = true;

        const derivedKeys = COLOR_TOKENS.filter((c) => c.derived).map((c) => c.key);
        this.autoKeys = new Set();
        this.tokens = {};
        const incoming = data.tokens || {};
        for (const { key } of COLOR_TOKENS) {
            if (incoming[key] != null && incoming[key] !== '') {
                this.tokens[key] = incoming[key];
            } else if (derivedKeys.includes(key)) {
                this.autoKeys.add(key);
            }
        }
        for (const [key, val] of Object.entries(incoming)) {
            if (val != null && val !== '' && this.tokens[key] == null) this.tokens[key] = val;
        }
        for (const key of [...this.autoKeys]) {
            this.tokens[key] = deriveToken(key, this.tokens);
        }
        this.tokens['primary-rgb'] = rgbTripletFromPrimary(this.tokens['primary-green'] || incoming['primary-green'] || '#00ff9d');

        this.logoLayout = normalizeLogoLayout(data.logoLayout || DEFAULT_LOGO_LAYOUT);
        this.logoDataUrl = data.logoDataUrl || '';
        this._pendingLogoFetch = this.logoDataUrl ? '' : getLogoUrlForTheme(data.id);
        this.dirty = false;
        this._status = { type: '', text: '' };

        this._render();
        this._bind();
        this._pushPreview();
        if (window.lucide) window.lucide.createIcons();
    }

    _seedFromTheme(themeId, opts = {}) {
        const keepIdentity = !!opts.keepIdentity;
        this.startFrom = themeId || DEFAULT_UI_THEME;
        const tokens = readLiveThemeTokens(this.startFrom);
        for (const { key } of COLOR_TOKENS) {
            let val = tokens[key];
            if (!val || !parseColor(val)) val = deriveToken(key, tokens);
            this.tokens[key] = val;
        }
        this.tokens['primary-rgb'] = rgbTripletFromPrimary(this.tokens['primary-green']);
        this.autoKeys = new Set(DEFAULT_AUTO_KEYS);
        for (const key of [...this.autoKeys]) {
            this.tokens[key] = deriveToken(key, this.tokens);
        }

        this.logoLayout = isBuiltInUiTheme(this.startFrom)
            ? { ...getLogoLayoutForTheme(this.startFrom) }
            : { ...DEFAULT_LOGO_LAYOUT };

        this.logoDataUrl = '';
        this._pendingLogoFetch = getLogoUrlForTheme(this.startFrom);

        if (!keepIdentity) {
            this.displayName = '';
            this.themeId = '';
            this.idManual = false;
            this.nameTouched = false;
            this.idTouched = false;
            this.dirty = false;
            this.editMode = false;
        }
    }

    _recomputeDerived() {
        for (const key of this.autoKeys) {
            this.tokens[key] = deriveToken(key, this.tokens);
        }
        this.tokens['primary-rgb'] = rgbTripletFromPrimary(this.tokens['primary-green']);
    }

    _currentLogoUrl() {
        return this.logoDataUrl || this._pendingLogoFetch || getLogoUrlForTheme(this.startFrom);
    }

    _buildCss() {
        this._recomputeDerived();
        return buildDraftThemeCss(DRAFT_ID, {
            ...this.tokens,
            'primary-rgb': this.tokens['primary-rgb'],
        }, this.logoLayout);
    }

    _pushPreview() {
        const css = this._buildCss();
        const logo = this._currentLogoUrl();
        const layout = normalizeLogoLayout(this.logoLayout);
        if (isThemePreviewActive()) {
            updateThemePreview(css, logo, layout);
        } else {
            beginThemePreview(css, logo, layout);
        }
        try {
            window.chrome?.webview?.postMessage({
                type: 'THEME_PREVIEW_UPDATE',
                css,
                logoUrl: logo,
                layout,
            });
        } catch {  }
        this._updateReplicaLogos();
    }

    _contentTitle() {
        if (this.activeTab === 'colors') return t('theme_creator_tab_colors');
        if (this.activeTab === 'logo') return t('theme_creator_tab_logo');
        return t('theme_creator_tab_general');
    }

    _contentDesc() {
        return t('theme_creator_popout_hint');
    }

    _isFormValid() {
        const nameOk = !!this.displayName.trim();
        const idOk = isValidThemeId(this.themeId) && !isBuiltInUiTheme(this.themeId);
        return nameOk && idOk;
    }

    _nameError() {
        if (!this.nameTouched) return '';
        if (!this.displayName.trim()) return t('theme_creator_name_required');
        return '';
    }

    _idError() {
        if (!this.themeId) {
            return this.idTouched || this.nameTouched ? t('theme_creator_id_required') : '';
        }
        if (!isValidThemeId(this.themeId)) return t('theme_creator_id_invalid');
        if (isBuiltInUiTheme(this.themeId)) return t('theme_creator_id_reserved');
        return '';
    }

    _startFromOptionsHtml() {
        return getThemeOptionsForSettings({ uiTheme: this.startFrom })
            .map((th) => {
                const label = th.isUser
                    ? escapeHtml(th.displayName || th.id)
                    : escapeHtml(t(th.labelKey));
                return `<option value="${escapeAttr(th.id)}" ${th.id === this.startFrom ? 'selected' : ''}>${label}</option>`;
            })
            .join('');
    }

    _renderGeneral() {
        const nameErr = this._nameError();
        const idErr = this._idError();
        return `
            <div class="settings-card-inner theme-creator-general">
                <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 8px;">
                    <div class="tweak-label">${escapeHtml(t('theme_creator_name'))} <span class="theme-creator-required" aria-hidden="true">*</span></div>
                    <div class="path-input-group" style="width: 100%;">
                        <input type="text" id="theme-creator-name" maxlength="80"
                            class="${nameErr ? 'theme-creator-input-error' : ''}"
                            placeholder="${escapeAttr(t('theme_creator_name_placeholder'))}"
                            value="${escapeAttr(this.displayName)}">
                    </div>
                    <div class="theme-creator-field-hint ${nameErr ? 'theme-creator-field-hint--error' : ''}" id="theme-creator-name-hint">${escapeHtml(nameErr)}</div>
                </div>
                <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 8px;">
                    <div class="tweak-label">${escapeHtml(t('theme_creator_id'))} <span class="theme-creator-required" aria-hidden="true">*</span></div>
                    <div class="path-input-group" style="width: 100%;">
                        <input type="text" id="theme-creator-id" maxlength="48"
                            class="${idErr ? 'theme-creator-input-error' : ''}"
                            placeholder="${escapeAttr(t('theme_creator_id_placeholder'))}"
                            value="${escapeAttr(this.themeId)}"
                            ${this.editMode ? 'readonly' : ''}>
                    </div>
                    <div class="theme-creator-field-hint ${idErr ? 'theme-creator-field-hint--error' : this.editMode ? 'theme-creator-field-hint--muted' : ''}" id="theme-creator-id-hint">${escapeHtml(idErr || (this.editMode ? t('theme_creator_id_locked') : t('theme_creator_id_invalid')))}</div>
                </div>
            </div>
        `;
    }

    _renderColors() {
        const rows = COLOR_TOKENS.map(({ key, labelKey, derived }) => {
            const parsed = parseColor(this.tokens[key]) || { r: 128, g: 128, b: 128, a: 1 };
            const hex = toHex6(parsed);
            const isAuto = derived && this.autoKeys.has(key);
            const showOpacity = parsed.a < 0.999
                || key === 'text-muted'
                || key === 'border-color'
                || key === 'danger-red-soft';
            const value = this.tokens[key] || '';
            return `
                <div class="theme-creator-color-cell" data-token="${escapeAttr(key)}">
                    <div class="theme-creator-color-info">
                        <div class="tweak-label">${escapeHtml(t(labelKey))}</div>
                        ${derived ? `
                            <label class="theme-creator-auto" title="${escapeAttr(t('theme_creator_auto_hint'))}">
                                <span class="switch">
                                    <input type="checkbox" class="theme-creator-auto-toggle" data-token="${escapeAttr(key)}" ${isAuto ? 'checked' : ''}>
                                    <span class="slider"></span>
                                </span>
                                <span class="tweak-desc">${escapeHtml(t('theme_creator_auto'))}</span>
                            </label>
                        ` : ''}
                    </div>
                    <input type="color" class="theme-creator-color-swatch" data-token="${escapeAttr(key)}" value="${escapeAttr(hex)}" ${isAuto ? 'disabled' : ''}>
                    <input type="text" class="theme-creator-color-text" data-token="${escapeAttr(key)}" value="${escapeAttr(value)}" title="${escapeAttr(value)}" spellcheck="false" ${isAuto ? 'readonly' : ''}>
                    ${showOpacity ? `
                        <div class="theme-creator-opacity-row">
                            <span class="tweak-desc">${escapeHtml(t('theme_creator_opacity'))}</span>
                            <input type="range" min="0" max="100" step="1" class="tweak-range theme-creator-opacity" data-token="${escapeAttr(key)}" value="${Math.round(parsed.a * 100)}" ${isAuto ? 'disabled' : ''}>
                            <span class="theme-creator-opacity-val" data-token="${escapeAttr(key)}">${Math.round(parsed.a * 100)}%</span>
                        </div>
                    ` : ''}
                </div>
            `;
        }).join('');
        const modeOptions = RANDOM_MODES.map(({ id, labelKey, fallback }) =>
            `<option value="${id}" ${this.randomMode === id ? 'selected' : ''}>${escapeHtml(tOr(labelKey, fallback))}</option>`
        ).join('');
        return `
            <div class="theme-creator-colors-toolbar">
                <label class="tweak-desc" for="tc-random-mode">${escapeHtml(tOr('theme_creator_random_mode', 'Style'))}</label>
                <select id="tc-random-mode" class="settings-select">${modeOptions}</select>
                <button type="button" class="btn-secondary" id="tc-randomize">
                    <i data-lucide="dices"></i>
                    <span>${escapeHtml(tOr('theme_creator_randomize', 'Randomize'))}</span>
                </button>
            </div>
            <div class="theme-creator-colors-grid">${rows}</div>
        `;
    }

    _renderLogo() {
        const L = this.logoLayout;
        const logoSrc = escapeAttr(this._currentLogoUrl());
        return `
            <div class="theme-creator-logo-section">
                <div class="theme-creator-logo-actions">
                    <button type="button" class="btn-secondary" id="theme-creator-pick-logo">
                        <i data-lucide="image"></i>
                        <span>${escapeHtml(t('theme_creator_pick_logo'))}</span>
                    </button>
                    <input type="file" id="theme-creator-logo-file" accept="image/png,image/jpeg,image/webp,image/gif,.png,.jpg,.jpeg,.webp,.gif" hidden>
                    <p class="tweak-desc">${escapeHtml(t('theme_creator_logo_drag_hint'))}</p>
                    <div class="tweak-label">${escapeHtml(tOr('theme_creator_builtin_logos', 'Built-in logos'))}</div>
                    <div class="theme-creator-builtin-logos">${this._builtInLogosHtml()}</div>
                </div>

                <div class="theme-creator-sidebar-previews">
                    <div class="theme-creator-sidebar-preview-card">
                        <div class="theme-creator-preview-label">${escapeHtml(t('theme_creator_preview_expanded'))}</div>
                        <div class="sidebar theme-creator-sidebar-replica" data-collapsed="0">
                            <div class="sidebar-header">
                                <div class="logo-container">
                                    <img class="logo theme-studio-logo-editable theme-creator-replica-logo" src="${logoSrc}" alt="" draggable="false">
                                    <span class="app-name">F76 Manager</span>
                                </div>
                            </div>
                        </div>
                    </div>
                    <div class="theme-creator-sidebar-preview-card">
                        <div class="theme-creator-preview-label">${escapeHtml(t('theme_creator_preview_collapsed'))}</div>
                        <div class="sidebar collapsed theme-creator-sidebar-replica" data-collapsed="1">
                            <div class="sidebar-header">
                                <div class="logo-container">
                                    <img class="logo theme-studio-logo-editable theme-creator-replica-logo" src="${logoSrc}" alt="" draggable="false">
                                </div>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="theme-creator-sliders">
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t('theme_creator_logo_width'))} <em id="tc-w-val">${L.width}</em></div>
                        <input type="range" class="tweak-range" id="tc-width" min="16" max="120" value="${L.width}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t('theme_creator_logo_scale'))} <em id="tc-s-val">${L.scale.toFixed(2)}</em></div>
                        <input type="range" class="tweak-range" id="tc-scale" min="25" max="300" value="${Math.round(L.scale * 100)}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t('theme_creator_logo_offset_x'))} <em id="tc-ox-val">${L.offsetX}</em></div>
                        <input type="range" class="tweak-range" id="tc-offset-x" min="-40" max="40" value="${L.offsetX}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t('theme_creator_logo_offset_y'))} <em id="tc-oy-val">${L.offsetY}</em></div>
                        <input type="range" class="tweak-range" id="tc-offset-y" min="-40" max="40" value="${L.offsetY}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t('theme_creator_logo_collapsed_scale'))} <em id="tc-cs-val">${L.collapsedScale.toFixed(2)}</em></div>
                        <input type="range" class="tweak-range" id="tc-collapsed-scale" min="25" max="300" value="${Math.round(L.collapsedScale * 100)}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t('theme_creator_logo_collapsed_x'))} <em id="tc-cox-val">${L.collapsedOffsetX}</em></div>
                        <input type="range" class="tweak-range" id="tc-collapsed-x" min="-40" max="40" value="${L.collapsedOffsetX}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 8px;">
                        <div class="tweak-label">${escapeHtml(t('theme_creator_logo_fit'))}</div>
                        <select id="tc-object-fit" class="settings-select" style="width: 100%;">
                            ${OBJECT_FITS.map((f) => `<option value="${f}" ${L.objectFit === f ? 'selected' : ''}>${f}</option>`).join('')}
                        </select>
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t('theme_creator_logo_opacity'))} <em id="tc-op-val">${Math.round(L.opacity * 100)}%</em></div>
                        <input type="range" class="tweak-range" id="tc-opacity" min="0" max="100" value="${Math.round(L.opacity * 100)}">
                    </div>
                </div>
            </div>
        `;
    }

    _builtInLogosHtml() {
        const fallbacks = {
            fallout: 'Fallout',
            'vault-tec': 'Vault-Tec',
            'red-black': 'Red & Black',
            'black-white': 'Black & White',
            'app-icon': 'App icon',
        };
        const active = this.logoDataUrl ? '' : this._currentLogoUrl();
        return getBuiltInLogoOptions().map(({ id, labelKey, url }) => {
            const label = tOr(labelKey, fallbacks[id] || id);
            return `
                <button type="button" class="theme-creator-builtin-logo ${url === active ? 'active' : ''}"
                    data-logo-url="${escapeAttr(url)}" data-logo-theme="${escapeAttr(id)}"
                    title="${escapeAttr(label)}" aria-label="${escapeAttr(label)}">
                    <img src="${escapeAttr(url)}" alt="" draggable="false">
                </button>
            `;
        }).join('');
    }

    _syncBuiltInLogoActive() {
        const active = this.logoDataUrl ? '' : this._currentLogoUrl();
        this.root?.querySelectorAll('.theme-creator-builtin-logo').forEach((btn) => {
            btn.classList.toggle('active', btn.dataset.logoUrl === active);
        });
    }

    _useBuiltInLogo(url, themeId) {
        if (!url) return;
        this.logoDataUrl = '';
        this._pendingLogoFetch = url;
        if (themeId && isBuiltInUiTheme(themeId)) {
            const builtIn = getLogoLayoutForTheme(themeId);
            this.logoLayout = normalizeLogoLayout({
                ...this.logoLayout,
                objectFit: builtIn.objectFit,
                objectPosition: builtIn.objectPosition,
            });
        }
        this._setStatus('', '');
        this._syncBuiltInLogoActive();
        this._syncSlidersFromLayout();
        const fitSelect = this.root?.querySelector('#tc-object-fit');
        if (fitSelect) fitSelect.value = this.logoLayout.objectFit;
        this._markDirty();
        this._pushPreview();
    }

    _activePanelHtml() {
        if (this.activeTab === 'colors') return this._renderColors();
        if (this.activeTab === 'logo') return this._renderLogo();
        return this._renderGeneral();
    }

    _render() {
        if (!this.root) return;
        const canSave = this._isFormValid();
        const status = this._status;
        this.root.innerHTML = `
            <div class="settings-page theme-creator-page animate-fade">
                <div class="settings-header-bar theme-creator-header-bar">
                    <div class="theme-creator-header-title">
                        <i data-lucide="palette"></i>
                        <h1 class="settings-title">${escapeHtml(this.editMode ? t('theme_creator_edit_title') : t('theme_creator'))}</h1>
                    </div>
                    <div class="settings-lang-wrap">
                        <label for="theme-creator-start">${escapeHtml(t('theme_creator_start_from'))}</label>
                        <select class="settings-select" id="theme-creator-start">${this._startFromOptionsHtml()}</select>
                    </div>
                </div>

                <div class="settings-card-wide theme-creator-shell">
                    <div class="settings-inner">
                        <nav class="settings-sidebar">
                            <button type="button" class="settings-tab-btn ${this.activeTab === 'general' ? 'active' : ''}" data-tc-tab="general">
                                <i data-lucide="sliders-horizontal"></i>
                                <span>${escapeHtml(t('theme_creator_tab_general'))}</span>
                            </button>
                            <button type="button" class="settings-tab-btn ${this.activeTab === 'colors' ? 'active' : ''}" data-tc-tab="colors">
                                <i data-lucide="palette"></i>
                                <span>${escapeHtml(t('theme_creator_tab_colors'))}</span>
                            </button>
                            <button type="button" class="settings-tab-btn ${this.activeTab === 'logo' ? 'active' : ''}" data-tc-tab="logo">
                                <i data-lucide="image"></i>
                                <span>${escapeHtml(t('theme_creator_tab_logo'))}</span>
                            </button>
                        </nav>
                        <div class="settings-inner-content">
                            <header class="settings-content-header">
                                <h2 class="settings-content-title">${escapeHtml(this._contentTitle())}</h2>
                                <p class="settings-content-desc text-muted">${escapeHtml(this._contentDesc())}</p>
                            </header>
                            <div class="settings-panel-card theme-creator-panel-card">
                                ${this._activePanelHtml()}
                            </div>
                        </div>
                    </div>
                </div>

                <div class="settings-footer theme-creator-footer">
                    <div class="app-info">
                        <div class="theme-creator-status ${status.text ? `theme-creator-status--${status.type || 'info'}` : ''}" id="theme-creator-status" ${status.text ? '' : 'hidden'}>${escapeHtml(status.text || '')}</div>
                    </div>
                    <div class="theme-creator-actions">
                        <button type="button" class="btn-secondary" id="theme-creator-cancel">${escapeHtml(t('cancel'))}</button>
                        <button type="button" class="btn-secondary" id="theme-creator-export" ${canSave ? '' : 'disabled'}>${escapeHtml(t('theme_creator_export'))}</button>
                        <button type="button" class="btn primary" id="theme-creator-save" ${canSave ? '' : 'disabled'}>
                            <i data-lucide="save"></i>
                            <span>${escapeHtml(t('theme_creator_save'))}</span>
                        </button>
                    </div>
                </div>
            </div>
        `;
        if (this.activeTab === 'general' && !this._idError() && !this.editMode) {
            const hint = this.root.querySelector('#theme-creator-id-hint');
            if (hint) {
                hint.textContent = t('theme_creator_id_invalid');
                hint.className = 'theme-creator-field-hint theme-creator-field-hint--muted';
            }
        }
    }

    _setStatus(type, text) {
        this._status = { type: type || '', text: text || '' };
        const el = this.root?.querySelector('#theme-creator-status');
        if (!el) return;
        if (!text) {
            el.hidden = true;
            el.textContent = '';
            el.className = 'theme-creator-status';
            return;
        }
        el.hidden = false;
        el.className = `theme-creator-status theme-creator-status--${type || 'info'}`;
        el.textContent = text;
    }

    _updateSaveButtons() {
        const canSave = this._isFormValid();
        const save = this.root?.querySelector('#theme-creator-save');
        const exp = this.root?.querySelector('#theme-creator-export');
        if (save) save.disabled = !canSave;
        if (exp) exp.disabled = !canSave;
    }

    _refreshIdentityHints() {
        const nameHint = this.root?.querySelector('#theme-creator-name-hint');
        const idHint = this.root?.querySelector('#theme-creator-id-hint');
        const nameInput = this.root?.querySelector('#theme-creator-name');
        const idInput = this.root?.querySelector('#theme-creator-id');
        const nameErr = this._nameError();
        const idErr = this._idError();

        if (nameHint) {
            nameHint.textContent = nameErr;
            nameHint.className = `theme-creator-field-hint${nameErr ? ' theme-creator-field-hint--error' : ''}`;
        }
        if (nameInput) nameInput.classList.toggle('theme-creator-input-error', !!nameErr);

        if (idHint) {
            if (idErr) {
                idHint.textContent = idErr;
                idHint.className = 'theme-creator-field-hint theme-creator-field-hint--error';
            } else {
                idHint.textContent = t('theme_creator_id_invalid');
                idHint.className = 'theme-creator-field-hint theme-creator-field-hint--muted';
            }
        }
        if (idInput) idInput.classList.toggle('theme-creator-input-error', !!idErr);
        this._updateSaveButtons();
    }

    _markDirty() {
        this.dirty = true;
    }

    _syncSliderLabels() {
        const L = this.logoLayout;
        const set = (id, text) => {
            const el = this.root?.querySelector(`#${id}`);
            if (el) el.textContent = text;
        };
        set('tc-w-val', String(L.width));
        set('tc-s-val', L.scale.toFixed(2));
        set('tc-ox-val', String(L.offsetX));
        set('tc-oy-val', String(L.offsetY));
        set('tc-cs-val', L.collapsedScale.toFixed(2));
        set('tc-cox-val', String(L.collapsedOffsetX));
        set('tc-op-val', `${Math.round(L.opacity * 100)}%`);
    }

    _syncSlidersFromLayout() {
        const L = this.logoLayout;
        const setVal = (id, v) => {
            const el = this.root?.querySelector(`#${id}`);
            if (el) {
                el.value = String(v);
                syncRangeFill(el);
            }
        };
        setVal('tc-width', L.width);
        setVal('tc-scale', Math.round(L.scale * 100));
        setVal('tc-offset-x', L.offsetX);
        setVal('tc-offset-y', L.offsetY);
        setVal('tc-collapsed-scale', Math.round(L.collapsedScale * 100));
        setVal('tc-collapsed-x', L.collapsedOffsetX);
        setVal('tc-opacity', Math.round(L.opacity * 100));
        this._syncSliderLabels();
    }

    _refreshColorRow(key) {
        const cell = this.root?.querySelector(`.theme-creator-color-cell[data-token="${key}"]`);
        if (!cell) return;
        const parsed = parseColor(this.tokens[key]) || { r: 128, g: 128, b: 128, a: 1 };
        const swatch = cell.querySelector('.theme-creator-color-swatch');
        const text = cell.querySelector('.theme-creator-color-text');
        const opacity = cell.querySelector('.theme-creator-opacity');
        const autoToggle = cell.querySelector('.theme-creator-auto-toggle');
        const isAuto = this.autoKeys.has(key);
        if (swatch) {
            swatch.value = toHex6(parsed);
            swatch.disabled = isAuto;
        }
        if (text) {
            text.value = this.tokens[key] || '';
            text.title = text.value;
            text.readOnly = isAuto;
        }
        if (opacity) {
            opacity.value = String(Math.round(parsed.a * 100));
            opacity.disabled = isAuto;
            syncRangeFill(opacity);
        }
        const opacityVal = cell.querySelector('.theme-creator-opacity-val');
        if (opacityVal) opacityVal.textContent = `${Math.round(parsed.a * 100)}%`;
        if (autoToggle) autoToggle.checked = isAuto;
    }

    _randomizeColors() {
        const palette = generateRandomPalette(this.randomMode);
        Object.assign(this.tokens, palette);
        this.autoKeys = this.randomMode === 'chaos'
            ? new Set()
            : new Set(COLOR_TOKENS.filter((c) => c.derived).map((c) => c.key));
        this._recomputeDerived();
        for (const { key } of COLOR_TOKENS) this._refreshColorRow(key);
        this._markDirty();
        this._pushPreview();
    }

    _updateReplicaLogos() {
        const src = this._currentLogoUrl();
        this.root?.querySelectorAll('.theme-creator-replica-logo').forEach((img) => {
            setSharpLogo(img, src);
        });
    }

    _switchTab(tab) {
        if (this.activeTab === tab) return;
        this.activeTab = tab;
        this._render();
        this._bind();
        if (window.lucide) window.lucide.createIcons();
        this._pushPreview();
    }

    _bind() {
        if (this._abort) this._abort.abort();
        this._abort = new AbortController();
        const { signal } = this._abort;
        const root = this.root;
        if (!root) return;

        root.querySelectorAll('.tweak-range').forEach(syncRangeFill);
        root.addEventListener('input', (e) => {
            if (e.target?.classList?.contains('tweak-range')) syncRangeFill(e.target);
        }, { signal });

        root.querySelectorAll('[data-tc-tab]').forEach((btn) => {
            btn.addEventListener('click', () => this._switchTab(btn.getAttribute('data-tc-tab')), { signal });
        });

        root.querySelector('#theme-creator-cancel')?.addEventListener('click', () => this.close(), { signal });
        root.querySelector('#theme-creator-save')?.addEventListener('click', () => this._save('install'), { signal });
        root.querySelector('#theme-creator-export')?.addEventListener('click', () => this._save('export'), { signal });

        root.querySelector('#theme-creator-start')?.addEventListener('change', async (e) => {
            if (this.dirty) {
                const ok = await appConfirm({
                    title: t('theme_creator'),
                    message: t('theme_creator_discard'),
                    confirmLabel: t('ok'),
                    cancelLabel: t('cancel'),
                });
                if (!ok) {
                    e.target.value = this.startFrom;
                    return;
                }
            }
            this._seedFromTheme(e.target.value, { keepIdentity: true });
            this.dirty = true;
            this._render();
            this._bind();
            this._pushPreview();
            if (window.lucide) window.lucide.createIcons();
        }, { signal });

        root.querySelector('#theme-creator-name')?.addEventListener('input', (e) => {
            this.nameTouched = true;
            this.displayName = e.target.value;
            if (!this.idManual) {
                this.themeId = slugify(this.displayName);
                const idEl = root.querySelector('#theme-creator-id');
                if (idEl) idEl.value = this.themeId;
            }
            this._refreshIdentityHints();
            this._markDirty();
        }, { signal });

        root.querySelector('#theme-creator-name')?.addEventListener('blur', () => {
            this.nameTouched = true;
            this._refreshIdentityHints();
        }, { signal });

        root.querySelector('#theme-creator-id')?.addEventListener('input', (e) => {
            if (this.editMode) {
                e.target.value = this.themeId;
                return;
            }
            this.idManual = true;
            this.idTouched = true;
            this.themeId = e.target.value.trim().toLowerCase();
            this._refreshIdentityHints();
            this._markDirty();
        }, { signal });

        root.querySelector('#theme-creator-id')?.addEventListener('blur', () => {
            this.idTouched = true;
            this._refreshIdentityHints();
        }, { signal });

        root.querySelectorAll('.theme-creator-color-swatch').forEach((el) => {
            el.addEventListener('input', (e) => {
                const key = e.target.dataset.token;
                if (!key || this.autoKeys.has(key)) return;
                const prev = parseColor(this.tokens[key]) || { a: 1 };
                const next = parseColor(e.target.value);
                if (!next) return;
                next.a = prev.a;
                this.tokens[key] = toCssColor(next);
                if (key === 'primary-green') {
                    this.tokens['primary-rgb'] = rgbTripletFromPrimary(this.tokens[key]);
                }
                this._recomputeDerived();
                for (const k of this.autoKeys) this._refreshColorRow(k);
                const text = root.querySelector(`.theme-creator-color-text[data-token="${key}"]`);
                if (text) text.value = this.tokens[key];
                this._markDirty();
                this._pushPreview();
            }, { signal });
        });

        root.querySelectorAll('.theme-creator-color-text').forEach((el) => {
            el.addEventListener('change', (e) => {
                const key = e.target.dataset.token;
                if (!key || this.autoKeys.has(key)) return;
                const parsed = parseColor(e.target.value);
                if (!parsed) {
                    e.target.value = this.tokens[key] || '';
                    return;
                }
                this.tokens[key] = toCssColor(parsed);
                e.target.value = this.tokens[key];
                if (key === 'primary-green') {
                    this.tokens['primary-rgb'] = rgbTripletFromPrimary(this.tokens[key]);
                }
                this.autoKeys.delete(key);
                this._recomputeDerived();
                this._refreshColorRow(key);
                for (const k of this.autoKeys) this._refreshColorRow(k);
                this._markDirty();
                this._pushPreview();
            }, { signal });
        });

        root.querySelectorAll('.theme-creator-opacity').forEach((el) => {
            el.addEventListener('input', (e) => {
                const key = e.target.dataset.token;
                if (!key || this.autoKeys.has(key)) return;
                const parsed = parseColor(this.tokens[key]) || { r: 128, g: 128, b: 128, a: 1 };
                parsed.a = Number(e.target.value) / 100;
                this.tokens[key] = toCssColor(parsed);
                const text = root.querySelector(`.theme-creator-color-text[data-token="${key}"]`);
                if (text) {
                    text.value = this.tokens[key];
                    text.title = text.value;
                }
                const val = root.querySelector(`.theme-creator-opacity-val[data-token="${key}"]`);
                if (val) val.textContent = `${e.target.value}%`;
                this._markDirty();
                this._pushPreview();
            }, { signal });
        });

        root.querySelector('#tc-random-mode')?.addEventListener('change', (e) => {
            this.randomMode = e.target.value;
        }, { signal });
        root.querySelector('#tc-randomize')?.addEventListener('click', () => this._randomizeColors(), { signal });

        root.querySelectorAll('.theme-creator-auto-toggle').forEach((el) => {
            el.addEventListener('change', (e) => {
                const key = e.currentTarget.dataset.token;
                if (!key) return;
                if (e.currentTarget.checked) {
                    this.autoKeys.add(key);
                    this.tokens[key] = deriveToken(key, this.tokens);
                } else {
                    this.autoKeys.delete(key);
                }
                this._refreshColorRow(key);
                this._markDirty();
                this._pushPreview();
            }, { signal });
        });

        const pickBtn = root.querySelector('#theme-creator-pick-logo');
        const fileInput = root.querySelector('#theme-creator-logo-file');
        pickBtn?.addEventListener('click', () => fileInput?.click(), { signal });
        fileInput?.addEventListener('change', async (e) => {
            const file = e.target.files?.[0];
            if (!file) return;
            try {
                const { dataUrl } = await resizeLogoFile(file);
                this.logoDataUrl = dataUrl;
                this._pendingLogoFetch = '';
                this._syncBuiltInLogoActive();
                this._markDirty();
                this._pushPreview();
            } catch (err) {
                this._setStatus('error', err?.message || t('theme_creator_logo_failed'));
            }
        }, { signal });

        root.querySelectorAll('.theme-creator-builtin-logo').forEach((btn) => {
            btn.addEventListener('click', () => {
                this._useBuiltInLogo(btn.dataset.logoUrl, btn.dataset.logoTheme);
            }, { signal });
        });

        const bindRange = (id, apply) => {
            root.querySelector(id)?.addEventListener('input', (e) => {
                apply(Number(e.target.value));
                this.logoLayout = normalizeLogoLayout(this.logoLayout);
                this._syncSliderLabels();
                this._markDirty();
                this._pushPreview();
            }, { signal });
        };
        bindRange('#tc-width', (v) => { this.logoLayout.width = v; });
        bindRange('#tc-scale', (v) => { this.logoLayout.scale = v / 100; });
        bindRange('#tc-offset-x', (v) => { this.logoLayout.offsetX = v; });
        bindRange('#tc-offset-y', (v) => { this.logoLayout.offsetY = v; });
        bindRange('#tc-collapsed-scale', (v) => { this.logoLayout.collapsedScale = v / 100; });
        bindRange('#tc-collapsed-x', (v) => { this.logoLayout.collapsedOffsetX = v; });
        bindRange('#tc-opacity', (v) => { this.logoLayout.opacity = v / 100; });

        root.querySelector('#tc-object-fit')?.addEventListener('change', (e) => {
            this.logoLayout.objectFit = e.target.value;
            this._markDirty();
            this._pushPreview();
        }, { signal });

        this._bindReplicaLogoEdit(signal);
    }

    _bindReplicaLogoEdit(signal) {
        const logos = this.root?.querySelectorAll('.theme-creator-replica-logo') || [];
        logos.forEach((logo) => {
            const replica = logo.closest('.theme-creator-sidebar-replica');
            const collapsed = replica?.getAttribute('data-collapsed') === '1';

            logo.addEventListener('pointerdown', (e) => {
                if (e.button !== 0) return;
                e.preventDefault();
                e.stopPropagation();
                logo.setPointerCapture?.(e.pointerId);
                this._logoDrag = {
                    startX: e.clientX,
                    startY: e.clientY,
                    baseX: collapsed ? this.logoLayout.collapsedOffsetX : this.logoLayout.offsetX,
                    baseY: this.logoLayout.offsetY,
                    collapsed,
                };
                const onMove = (ev) => {
                    if (!this._logoDrag) return;
                    const dx = Math.round(ev.clientX - this._logoDrag.startX);
                    const dy = Math.round(ev.clientY - this._logoDrag.startY);
                    if (this._logoDrag.collapsed) {
                        this.logoLayout.collapsedOffsetX = Math.max(-40, Math.min(40, this._logoDrag.baseX + dx));
                    } else {
                        this.logoLayout.offsetX = Math.max(-40, Math.min(40, this._logoDrag.baseX + dx));
                        this.logoLayout.offsetY = Math.max(-40, Math.min(40, this._logoDrag.baseY - dy));
                    }
                    this.logoLayout = normalizeLogoLayout(this.logoLayout);
                    this._syncSlidersFromLayout();
                    this._markDirty();
                    this._pushPreview();
                };
                const onUp = () => {
                    this._logoDrag = null;
                    window.removeEventListener('pointermove', onMove);
                    window.removeEventListener('pointerup', onUp);
                };
                window.addEventListener('pointermove', onMove);
                window.addEventListener('pointerup', onUp);
            }, { signal });

            logo.addEventListener('wheel', (e) => {
                e.preventDefault();
                e.stopPropagation();
                const delta = e.deltaY > 0 ? -0.05 : 0.05;
                if (collapsed) {
                    this.logoLayout.collapsedScale = Math.max(0.25, Math.min(3, this.logoLayout.collapsedScale + delta));
                } else {
                    this.logoLayout.scale = Math.max(0.25, Math.min(3, this.logoLayout.scale + delta));
                }
                this.logoLayout = normalizeLogoLayout(this.logoLayout);
                this._syncSlidersFromLayout();
                this._markDirty();
                this._pushPreview();
            }, { signal, passive: false });
        });
    }

    _buildManifest() {
        this._recomputeDerived();
        const tokens = {};
        for (const { key } of COLOR_TOKENS) tokens[key] = this.tokens[key];
        tokens['primary-rgb'] = this.tokens['primary-rgb'] || rgbTripletFromPrimary(tokens['primary-green']);
        return {
            formatVersion: 2,
            id: this.themeId,
            displayName: this.displayName.trim(),
            tokens,
            logoLayout: normalizeLogoLayout(this.logoLayout),
        };
    }

    async _ensureLogoDataUrl() {
        if (this.logoDataUrl) return this.logoDataUrl;
        const url = this._pendingLogoFetch || getLogoUrlForTheme(this.startFrom);
        const res = await fetch(url);
        if (!res.ok) throw new Error(t('theme_creator_logo_failed'));
        const blob = await res.blob();
        const mime = blob.type || 'image/png';
        const ext = mime.includes('gif') ? 'gif' : mime.includes('webp') ? 'webp' : mime.includes('jpeg') || mime.includes('jpg') ? 'jpg' : 'png';
        const file = new File([blob], `logo.${ext}`, { type: mime });
        const { dataUrl } = await resizeLogoFile(file);
        this.logoDataUrl = dataUrl;
        return dataUrl;
    }

    async _save(mode) {
        this.nameTouched = true;
        this.idTouched = true;
        this._refreshIdentityHints();
        if (!this._isFormValid()) {
            this._setStatus('error', this._nameError() || this._idError() || t('theme_creator_id_invalid'));
            this._switchTab('general');
            return;
        }
        this._setStatus('info', t('theme_creator_saving'));
        try {
            const logoDataUrl = await this._ensureLogoDataUrl();
            const manifest = this._buildManifest();
            window.chrome?.webview?.postMessage({
                type: 'SAVE_THEME_PACKAGE',
                mode,
                manifest,
                logoDataUrl,
            });
        } catch (err) {
            this._setStatus('error', err?.message || t('theme_creator_save_failed'));
        }
    }

    handleSaveResult(data) {
        if (!data) return;
        if (data.ok) {
            this.dirty = false;
            if (data.mode === 'export') {
                this._setStatus('success', t('theme_creator_export_success'));
            } else {
                this._setStatus('success', t('theme_creator_save_success', data.displayName || data.themeId || ''));
            }
        } else {
            this._setStatus('error', data.error || t('theme_creator_save_failed'));
        }
    }

    async close() {
        if (this._closing) return;
        if (this.dirty) {
            const ok = await appConfirm({
                title: t('theme_creator'),
                message: t('theme_creator_discard'),
                confirmLabel: t('ok'),
                cancelLabel: t('cancel'),
            });
            if (!ok) return;
        }
        this._closing = true;
        if (this._abort) {
            this._abort.abort();
            this._abort = null;
        }
        try {
            window.chrome?.webview?.postMessage({ type: 'CLOSE_THEME_CREATOR' });
        } catch {  }
    }
}

export function mountThemeCreator(root) {
    const creator = new ThemeCreator(root);
    creator.mount();
    return creator;
}
