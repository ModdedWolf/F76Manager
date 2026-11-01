import { escapeAttr, escapeHtml } from '../utils/htmlSafe.js';
import {
    CONFIG_EDITOR_FONT_DEFAULT_SIZE,
    CONFIG_EDITOR_FONT_DEFAULT_FAMILY,
    getConfigEditorFont,
    applyConfigEditorFont,
    saveConfigEditorFont
} from '../utils/configEditorFont.js';
import { mountConfigFontPopover } from '../utils/configEditorFontPopover.js';

let _requestSeq = Math.floor(Math.random() * 1e9);

function textEditErrorText(error) {
    if (error === 'missing_edit') return window._t('bundle_preview_edit_missing');
    if (error === 'missing_save') return window._t('bundle_preview_save_missing');
    if (error === 'outside_workspace') return window._t('bundle_preview_edit_outside');
    if (!error) return '';
    return window._t('bundle_preview_edit_failed', error);
}

export function renderBa2TextView({ idPrefix, name, meta, text, truncated, showPopout }) {
    return `
        <div class="ba2-text-view" data-ba2-text-view>
            <div class="ba2-preview-header ba2-text-header">
                <div class="ba2-text-title">
                    <div class="ba2-preview-name">${escapeHtml(name)}</div>
                    ${meta ? `<div class="ba2-preview-meta">${escapeHtml(meta)}</div>` : ''}
                </div>
                <div class="ba2-text-tools">
                    <span class="ba2-text-dirty" data-text-dirty hidden title="${escapeAttr(window._t('bundle_preview_unsaved'))}"></span>
                    <button type="button" class="ba2-preview-tool ba2-preview-tool--primary" data-text-action="save" disabled title="${escapeAttr(window._t('config_editor_save_tooltip'))}">
                        <i data-lucide="save"></i><span>${escapeHtml(window._t('bundle_preview_save'))}</span>
                    </button>
                    <button type="button" class="ba2-preview-tool" data-text-action="discard" hidden>
                        <span>${escapeHtml(window._t('bundle_preview_discard'))}</span>
                    </button>
                    <div class="ba2-text-font-wrap">
                        <button type="button" class="ba2-preview-tool ba2-preview-tool--icon" id="${idPrefix}-btn" aria-haspopup="dialog" aria-expanded="false" title="${escapeAttr(window._t('config_editor_font'))}">
                            <i data-lucide="type"></i>
                        </button>
                    </div>
                    ${showPopout ? `
                    <button type="button" class="ba2-preview-tool ba2-preview-tool--icon" data-preview-popout title="${escapeAttr(window._t('bundle_preview_popout'))}">
                        <i data-lucide="external-link"></i>
                    </button>` : ''}
                </div>
            </div>
            ${truncated ? `<div class="ba2-preview-note" data-text-truncated>${escapeHtml(window._t('preview_truncated'))}</div>` : ''}
            <div class="ba2-text-body">
                <textarea class="ba2-text-editor" data-text-edit spellcheck="false" wrap="off" readonly>${escapeHtml(text || '')}</textarea>
            </div>
        </div>
    `;
}

export function mountBa2TextView(root, {
    idPrefix,
    target,
    post,
    confirm,
    onDirtyChange,
    onStatus,
    onPopout,
    initialEdit = null
}) {
    const view = root?.querySelector('[data-ba2-text-view]');
    if (!view) return null;
    const editEl = view.querySelector('[data-text-edit]');
    const dirtyEl = view.querySelector('[data-text-dirty]');
    const btnSave = view.querySelector('[data-text-action="save"]');
    const btnDiscard = view.querySelector('[data-text-action="discard"]');
    const truncatedNote = view.querySelector('[data-text-truncated]');

    let loading = false;
    let saving = false;
    let ready = false;
    let tooLarge = false;
    let original = '';
    let encoding = 'utf8';
    let eol = 'lf';
    let loadRequestId = 0;
    let saveRequestId = 0;
    let dirty = false;
    let font = getConfigEditorFont();

    applyConfigEditorFont(view, font);

    const syncChrome = () => {
        if (dirtyEl) dirtyEl.hidden = !dirty;
        if (btnSave) btnSave.disabled = !ready || tooLarge || saving || !dirty;
        if (btnDiscard) btnDiscard.hidden = !dirty;
        if (editEl) editEl.readOnly = !ready || tooLarge || loading;
    };

    const setDirty = (next) => {
        if (dirty === next) return;
        dirty = next;
        syncChrome();
        onDirtyChange?.(dirty);
    };

    const loadFullText = () => {
        if (loading || !target || ready || tooLarge) return;
        loading = true;
        syncChrome();
        loadRequestId = ++_requestSeq;
        post({
            type: 'BUNDLE_LOAD_TEXT_EDIT',
            source: target.source,
            mode: target.mode,
            path: target.path,
            requestId: loadRequestId
        });
    };

    const save = () => {
        if (!ready || tooLarge || saving || !dirty || !target || !editEl) return;
        saving = true;
        syncChrome();
        saveRequestId = ++_requestSeq;
        post({
            type: 'BUNDLE_SAVE_TEXT_EDIT',
            archive: target.archive,
            path: target.entryPath,
            text: editEl.value,
            encoding,
            eol,
            requestId: saveRequestId
        });
    };

    const discard = async () => {
        if (!dirty || !editEl) return;
        if (confirm) {
            const ok = await confirm(window._t('bundle_preview_unsaved_confirm'));
            if (!ok) return;
        }
        editEl.value = original;
        setDirty(false);
    };

    const setFont = (next) => {
        const prev = font;
        font = getConfigEditorFont({ configEditorFontSize: next.size, configEditorFontFamily: next.family });
        applyConfigEditorFont(view, font);
        if (prev.size !== font.size || prev.family !== font.family) saveConfigEditorFont(font);
        const label = document.getElementById(`${idPrefix}-size-value`);
        if (label) label.textContent = `${font.size}px`;
    };
    const stepFont = (delta) => {
        const size = delta === 0 ? CONFIG_EDITOR_FONT_DEFAULT_SIZE : font.size + delta;
        setFont({ ...font, size });
    };

    const popover = mountConfigFontPopover(view.querySelector(`#${idPrefix}-btn`), {
        idPrefix,
        getFont: () => font,
        onStep: stepFont,
        onFamily: (family) => setFont({ ...font, family }),
        onReset: () => setFont({ size: CONFIG_EDITOR_FONT_DEFAULT_SIZE, family: CONFIG_EDITOR_FONT_DEFAULT_FAMILY })
    });

    const onWheel = (e) => {
        if (!e.ctrlKey) return;
        e.preventDefault();
        if (e.deltaY) stepFont(e.deltaY < 0 ? 1 : -1);
    };
    const onKey = (e) => {
        if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
        if (!view.contains(document.activeElement)) return;
        const k = e.key;
        if (k === 's' || k === 'S') {
            e.preventDefault();
            e.stopPropagation();
            save();
            return;
        }
        let delta = null;
        if (k === '=' || k === '+') delta = 1;
        else if (k === '-' || k === '_') delta = -1;
        else if (k === '0') delta = 0;
        if (delta === null) return;
        e.preventDefault();
        e.stopPropagation();
        stepFont(delta);
    };
    const onInput = () => setDirty(editEl.value !== original);
    const onEditorKey = (e) => {
        if (e.key !== 'Tab' || e.ctrlKey || e.altKey || e.metaKey) return;
        e.preventDefault();
        const start = editEl.selectionStart;
        const end = editEl.selectionEnd;
        editEl.setRangeText('\t', start, end, 'end');
        onInput();
    };

    const body = view.querySelector('.ba2-text-body');
    body?.addEventListener('wheel', onWheel, { passive: false });
    document.addEventListener('keydown', onKey, true);
    editEl?.addEventListener('input', onInput);
    editEl?.addEventListener('keydown', onEditorKey);
    btnSave?.addEventListener('click', save);
    btnDiscard?.addEventListener('click', () => { void discard(); });
    view.querySelector('[data-preview-popout]')?.addEventListener('click', () => onPopout?.());

    if (initialEdit && editEl) {
        original = initialEdit.original;
        encoding = initialEdit.encoding;
        eol = initialEdit.eol;
        ready = true;
        editEl.value = initialEdit.value;
        editEl.readOnly = false;
        if (truncatedNote) truncatedNote.hidden = true;
        syncChrome();
        onInput();
        editEl.scrollTop = initialEdit.scrollTop || 0;
        const selStart = initialEdit.selectionStart ?? 0;
        const selEnd = initialEdit.selectionEnd ?? selStart;
        try { editEl.setSelectionRange(selStart, selEnd); } catch (_) { }
    } else {
        syncChrome();
        loadFullText();
    }

    return {
        isDirty: () => dirty,
        isEditing: () => ready,
        getEditState: () => (ready && editEl ? {
            original,
            value: editEl.value,
            encoding,
            eol,
            scrollTop: editEl.scrollTop,
            selectionStart: editEl.selectionStart,
            selectionEnd: editEl.selectionEnd
        } : null),
        applyFont(prefs) {
            font = getConfigEditorFont({ configEditorFontSize: prefs.size, configEditorFontFamily: prefs.family });
            applyConfigEditorFont(view, font);
            const label = document.getElementById(`${idPrefix}-size-value`);
            if (label) label.textContent = `${font.size}px`;
        },
        handleMessage(data) {
            if (data?.type === 'BUNDLE_TEXT_EDIT_CONTENT' && loadRequestId && data.requestId === loadRequestId) {
                loadRequestId = 0;
                loading = false;
                if (data.error) {
                    if (data.error === 'too_large') {
                        tooLarge = true;
                        if (truncatedNote) {
                            truncatedNote.hidden = false;
                            truncatedNote.textContent = window._t('bundle_preview_edit_too_large');
                        }
                        syncChrome();
                        return true;
                    }
                    syncChrome();
                    onStatus?.('error', textEditErrorText(data.error));
                    return true;
                }
                original = data.text || '';
                encoding = data.encoding || 'utf8';
                eol = data.eol || 'lf';
                ready = true;
                if (editEl) {
                    editEl.value = original;
                    editEl.scrollTop = 0;
                    try { editEl.setSelectionRange(0, 0); } catch (_) { }
                }
                if (truncatedNote) truncatedNote.hidden = true;
                setDirty(false);
                syncChrome();
                return true;
            }
            if ((data?.type === 'BUNDLE_TEXT_EDIT_SAVED' || data?.type === 'BUNDLE_TEXT_EDIT_SAVE_FAILED')
                && saveRequestId && data.requestId === saveRequestId) {
                saveRequestId = 0;
                saving = false;
                if (data.type === 'BUNDLE_TEXT_EDIT_SAVE_FAILED') {
                    syncChrome();
                    onStatus?.('error', textEditErrorText(data.error));
                    return true;
                }
                original = editEl.value;
                setDirty(false);
                syncChrome();
                return true;
            }
            return false;
        },
        destroy() {
            popover.destroy();
            body?.removeEventListener('wheel', onWheel);
            document.removeEventListener('keydown', onKey, true);
        }
    };
}
