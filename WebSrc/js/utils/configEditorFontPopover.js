import { escapeAttr, escapeHtml } from './htmlSafe.js';
import {
    CONFIG_EDITOR_FONT_FAMILIES,
    configEditorFontFamilyLabel
} from './configEditorFont.js';

const t = (key, fallback) => {
    const v = typeof window._t === 'function' ? window._t(key) : '';
    return v && v !== key ? v : fallback;
};

export function mountConfigFontPopover(btn, {
    idPrefix,
    getFont,
    onStep,
    onFamily,
    onReset,
    isSuspended = () => false
}) {
    if (!btn) return { close() { }, destroy() { } };
    let pop = null;

    const close = () => {
        if (!pop) return;
        pop.remove();
        pop = null;
        btn.setAttribute('aria-expanded', 'false');
        document.removeEventListener('mousedown', onOutside, true);
        document.removeEventListener('keydown', onKey, true);
    };
    const onOutside = (e) => {
        if (!pop) return;
        if (pop.contains(e.target) || btn.contains(e.target)) return;
        if (e.target?.closest?.('.themed-select-menu')) return;
        close();
    };
    const onKey = (e) => {
        if (e.key !== 'Escape' || !pop) return;
        if (isSuspended()) return;
        if (document.querySelector('.themed-select-menu')) return;
        const hint = pop.querySelector(`#${idPrefix}-hint`);
        if (hint && !hint.hidden) {
            e.preventDefault();
            e.stopPropagation();
            hint.hidden = true;
            const infoBtn = pop.querySelector(`#${idPrefix}-info`);
            infoBtn?.setAttribute('aria-expanded', 'false');
            infoBtn?.classList.remove('active');
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        close();
        btn.focus();
    };
    const open = () => {
        const cur = getFont();
        pop = document.createElement('div');
        pop.className = 'config-font-popover';
        pop.setAttribute('role', 'dialog');
        pop.setAttribute('aria-label', t('config_editor_font', 'Editor font'));
        const hintText = t('config_editor_font_hint', 'Tip: Ctrl + Mouse Wheel in the editor changes the size. Ctrl + 0 resets it.');
        pop.innerHTML = `
            <div class="config-font-head">
                <h3>${escapeHtml(t('config_editor_font', 'Editor font'))}</h3>
                <div class="config-font-info-wrap">
                    <button type="button" class="config-font-info" id="${idPrefix}-info" aria-expanded="false" aria-controls="${idPrefix}-hint" aria-label="${escapeAttr(hintText)}">
                        <i data-lucide="info"></i>
                    </button>
                    <div class="config-font-hint" id="${idPrefix}-hint" role="tooltip" hidden>${escapeHtml(hintText)}</div>
                </div>
            </div>
            <span class="config-font-label">${escapeHtml(t('config_editor_font_size_label', 'Font size'))}</span>
            <div class="config-font-stepper">
                <button type="button" data-font-step="-1" aria-label="-"><i data-lucide="minus"></i></button>
                <span id="${idPrefix}-size-value" class="config-font-value">${cur.size}px</span>
                <button type="button" data-font-step="1" aria-label="+"><i data-lucide="plus"></i></button>
            </div>
            <label class="config-font-label" for="${idPrefix}-family">${escapeHtml(t('config_editor_font_family_label', 'Font'))}</label>
            <select id="${idPrefix}-family" class="tweak-select">
                ${CONFIG_EDITOR_FONT_FAMILIES.map(f => `<option value="${escapeAttr(f.id)}" ${f.id === cur.family ? 'selected' : ''}>${escapeHtml(configEditorFontFamilyLabel(f))}</option>`).join('')}
            </select>
            <div class="config-font-actions">
                <button type="button" class="btn-popup secondary" id="${idPrefix}-reset">${escapeHtml(t('config_editor_font_reset', 'Reset'))}</button>
            </div>`;
        btn.parentElement.appendChild(pop);
        const br = btn.getBoundingClientRect();
        const pr = btn.parentElement.getBoundingClientRect();
        pop.style.top = `${br.bottom - pr.top + 6}px`;
        pop.style.right = `${Math.max(0, pr.right - br.right)}px`;
        try { window.lucide?.createIcons({ nodes: [pop] }); } catch (_) { }
        const infoBtn = pop.querySelector(`#${idPrefix}-info`);
        const hint = pop.querySelector(`#${idPrefix}-hint`);
        const setHintOpen = (openHint) => {
            if (!hint || !infoBtn) return;
            hint.hidden = !openHint;
            infoBtn.setAttribute('aria-expanded', String(openHint));
            infoBtn.classList.toggle('active', openHint);
        };
        infoBtn?.addEventListener('click', (e) => {
            e.stopPropagation();
            setHintOpen(!!hint?.hidden);
        });
        pop.addEventListener('click', (e) => {
            if (!hint || hint.hidden) return;
            if (e.target.closest(`#${idPrefix}-info, #${idPrefix}-hint`)) return;
            setHintOpen(false);
        });
        pop.querySelectorAll('[data-font-step]').forEach(b => {
            b.addEventListener('click', () => onStep(Number(b.dataset.fontStep)));
        });
        pop.querySelector(`#${idPrefix}-family`)?.addEventListener('change', (e) => onFamily(e.target.value));
        pop.querySelector(`#${idPrefix}-reset`)?.addEventListener('click', () => onReset());
        btn.setAttribute('aria-expanded', 'true');
        document.addEventListener('mousedown', onOutside, true);
        document.addEventListener('keydown', onKey, true);
    };
    const onClick = () => {
        if (pop) close();
        else open();
    };
    btn.addEventListener('click', onClick);

    return {
        close,
        destroy() {
            close();
            btn.removeEventListener('click', onClick);
        }
    };
}
