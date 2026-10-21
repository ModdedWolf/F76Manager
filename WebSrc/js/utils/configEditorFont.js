export const CONFIG_EDITOR_FONT_MIN = 9;
export const CONFIG_EDITOR_FONT_MAX = 28;
export const CONFIG_EDITOR_FONT_DEFAULT_SIZE = 12;
export const CONFIG_EDITOR_FONT_DEFAULT_FAMILY = 'default';

export const CONFIG_EDITOR_FONT_FAMILIES = [
    { id: 'default', label: null, css: "'JetBrains Mono', Consolas, monospace" },
    { id: 'consolas', label: 'Consolas', css: 'Consolas, monospace' },
    { id: 'cascadia-mono', label: 'Cascadia Mono', css: "'Cascadia Mono', Consolas, monospace" },
    { id: 'cascadia-code', label: 'Cascadia Code', css: "'Cascadia Code', Consolas, monospace" },
    { id: 'courier-new', label: 'Courier New', css: "'Courier New', monospace" },
    { id: 'lucida-console', label: 'Lucida Console', css: "'Lucida Console', monospace" },
    { id: 'inter', label: 'Inter', css: "'Inter', sans-serif" },
    { id: 'roboto-condensed', label: 'Roboto Condensed', css: "'Roboto Condensed', sans-serif" },
];

export function clampConfigEditorFontSize(value) {
    const n = Math.round(Number(value));
    if (!Number.isFinite(n)) return CONFIG_EDITOR_FONT_DEFAULT_SIZE;
    return Math.min(CONFIG_EDITOR_FONT_MAX, Math.max(CONFIG_EDITOR_FONT_MIN, n));
}

export function normalizeConfigEditorFontFamily(value) {
    const id = String(value ?? '').trim().toLowerCase();
    return CONFIG_EDITOR_FONT_FAMILIES.some(f => f.id === id) ? id : CONFIG_EDITOR_FONT_DEFAULT_FAMILY;
}

export function configEditorFontFamilyLabel(family) {
    if (family.label) return family.label;
    const t = typeof window._t === 'function' ? window._t('config_editor_font_default') : '';
    return t && t !== 'config_editor_font_default' ? t : 'Default (JetBrains Mono)';
}

export function getConfigEditorFont(ms = window.app?.realData?.managerSettings) {
    return {
        size: clampConfigEditorFontSize(ms?.configEditorFontSize ?? CONFIG_EDITOR_FONT_DEFAULT_SIZE),
        family: normalizeConfigEditorFontFamily(ms?.configEditorFontFamily),
    };
}

export function applyConfigEditorFont(el, prefs = getConfigEditorFont()) {
    if (!el) return;
    const family = CONFIG_EDITOR_FONT_FAMILIES.find(f => f.id === normalizeConfigEditorFontFamily(prefs.family))
        || CONFIG_EDITOR_FONT_FAMILIES[0];
    el.style.setProperty('--cfg-editor-font-size', `${clampConfigEditorFontSize(prefs.size)}px`);
    el.style.setProperty('--cfg-editor-font-family', family.css);
}

let _saveTimer = null;

export function saveConfigEditorFont(prefs) {
    const size = clampConfigEditorFontSize(prefs.size);
    const family = normalizeConfigEditorFontFamily(prefs.family);
    const ms = window.app?.realData?.managerSettings;
    if (ms) {
        ms.configEditorFontSize = size;
        ms.configEditorFontFamily = family;
    }
    if (_saveTimer) clearTimeout(_saveTimer);
    _saveTimer = setTimeout(() => {
        _saveTimer = null;
        window.chrome?.webview?.postMessage({ type: 'SAVE_CONFIG_EDITOR_FONT', fontSize: size, fontFamily: family });
    }, 400);
}
