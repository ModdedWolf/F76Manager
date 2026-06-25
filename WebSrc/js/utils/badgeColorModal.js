import { escapeAttr, escapeHtml } from './htmlSafe.js';
import {
    applyStylesheet as applyModTypeBadgeStylesheet,
    CONFIGS_TAB_TYPE_KEYS,
    getEffectiveTheme,
    loadMap as loadModTypeBadgeMap,
    MODS_TAB_TYPE_KEYS,
    removeTypeFromStorage,
    sanitizeExt as sanitizeModTypeExt,
    saveMap as saveModTypeBadgeMap,
    themeForStorage as themeModTypeForStorage,
} from './modTypeBadgeTheme.js';

const OVERLAY_ID = 'badge-color-overlay';

export function isConfigListEntry(mod) {
    const original = String(mod?.originalName || '').toLowerCase();
    return original.endsWith('.ini') || original.endsWith('.json') || original.endsWith('.txt');
}

export function filterModsTabEntries(mods) {
    return (mods || []).filter((m) => {
        if (!m || isConfigListEntry(m)) return false;
        if (m.isBundle && m.status === 'disabled') return false;
        return true;
    });
}

export function filterConfigsTabEntries(mods) {
    return (mods || []).filter((m) => m && isConfigListEntry(m));
}

export function collectModTypeKeys(mods) {
    const keys = new Set();
    for (const mod of mods || []) {
        const key = sanitizeModTypeExt(mod?.type);
        if (key) keys.add(key);
    }
    return Array.from(keys).sort((a, b) => a.localeCompare(b));
}

export function collectModsTabTypeKeys(mods) {
    const keys = new Set(MODS_TAB_TYPE_KEYS);
    for (const key of collectModTypeKeys(filterModsTabEntries(mods))) keys.add(key);
    return Array.from(keys).sort((a, b) => a.localeCompare(b));
}

export function collectConfigsTabTypeKeys(mods) {
    const keys = new Set(CONFIGS_TAB_TYPE_KEYS);
    for (const key of collectModTypeKeys(filterConfigsTabEntries(mods))) keys.add(key);
    return Array.from(keys).sort((a, b) => a.localeCompare(b));
}

function hintForScope(scope, typeUpper) {
    if (scope === 'configs') {
        return window._t?.('rename_mod_type_hint_configs_tab', typeUpper)
            ?? `Applies to every config with type ${typeUpper} in the Configs tab.`;
    }
    return window._t?.('rename_mod_type_hint_mods_tab', typeUpper)
        ?? `Applies to every mod with type ${typeUpper} in the Mods tab.`;
}

function hideBadgeColorModal() {
    document.getElementById(OVERLAY_ID)?.remove();
}

function persistTheme(typeKey, theme) {
    const map = loadModTypeBadgeMap();
    const stored = themeModTypeForStorage(typeKey, theme);
    if (stored) map[typeKey] = stored;
    else delete map[typeKey];
    saveModTypeBadgeMap(map);
    applyModTypeBadgeStylesheet();
}

function styleBadgeEl(el, key, theme) {
    if (!el || !key) return;
    const t = theme || getEffectiveTheme(key);
    el.textContent = key.toUpperCase();
    el.className = `badge badge-${key} badge-color-preview`;
    el.style.background = t.bg;
    el.style.color = t.fg;
    el.style.boxShadow = t.glow ? `0 0 10px 2px ${t.fg}73` : 'none';
}

function wrapTypeIndex(index, length) {
    return ((index % length) + length) % length;
}

function updateCarouselStrip(overlay, typeKeys, currentIndex) {
    const len = typeKeys.length;
    const track = overlay.querySelector('.badge-color-track');
    const prevSlot = overlay.querySelector('#badge-color-prev-badge');
    const nextSlot = overlay.querySelector('#badge-color-next-badge');
    const prevBadge = prevSlot?.querySelector('.badge-color-preview');
    const nextBadge = nextSlot?.querySelector('.badge-color-preview');

    if (len <= 1) {
        track?.classList.add('badge-color-track-single');
        prevSlot?.setAttribute('hidden', '');
        nextSlot?.setAttribute('hidden', '');
        return;
    }

    track?.classList.remove('badge-color-track-single');
    prevSlot?.removeAttribute('hidden');
    nextSlot?.removeAttribute('hidden');

    const prevKey = typeKeys[wrapTypeIndex(currentIndex - 1, len)];
    const nextKey = typeKeys[wrapTypeIndex(currentIndex + 1, len)];
    const ariaForType = (key) => window._t?.('badge_color_select_type', key.toUpperCase())
        ?? `Edit ${key.toUpperCase()} badge colors`;
    styleBadgeEl(prevBadge, prevKey);
    styleBadgeEl(nextBadge, nextKey);
    if (prevSlot) {
        prevSlot.dataset.typeKey = prevKey;
        prevSlot.setAttribute('aria-label', ariaForType(prevKey));
    }
    if (nextSlot) {
        nextSlot.dataset.typeKey = nextKey;
        nextSlot.setAttribute('aria-label', ariaForType(nextKey));
    }
}

function bindTypeControls(overlay, getTypeKey, scope, onCarouselRefresh) {
    const typeBgInput = overlay.querySelector('#badge-color-type-bg');
    const typeFgInput = overlay.querySelector('#badge-color-type-fg');
    const typeGlowInput = overlay.querySelector('#badge-color-type-glow');
    const typeResetBtn = overlay.querySelector('#badge-color-type-reset');
    const previewBadge = overlay.querySelector('#badge-color-preview');
    const hintEl = overlay.querySelector('.badge-color-type-hint');

    const getThemeFromInputs = () => ({
        bg: typeBgInput?.value || '#000000',
        fg: typeFgInput?.value || '#ffffff',
        glow: !!(typeGlowInput && typeGlowInput.checked),
    });

    const syncUiToType = (key) => {
        const eff = getEffectiveTheme(key);
        if (typeBgInput) typeBgInput.value = eff.bg;
        if (typeFgInput) typeFgInput.value = eff.fg;
        if (typeGlowInput) typeGlowInput.checked = eff.glow;
        if (previewBadge) styleBadgeEl(previewBadge, key, eff);
        if (hintEl) {
            hintEl.textContent = hintForScope(scope, key.toUpperCase());
        }
        onCarouselRefresh?.();
    };

    const onThemeInput = () => {
        const key = getTypeKey();
        const theme = getThemeFromInputs();
        if (previewBadge) styleBadgeEl(previewBadge, key, theme);
        persistTheme(key, theme);
    };

    syncUiToType(getTypeKey());

    if (typeBgInput) typeBgInput.oninput = onThemeInput;
    if (typeFgInput) typeFgInput.oninput = onThemeInput;
    if (typeGlowInput) typeGlowInput.onchange = onThemeInput;

    if (typeResetBtn) {
        typeResetBtn.onclick = () => {
            const key = getTypeKey();
            removeTypeFromStorage(key);
            applyModTypeBadgeStylesheet();
            syncUiToType(key);
            persistTheme(key, getThemeFromInputs());
        };
    }

    return { syncUiToType };
}

export function openBadgeColorModal(options = {}) {
    hideBadgeColorModal();

    const scope = options.scope === 'configs' ? 'configs' : 'mods';

    let typeKeys = (options.typeKeys || [])
        .map((k) => sanitizeModTypeExt(k))
        .filter(Boolean);
    typeKeys = [...new Set(typeKeys)].sort((a, b) => a.localeCompare(b));

    if (typeKeys.length === 0) {
        return false;
    }

    let activeTypeKey = sanitizeModTypeExt(options.initialTypeKey) || typeKeys[0];
    if (!typeKeys.includes(activeTypeKey)) activeTypeKey = typeKeys[0];

    let currentIndex = typeKeys.indexOf(activeTypeKey);
    const typeUpper = activeTypeKey.toUpperCase();
    const hasMultipleTypes = typeKeys.length > 1;
    const prevKey = hasMultipleTypes ? typeKeys[wrapTypeIndex(currentIndex - 1, typeKeys.length)] : '';
    const nextKey = hasMultipleTypes ? typeKeys[wrapTypeIndex(currentIndex + 1, typeKeys.length)] : '';
    const title = window._t?.('edit_badge_color') ?? 'Edit Badge Color';
    const doneLabel = window._t?.('done') ?? window._t?.('ok') ?? 'Done';
    const prevLabel = window._t?.('badge_color_prev_type') ?? 'Previous type';
    const nextLabel = window._t?.('badge_color_next_type') ?? 'Next type';
    const ariaForType = (key) => window._t?.('badge_color_select_type', key.toUpperCase())
        ?? `Edit ${key.toUpperCase()} badge colors`;

    const overlay = document.createElement('div');
    overlay.id = OVERLAY_ID;
    overlay.className = 'modal-overlay active rename-mod-overlay';

    overlay.innerHTML = `
        <div class="custom-modal polished badge-color-modal" role="dialog" aria-modal="true" aria-label="${escapeAttr(title)}">
            <div class="rename-mod-header badge-color-header">
                <h3 class="badge-color-title">${escapeHtml(title)}</h3>
                <button type="button" class="close-status" id="badge-color-close-top" aria-label="${escapeAttr(window._t?.('cancel') ?? 'Cancel')}">&times;</button>
            </div>
            <div class="modal-body rename-mod-body">
                <div class="badge-color-carousel" role="group" aria-label="${escapeAttr(window._t?.('rename_mod_type_heading') ?? 'TYPE badge')}">
                    <button type="button" class="badge-color-nav badge-color-nav-prev" id="badge-color-prev" aria-label="${escapeAttr(prevLabel)}"${hasMultipleTypes ? '' : ' hidden'}>
                        <i data-lucide="chevron-left"></i>
                    </button>
                    <div class="badge-color-track${hasMultipleTypes ? '' : ' badge-color-track-single'}">
                        <button type="button" class="badge-color-slot badge-color-slot-side" id="badge-color-prev-badge" aria-label="${escapeAttr(ariaForType(prevKey))}"${hasMultipleTypes ? '' : ' hidden'}>
                            <span class="badge badge-${escapeAttr(prevKey)} badge-color-preview">${escapeHtml(prevKey.toUpperCase())}</span>
                        </button>
                        <div class="badge-color-slot badge-color-slot-center">
                            <span class="badge badge-${escapeAttr(activeTypeKey)} badge-color-preview" id="badge-color-preview">${escapeHtml(typeUpper)}</span>
                        </div>
                        <button type="button" class="badge-color-slot badge-color-slot-side" id="badge-color-next-badge" aria-label="${escapeAttr(ariaForType(nextKey))}"${hasMultipleTypes ? '' : ' hidden'}>
                            <span class="badge badge-${escapeAttr(nextKey)} badge-color-preview">${escapeHtml(nextKey.toUpperCase())}</span>
                        </button>
                    </div>
                    <button type="button" class="badge-color-nav badge-color-nav-next" id="badge-color-next" aria-label="${escapeAttr(nextLabel)}"${hasMultipleTypes ? '' : ' hidden'}>
                        <i data-lucide="chevron-right"></i>
                    </button>
                </div>
                ${hasMultipleTypes ? `<div class="badge-color-position" id="badge-color-position" aria-live="polite">${currentIndex + 1} / ${typeKeys.length}</div>` : ''}
                <p class="rename-mod-type-hint badge-color-type-hint">${escapeHtml(hintForScope(scope, typeUpper))}</p>
                <div class="rename-mod-type-section badge-color-section" role="group">
                    <div class="rename-mod-type-controls">
                        <div class="rename-mod-type-row">
                            <label class="rename-mod-type-label" for="badge-color-type-bg">${window._t?.('rename_mod_type_bg') ?? 'Background'}</label>
                            <input type="color" id="badge-color-type-bg" class="rename-mod-type-color" />
                        </div>
                        <div class="rename-mod-type-row">
                            <label class="rename-mod-type-label" for="badge-color-type-fg">${window._t?.('rename_mod_type_fg') ?? 'Text'}</label>
                            <input type="color" id="badge-color-type-fg" class="rename-mod-type-color" />
                        </div>
                        <label class="rename-mod-type-glow">
                            <input type="checkbox" id="badge-color-type-glow" />
                            <span>${escapeHtml(window._t?.('rename_mod_type_glow') ?? 'Glow')}</span>
                        </label>
                    </div>
                    <button type="button" class="btn-popup secondary rename-mod-type-reset" id="badge-color-type-reset">${escapeHtml(window._t?.('rename_mod_type_reset') ?? 'Reset type colors')}</button>
                </div>
            </div>
            <div class="modal-footer rename-mod-footer">
                <button type="button" class="btn-popup primary" id="badge-color-done">${escapeHtml(doneLabel)}</button>
            </div>
        </div>
    `;

    document.body.appendChild(overlay);
    if (window.lucide) window.lucide.createIcons();

    let currentTypeKey = activeTypeKey;

    const updateCarouselChrome = () => {
        const posEl = overlay.querySelector('#badge-color-position');
        if (posEl) posEl.textContent = `${currentIndex + 1} / ${typeKeys.length}`;
    };

    const refreshCarousel = () => {
        updateCarouselStrip(overlay, typeKeys, currentIndex);
        updateCarouselChrome();
    };

    const { syncUiToType } = bindTypeControls(overlay, () => currentTypeKey, scope, refreshCarousel);

    refreshCarousel();

    const setTypeIndex = (nextIndex) => {
        if (typeKeys.length <= 1) return;
        currentIndex = wrapTypeIndex(nextIndex, typeKeys.length);
        currentTypeKey = typeKeys[currentIndex];
        syncUiToType(currentTypeKey);
    };

    const close = () => hideBadgeColorModal();

    overlay.querySelector('#badge-color-done')?.addEventListener('click', close);
    overlay.querySelector('#badge-color-close-top')?.addEventListener('click', close);
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) close();
    });

    overlay.querySelector('#badge-color-prev')?.addEventListener('click', () => setTypeIndex(currentIndex - 1));
    overlay.querySelector('#badge-color-next')?.addEventListener('click', () => setTypeIndex(currentIndex + 1));
    overlay.querySelector('#badge-color-prev-badge')?.addEventListener('click', () => setTypeIndex(currentIndex - 1));
    overlay.querySelector('#badge-color-next-badge')?.addEventListener('click', () => setTypeIndex(currentIndex + 1));

    const onKey = (e) => {
        if (e.key === 'Escape') {
            e.preventDefault();
            close();
            document.removeEventListener('keydown', onKey);
            return;
        }

        const ae = document.activeElement;
        const inFormControl = ae && (
            ae.tagName === 'INPUT' ||
            ae.tagName === 'TEXTAREA' ||
            ae.tagName === 'SELECT' ||
            ae.isContentEditable
        );
        if (inFormControl) return;

        if (e.key === 'ArrowLeft') {
            e.preventDefault();
            setTypeIndex(currentIndex - 1);
        } else if (e.key === 'ArrowRight') {
            e.preventDefault();
            setTypeIndex(currentIndex + 1);
        }
    };
    document.addEventListener('keydown', onKey);
    return true;
}

export function openTabBadgeColorModal(options = {}) {
    const scope = options.scope === 'configs' ? 'configs' : 'mods';
    const mods = options.mods || [];
    const typeKeys = scope === 'configs'
        ? collectConfigsTabTypeKeys(mods)
        : collectModsTabTypeKeys(mods);

    return openBadgeColorModal({ typeKeys, scope });
}
