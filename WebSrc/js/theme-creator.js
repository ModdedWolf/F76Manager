import { translator } from './Translations.js';
import { installAppConfirmGlobals } from './utils/appConfirm.js';
import { registerUserThemesFromHost } from './utils/themeManager.js';
import { mountThemeCreator } from './components/ThemeCreator.js';
import { installThemedSelectMenus } from './utils/themedSelectMenu.js';

window._t = (key, ...args) => translator.t(key, ...args);
window.translator = translator;
installAppConfirmGlobals();
installThemedSelectMenus();

function resolvePopoutLanguage() {
    try {
        const params = new URLSearchParams(window.location.search || '');
        let lang = (params.get('lang') || '').trim();
        if (!lang || lang === 'auto') {
            lang = translator.detectLanguage();
        }
        return lang || 'en-US';
    } catch {
        return 'en-US';
    }
}

function resolveEditThemeId() {
    try {
        const params = new URLSearchParams(window.location.search || '');
        return (params.get('edit') || '').trim();
    } catch {
        return '';
    }
}

async function fetchLocale(lang) {
    const res = await fetch(`https://f76manager.app/locales/${lang}.json`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
}

async function loadLocale() {
    try {
        const en = await fetchLocale('en-US');
        translator.loadedLanguages['en-US'] = en;
        translator.currentLanguage = 'en-US';

        const lang = resolvePopoutLanguage();
        if (lang !== 'en-US') {
            try {
                const json = await fetchLocale(lang);
                translator.loadedLanguages[lang] = json;
                translator.currentLanguage = lang;
            } catch (e) {
                console.warn('[THEME-CREATOR] selected locale load failed, using en-US', e);
            }
        }
        try {
            document.title = window._t('theme_creator_window_title') || 'F76 Manager — Theme Creator';
        } catch {  }
    } catch (e) {
        console.warn('[THEME-CREATOR] locale load failed', e);
    }
}

function registerBootThemes() {
    try {
        const ids = Array.isArray(window.__F76_USER_THEME_IDS) ? window.__F76_USER_THEME_IDS : [];
        const cssMap = window.__F76_USER_THEME_CSS || {};
        registerUserThemesFromHost(ids.map((id) => ({
            id,
            displayName: id,
            logo: `user-theme-logo/${id}`,
            css: cssMap[id] || '',
        })));
    } catch (e) {
        console.warn('[THEME-CREATOR] register themes failed', e);
    }
}

function requestEditData(id) {
    if (!id) return;
    window.chrome?.webview?.postMessage({ type: 'GET_THEME_EDIT_DATA', id });
}

async function boot() {
    await loadLocale();
    registerBootThemes();
    const root = document.getElementById('theme-creator-root');
    const creator = mountThemeCreator(root);
    window.__themeCreator = creator;

    const initialEdit = resolveEditThemeId();
    if (initialEdit) requestEditData(initialEdit);

    window.chrome?.webview?.addEventListener('message', async (event) => {
        const data = event.data;
        if (!data || typeof data !== 'object') return;
        if (data.type === 'THEME_SAVE_RESULT') {
            creator.handleSaveResult(data);
        } else if (data.type === 'THEME_EDIT_DATA') {
            if (data.ok) {
                creator.loadForEdit(data);
            } else {
                creator._setStatus('error', data.error || window._t('theme_creator_save_failed'));
            }
        } else if (data.type === 'THEME_CREATOR_LOAD') {
            const id = (data.editThemeId || '').trim();
            if (!id) return;
            if (creator.dirty) {
                const ok = await window.appConfirm?.({
                    title: window._t('theme_creator'),
                    message: window._t('theme_creator_discard'),
                    confirmLabel: window._t('ok'),
                    cancelLabel: window._t('cancel'),
                });
                if (!ok) return;
            }
            requestEditData(id);
        }
    });

    if (window.lucide) {
        try { window.lucide.createIcons(); } catch {  }
    }
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
} else {
    boot();
}
