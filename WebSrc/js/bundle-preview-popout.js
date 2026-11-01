import { translator } from './Translations.js';
import { installAppConfirmGlobals } from './utils/appConfirm.js';
import { renderBa2TextView, mountBa2TextView } from './components/Ba2TextEditor.js';
import { BundleRenderer } from './components/BundleRenderer.js';
import { mountMeshViewer } from './utils/meshViewer.js';
import { escapeAttr, escapeHtml } from './utils/htmlSafe.js';

installAppConfirmGlobals();

window._t = (key, ...args) => translator.t(key, ...args);
window.translator = translator;
window.app = { realData: { managerSettings: {} } };

const post = (msg) => window.chrome?.webview?.postMessage(msg);

let _appliedThemeId = null;
let target = null;
let previewRequestId = 0;
let textView = null;
let meshViewer = null;
let wireframe = false;
let meshAnimKeyAbort = null;
let modelVisible = false;
let modelLoading = false;
let modelRequest = false;
let lastMeshResult = null;

function showToast(type, text) {
    const el = document.getElementById('preview-popout-toast');
    if (!el || !text) return;
    el.textContent = text;
    el.className = `logs-popout-toast ${type || 'info'}`;
    el.hidden = false;
    requestAnimationFrame(() => el.classList.add('visible'));
    clearTimeout(showToast._timer);
    showToast._timer = setTimeout(() => {
        el.classList.remove('visible');
        setTimeout(() => { el.hidden = true; }, 200);
    }, 3200);
}

function confirmDiscard(message = window._t('bundle_preview_unsaved_confirm')) {
    return window.appConfirm({
        title: window._t('bundle_preview_unsaved'),
        message,
        okText: window._t('bundle_preview_discard'),
        cancelText: window._t('cancel'),
        danger: true
    });
}

async function fetchLocale(lang) {
    const res = await fetch(`https://f76manager.app/locales/${lang}.json`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
}

async function loadLocale() {
    try {
        translator.loadedLanguages['en-US'] = await fetchLocale('en-US');
        translator.currentLanguage = 'en-US';
        let lang = new URLSearchParams(window.location.search || '').get('lang') || '';
        if (!lang || lang === 'auto') lang = translator.detectLanguage() || 'en-US';
        if (lang !== 'en-US') {
            try {
                translator.loadedLanguages[lang] = await fetchLocale(lang);
                translator.currentLanguage = lang;
            } catch (e) {
                console.warn('[PREVIEW-POPOUT] selected locale load failed, using en-US', e);
            }
        }
    } catch (e) {
        console.warn('[PREVIEW-POPOUT] locale load failed', e);
    }
}

function applyThemeSync(payload) {
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

function setFontPrefs(size, family) {
    const ms = window.app.realData.managerSettings;
    if (size != null) ms.configEditorFontSize = size;
    if (family != null) ms.configEditorFontFamily = family;
}

function disposeViews() {
    textView?.destroy();
    textView = null;
    meshViewer?.dispose();
    meshViewer = null;
    if (meshAnimKeyAbort) {
        meshAnimKeyAbort.abort();
        meshAnimKeyAbort = null;
    }
}

function setBody(html) {
    disposeViews();
    const root = document.getElementById('preview-popout-root');
    root.innerHTML = `<div class="preview-popout">${html}</div>`;
    if (window.lucide) {
        try { window.lucide.createIcons({ nodes: [root] }); } catch (_) { }
    }
    return root;
}

function headerHtml(meta) {
    return `
        <div class="ba2-preview-header">
            <div class="ba2-preview-name">${escapeHtml(target?.name || '')}</div>
            ${meta ? `<div class="ba2-preview-meta">${escapeHtml(meta)}</div>` : ''}
        </div>`;
}

function mountMeshPreview(result) {
    meshViewer?.dispose?.();
    meshViewer = null;
    const manager = {
        previewPath: target?.name,
        _meshModelVisible: modelVisible,
        _meshModelLoading: modelLoading
    };
    const root = setBody(BundleRenderer.renderMeshPreview(manager, result));
    const stage = root.querySelector('#ba2-mesh-stage');
    const mesh = result.mesh;
    if (stage && mesh) {
        const showModel = modelVisible && Array.isArray(mesh.shapes) && mesh.shapes.length > 0;
        const display = showModel
            ? { ...mesh, bones: null }
            : { ...mesh, shapes: [], textures: [], vertexCount: 0, triangleCount: 0 };
        meshViewer = mountMeshViewer(stage, display);
    }
    const wireBtn = root.querySelector('#btn-mesh-wire');
    if (wireBtn) {
        wireBtn.classList.toggle('ba2-preview-mesh-tool--on', wireframe);
        if (wireframe) meshViewer?.setWireframe(true);
        wireBtn.onclick = () => {
            wireframe = !wireframe;
            wireBtn.classList.toggle('ba2-preview-mesh-tool--on', wireframe);
            meshViewer?.setWireframe(wireframe);
        };
    }
    const modelBtn = root.querySelector('#btn-mesh-model');
    if (modelBtn) {
        modelBtn.onclick = () => {
            if (modelLoading) return;
            const hasModel = Array.isArray(lastMeshResult?.mesh?.shapes) && lastMeshResult.mesh.shapes.length > 0;
            if (modelVisible && hasModel) {
                modelVisible = false;
                mountMeshPreview(lastMeshResult);
                return;
            }
            if (hasModel) {
                modelVisible = true;
                mountMeshPreview(lastMeshResult);
                return;
            }
            modelLoading = true;
            modelBtn.disabled = true;
            modelBtn.textContent = window._t('preview_hkx_model_loading');
            requestPreview({ model: true, quiet: true });
        };
    }
    root.querySelector('#btn-mesh-reset')?.addEventListener('click', () => meshViewer?.reset());
    bindMeshAnimControls(root, meshViewer);
    if (window.lucide) {
        try { window.lucide.createIcons({ nodes: [root] }); } catch (_) { }
    }
}

function requestPreview({ model = false, quiet = false } = {}) {
    if (!target) return;
    previewRequestId += 1;
    modelRequest = Boolean(model);
    if (!quiet) {
        modelVisible = false;
        modelLoading = false;
        lastMeshResult = null;
        setBody(`${headerHtml('')}<div class="ba2-preview-empty">${escapeHtml(window._t('preview_loading'))}</div>`);
    }
    const payload = {
        type: 'BUNDLE_PREVIEW_FILE',
        source: target.source,
        mode: target.mode,
        path: target.path,
        requestId: previewRequestId
    };
    if (modelRequest) payload.model = true;
    post(payload);
}

function renderPreview(result) {
    if (result.kind === 'text') {
        const root = setBody(renderBa2TextView({
            idPrefix: 'popout-text-font',
            name: target.name,
            meta: BundleRenderer.formatBa2Size(result.size),
            text: result.text,
            truncated: result.truncated,
            showPopout: false
        }));
        textView = mountBa2TextView(root, {
            idPrefix: 'popout-text-font',
            target,
            post,
            confirm: (message) => confirmDiscard(message),
            onDirtyChange: (dirty) => post({ type: 'PREVIEW_POPOUT_DIRTY', dirty }),
            onStatus: showToast
        });
        return;
    }
    if (result.kind === 'mesh') {
        lastMeshResult = result;
        if (modelRequest) {
            modelLoading = false;
            modelRequest = false;
            modelVisible = Array.isArray(result.mesh?.shapes) && result.mesh.shapes.length > 0;
        }
        mountMeshPreview(result);
        return;
    }
    if (result.kind === 'image' && result.dataUrl) {
        const meta = [];
        if (result.width && result.height) meta.push(`${result.width}×${result.height}`);
        if (result.format) meta.push(result.format);
        if (result.size) meta.push(BundleRenderer.formatBa2Size(result.size));
        setBody(`${headerHtml(meta.join(' · '))}<div class="ba2-preview-stage"><img alt="" src="${escapeAttr(result.dataUrl)}"></div>`);
        return;
    }
    const message = result.kind === 'none'
        ? BundleRenderer.previewNoneMessage(result.error)
        : window._t('bundle_preview_popout_only_main');
    const meta = result.format ? result.format : '';
    setBody(`${headerHtml(meta)}<div class="ba2-preview-empty">${escapeHtml(message)}</div>`);
}

function bindMeshAnimControls(root, viewer) {
    if (!viewer?.hasAnimation || !root) return;
    const playBtn = root.querySelector('#ba2-mesh-anim-play');
    const playIcon = root.querySelector('#ba2-mesh-anim-icon-play');
    const pauseIcon = root.querySelector('#ba2-mesh-anim-icon-pause');
    const seek = root.querySelector('#ba2-mesh-anim-seek');
    const timeLabel = root.querySelector('#ba2-mesh-anim-time');
    const loopBtn = root.querySelector('#ba2-mesh-anim-loop');
    const duration = viewer.getDuration();
    const formatTime = (sec) => {
        const total = Math.max(0, Math.floor(sec || 0));
        return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
    };
    const renderPlay = () => {
        const playing = viewer.isPlaying();
        if (playIcon) playIcon.hidden = playing;
        if (pauseIcon) pauseIcon.hidden = !playing;
        if (playBtn) {
            const label = window._t(playing ? 'preview_audio_pause' : 'preview_audio_play');
            playBtn.title = window._t('preview_shortcut_space', label);
            playBtn.setAttribute('aria-label', label);
        }
    };
    const renderProgress = () => {
        const t = viewer.getTime();
        const pct = duration > 0 ? Math.min(100, Math.max(0, (t / duration) * 100)) : 0;
        if (seek) {
            seek.value = String(Math.round(pct * 10));
            seek.style.setProperty('--value', `${pct}%`);
        }
        if (timeLabel) timeLabel.textContent = `${formatTime(t)} / ${formatTime(duration)}`;
    };
    let progressRaf = 0;
    const startProgress = () => {
        if (progressRaf) return;
        const tick = () => {
            renderProgress();
            progressRaf = viewer.isPlaying() ? requestAnimationFrame(tick) : 0;
        };
        progressRaf = requestAnimationFrame(tick);
    };
    if (playBtn) {
        playBtn.onclick = () => {
            viewer.toggle();
            renderPlay();
            if (viewer.isPlaying()) startProgress();
        };
    }
    if (loopBtn) {
        loopBtn.onclick = () => {
            const next = !viewer.isLooping();
            viewer.setLoop(next);
            loopBtn.classList.toggle('ba2-audio-icon-btn--on', next);
            loopBtn.setAttribute('aria-pressed', next ? 'true' : 'false');
        };
    }
    if (seek) {
        seek.oninput = () => {
            viewer.seek((Number(seek.value) / 1000) * duration);
            renderProgress();
        };
    }
    if (meshAnimKeyAbort) meshAnimKeyAbort.abort();
    meshAnimKeyAbort = new AbortController();
    document.addEventListener('keydown', (event) => {
        if (event.code !== 'Space') return;
        if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
        const el = event.target;
        if (el && typeof el.closest === 'function'
            && el.closest('input, textarea, select, [contenteditable], button')) return;
        if (!root.querySelector('#ba2-mesh-anim')) return;
        event.preventDefault();
        viewer.toggle();
        renderPlay();
        if (viewer.isPlaying()) startProgress();
    }, { signal: meshAnimKeyAbort.signal });
    renderPlay();
    renderProgress();
    if (viewer.isPlaying()) startProgress();
}

async function showTarget(data) {
    if (textView?.isDirty()) {
        if (!await confirmDiscard()) return;
        post({ type: 'PREVIEW_POPOUT_DIRTY', dirty: false });
    }
    setFontPrefs(data.fontSize, data.fontFamily);
    target = data.target;
    document.title = `F76 Manager — ${target?.name || ''}`;
    requestPreview();
}

function handleTextSaved(data) {
    const handled = textView?.handleMessage(data);
    if (data.type !== 'BUNDLE_TEXT_EDIT_SAVED' || !target) return;
    const samePath = String(data.path || '').toLowerCase() === String(target.entryPath || '').replace(/\\/g, '/').replace(/^\/+/, '').toLowerCase();
    if (!samePath || data.archive !== target.archive) return;
    const sepIndex = Math.max(data.source.lastIndexOf('\\'), data.source.lastIndexOf('/'));
    target = { ...target, source: data.source.slice(0, sepIndex), mode: 'folder', path: data.source.slice(sepIndex + 1) };
    if (handled) {
        post({ type: 'PREVIEW_POPOUT_DIRTY', dirty: false });
        showToast('success', window._t('bundle_preview_saved', target.name));
    } else if (!textView?.isDirty()) {
        requestPreview();
    }
}

async function handleCloseRequest() {
    if (textView?.isDirty()) {
        if (!await confirmDiscard()) return;
        post({ type: 'PREVIEW_POPOUT_DIRTY', dirty: false });
    }
    post({ type: 'PREVIEW_POPOUT_CLOSE_CONFIRMED' });
}

async function boot() {
    await loadLocale();
    window.chrome?.webview?.addEventListener('message', (event) => {
        const data = event.data;
        if (!data || typeof data !== 'object') return;
        switch (data.type) {
            case 'THEME_SYNC':
                applyThemeSync(data);
                break;
            case 'PREVIEW_POPOUT_SHOW':
                void showTarget(data);
                break;
            case 'PREVIEW_POPOUT_CLOSE_REQUEST':
                void handleCloseRequest();
                break;
            case 'BUNDLE_FILE_PREVIEW':
                if (Number(data.requestId) === previewRequestId) renderPreview(data);
                break;
            case 'BUNDLE_PREVIEW_SOURCE_CHANGED':
                if (!target || textView?.isDirty()) break;
                if (String(target.mode || '').toLowerCase() !== 'folder') break;
                {
                    const changed = String(data.path || '').replace(/\\/g, '/').replace(/^\/+/, '').toLowerCase();
                    const current = String(target.path || '').replace(/\\/g, '/').replace(/^\/+/, '').toLowerCase();
                    if (changed && changed === current) requestPreview();
                }
                break;
            case 'BUNDLE_TEXT_EDIT_CONTENT':
                textView?.handleMessage(data);
                break;
            case 'BUNDLE_TEXT_EDIT_SAVED':
            case 'BUNDLE_TEXT_EDIT_SAVE_FAILED':
                handleTextSaved(data);
                break;
            case 'CONFIG_EDITOR_FONT_CHANGED':
                setFontPrefs(data.fontSize, data.fontFamily);
                textView?.applyFont({ size: data.fontSize, family: data.fontFamily });
                break;
        }
    });
    post({ type: 'PREVIEW_POPOUT_READY' });
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
} else {
    boot();
}
