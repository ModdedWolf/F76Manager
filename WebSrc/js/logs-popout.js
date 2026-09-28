import { Logs } from './components/Logs.js';
import { translator } from './Translations.js';

window._t = (key, ...args) => translator.t(key, ...args);
window.translator = translator;

const logs = new Logs({ isPopout: true });
window.__logsPopoutData = { logs: { activity: [], errors: [], errorCount: 0 } };

let _appliedThemeId = null;

function showToast(status) {
    const el = document.getElementById('logs-popout-toast');
    if (!el || !status) return;
    el.textContent = status.text || '';
    el.className = `logs-popout-toast ${(status.type || 'info')}`;
    el.hidden = false;
    requestAnimationFrame(() => el.classList.add('visible'));
    clearTimeout(showToast._timer);
    showToast._timer = setTimeout(() => {
        el.classList.remove('visible');
        setTimeout(() => { el.hidden = true; }, 200);
    }, 3200);
}

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
                console.warn('[LOGS-POPOUT] selected locale load failed, using en-US', e);
            }
        }
    } catch (e) {
        console.warn('[LOGS-POPOUT] locale load failed', e);
    }
}

function applyThemeSync(payload) {
    if (!payload || typeof payload !== 'object') return;
    const themeId = (payload.themeId || '').trim() || 'fallout';
    const css = typeof payload.css === 'string' ? payload.css : '';

    if (typeof payload.uiAnimations === 'boolean') {
        document.body.classList.toggle('no-animations', payload.uiAnimations === false);
    }

    if (_appliedThemeId && _appliedThemeId !== themeId) {
        document.getElementById(`user-theme-${_appliedThemeId}`)?.remove();
    }

    document.documentElement.dataset.theme = themeId;
    _appliedThemeId = themeId;

    const styleId = `user-theme-${themeId}`;
    let styleEl = document.getElementById(styleId);
    if (css) {
        if (!styleEl) {
            styleEl = document.createElement('style');
            styleEl.id = styleId;
        }
        document.head.appendChild(styleEl);
        styleEl.textContent = css;
    } else if (styleEl) {
        styleEl.remove();
    }
}

function renderShell() {
    const root = document.getElementById('logs-popout-root');
    if (!root) return;
    root.innerHTML = logs.render(window.__logsPopoutData);
    logs.onMount();
}

function applyLogsData(payload) {
    window.__logsPopoutData = { logs: payload || { activity: [], errors: [], errorCount: 0 } };
    if (!logs.refreshView(window.__logsPopoutData, true)) {
        renderShell();
    }
    if (window.lucide) window.lucide.createIcons();
}

function requestLogs() {
    window.chrome?.webview?.postMessage({ type: 'GET_LOGS' });
}

function requestThemeSync() {
    window.chrome?.webview?.postMessage({ type: 'GET_THEME_SYNC' });
}

async function boot() {
    await loadLocale();
    renderShell();
    requestThemeSync();
    requestLogs();
    setInterval(requestLogs, 1500);

    window.chrome?.webview?.addEventListener('message', (event) => {
        const data = event.data;
        if (!data || typeof data !== 'object') return;
        if (data.type === 'THEME_SYNC') {
            applyThemeSync(data);
        } else if (data.type === 'LOGS_DATA') {
            applyLogsData(data.logs);
        } else if (data.type === 'STATUS' && data.status) {
            showToast(data.status);
            requestLogs();
        }
    });
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
} else {
    boot();
}
