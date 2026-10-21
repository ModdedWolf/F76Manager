import { ModsRenderer, buildModUpdateButtonHtml, buildModUnverifiedButtonHtml } from './ModsRenderer.js';
import { ModsDndHandler } from './ModsDndHandler.js';
import { escapeAttr, escapeHtml as escapeHtmlSafe } from '../utils/htmlSafe.js';
import { applyStylesheet as applyModTypeBadgeStylesheet } from '../utils/modTypeBadgeTheme.js';
import { openTabBadgeColorModal } from '../utils/badgeColorModal.js';
import { JsonTreeEditor } from '../utils/jsonTreeEditor.js';
import * as jsonc from '../utils/jsonc.js';
import {
    CONFIG_EDITOR_FONT_FAMILIES,
    CONFIG_EDITOR_FONT_DEFAULT_SIZE,
    CONFIG_EDITOR_FONT_DEFAULT_FAMILY,
    clampConfigEditorFontSize,
    configEditorFontFamilyLabel,
    getConfigEditorFont,
    applyConfigEditorFont,
    saveConfigEditorFont
} from '../utils/configEditorFont.js';

let _modsGlobalEventsAttached = false;
let _deleteModInFlight = false;
let _deleteModInFlightTimer = null;
let _pendingDeleteName = null;

function clearDeleteModInFlight() {
    _deleteModInFlight = false;
    _pendingDeleteName = null;
    if (_deleteModInFlightTimer) {
        clearTimeout(_deleteModInFlightTimer);
        _deleteModInFlightTimer = null;
    }
}

async function deleteModWithConfirmation(name) {
    if (!name || _deleteModInFlight) return;
    _deleteModInFlight = true;
    _pendingDeleteName = name;
    try {
        const confirmEnabled = !!(window.app?.realData?.managerSettings?.confirmBeforeDeleteMod);
        if (confirmEnabled) {
            const ok = await window.appConfirm({
                title: 'Confirmation',
                message: window._t('delete_mod_confirm', name),
                okText: window._t('delete'),
                cancelText: window._t('cancel'),
                danger: true
            });
            if (!ok) {
                clearDeleteModInFlight();
                return;
            }
        }
        console.log(`[MODS] Deleting mod: ${name}`);
        window.chrome?.webview?.postMessage?.({ type: 'DELETE_MOD', name });
        if (_deleteModInFlightTimer) clearTimeout(_deleteModInFlightTimer);
        _deleteModInFlightTimer = setTimeout(() => {
            clearDeleteModInFlight();
        }, 8000);
    } catch (err) {
        console.error('[MODS] Delete failed to start:', err);
        clearDeleteModInFlight();
    }
}

export class ModsManager {
    constructor(modGroupsManager) {
        this.modGroupsManager = modGroupsManager;
        this._modsActionsMenuAbort = null;
        localStorage.removeItem('mods_sort_field');
        localStorage.removeItem('mods_sort_order');
        this.lastSearchTerm = '';
        this.currentPreset = this.modGroupsManager?.getActiveName?.() || 'Default';
        this.expandedLooseKeys = new Set();
        this.renameModalKeyHandler = null;
        this._pendingModDetailsByFileKey = new Map();
        this._modUpdatesByName = new Map();
        this._modUpdateCheckInFlight = false;
        this._bulkUpdateInProgress = false;
        this._teardownConfigEditorLayout = null;
        this.initGlobalHandlers();
    }

    _syncConfigEditorBackdropPad(overlay) {
        const root = overlay || document.getElementById('rename-mod-overlay');
        if (!root) return;
        const ta = root.querySelector('#mod-config-editor');
        const bd = root.querySelector('#mod-config-highlight-backdrop');
        if (!ta || !bd) return;
        const sb = Math.max(0, ta.offsetWidth - ta.clientWidth);
        bd.style.paddingRight = `${16 + sb}px`;
        bd.scrollTop = ta.scrollTop;
    }

    _renderConfigHighlight(text, isJson) {
        const raw = String(text ?? '');
        const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
        if (!isJson) {
            return esc(raw).replace(/^([^=\s\n][^=\n]*=)/gm, (match) => {
                const parts = match.split('=');
                if (parts.length > 1) {
                    return `<span style="color: #6ed58a; font-weight: 500;">${parts[0]}</span>=`;
                }
                return match;
            });
        }

        const tok = (cls, s) => `<span class="cfg-tok-${cls}">${esc(s)}</span>`;
        const numRe = /-?(?:0x[0-9a-fA-F]+|\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/y;
        const wordRe = /[A-Za-z_$][\w$]*/y;
        const n = raw.length;
        let out = '';
        let i = 0;
        let lineStart = true;

        while (i < n) {
            const c = raw[i];
            if (c === '\n') {
                out += '\n';
                lineStart = true;
                i++;
                continue;
            }
            if (lineStart && (c === ' ' || c === '\t')) {
                while (i < n && (raw[i] === ' ' || raw[i] === '\t')) {
                    if (raw[i] === '\t') {
                        out += '<span class="cfg-indent">\t</span>';
                        i++;
                    } else if (raw[i + 1] === ' ') {
                        out += '<span class="cfg-indent">  </span>';
                        i += 2;
                    } else {
                        out += ' ';
                        i++;
                    }
                }
                lineStart = false;
                continue;
            }
            lineStart = false;

            if (c === '"') {
                let j = i + 1;
                while (j < n && raw[j] !== '"' && raw[j] !== '\n') j += raw[j] === '\\' ? 2 : 1;
                if (j < n && raw[j] === '"') j++;
                const s = raw.slice(i, j);
                let k = j;
                while (k < n && (raw[k] === ' ' || raw[k] === '\t')) k++;
                out += tok(raw[k] === ':' ? 'key' : 'str', s);
                i = j;
                continue;
            }
            if (c === '/' && raw[i + 1] === '/') {
                let j = raw.indexOf('\n', i);
                if (j < 0) j = n;
                out += tok('comment', raw.slice(i, j));
                i = j;
                continue;
            }
            if (c === '/' && raw[i + 1] === '*') {
                let j = raw.indexOf('*/', i + 2);
                j = j < 0 ? n : j + 2;
                out += raw.slice(i, j).split('\n').map((part) => tok('comment', part)).join('\n');
                i = j;
                continue;
            }
            if (c === '-' || (c >= '0' && c <= '9')) {
                numRe.lastIndex = i;
                const m = numRe.exec(raw);
                if (m && m[0] !== '-') {
                    out += tok('num', m[0]);
                    i += m[0].length;
                    continue;
                }
            }
            if (/[A-Za-z_$]/.test(c)) {
                wordRe.lastIndex = i;
                const w = wordRe.exec(raw)[0];
                if (w === 'true' || w === 'false') out += tok('bool', w);
                else if (w === 'null') out += tok('null', w);
                else out += esc(w);
                i += w.length;
                continue;
            }
            if ('{}[],:'.includes(c)) {
                out += tok('punct', c);
                i++;
                continue;
            }
            out += esc(c);
            i++;
        }
        return out;
    }

    _setConfigEditorFont(overlay, prefs) {
        const next = {
            size: clampConfigEditorFontSize(prefs.size),
            family: prefs.family || CONFIG_EDITOR_FONT_DEFAULT_FAMILY
        };
        const prev = this._configEditorFont;
        this._configEditorFont = next;
        applyConfigEditorFont(overlay, next);
        if (!prev || prev.size !== next.size || prev.family !== next.family) saveConfigEditorFont(next);
        const sizeLabel = overlay.querySelector('#mod-config-font-size-value');
        if (sizeLabel) sizeLabel.textContent = `${next.size}px`;
        const familySelect = overlay.querySelector('#mod-config-font-family');
        if (familySelect && familySelect.value !== next.family) familySelect.value = next.family;
        requestAnimationFrame(() => this._syncConfigEditorBackdropPad(overlay));
    }

    _stepConfigEditorFont(overlay, delta) {
        const cur = this._configEditorFont || getConfigEditorFont();
        const size = delta === 0 ? CONFIG_EDITOR_FONT_DEFAULT_SIZE : cur.size + delta;
        this._setConfigEditorFont(overlay, { ...cur, size });
    }

    _setupConfigEditorLayout(overlay) {
        this._teardownConfigEditorLayout?.();
        this._configEditorFont = getConfigEditorFont();
        applyConfigEditorFont(overlay, this._configEditorFont);
        const ta = overlay.querySelector('#mod-config-editor');
        const bd = overlay.querySelector('#mod-config-highlight-backdrop');
        if (!ta || !bd) return;
        const sync = () => this._syncConfigEditorBackdropPad(overlay);
        ta.addEventListener('scroll', sync, { passive: true });
        const ro = new ResizeObserver(() => requestAnimationFrame(sync));
        ro.observe(ta);
        const wrap = ta.closest('.mod-config-editor-wrap');
        if (wrap) ro.observe(wrap);
        const onWheel = (e) => {
            if (!e.ctrlKey) return;
            e.preventDefault();
            if (!e.deltaY) return;
            this._stepConfigEditorFont(overlay, e.deltaY < 0 ? 1 : -1);
        };
        const onZoomKey = (e) => {
            if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
            if (overlay.classList.contains('config-editor-parked')) return;
            if (!wrap || !wrap.contains(document.activeElement)) return;
            const k = e.key;
            let delta = null;
            if (k === '=' || k === '+') delta = 1;
            else if (k === '-' || k === '_') delta = -1;
            else if (k === '0') delta = 0;
            if (delta === null) return;
            e.preventDefault();
            e.stopPropagation();
            this._stepConfigEditorFont(overlay, delta);
        };
        wrap?.addEventListener('wheel', onWheel, { passive: false });
        document.addEventListener('keydown', onZoomKey, true);
        const teardownFind = this._mountConfigFind(overlay);
        this._setConfigFindHostActive(true);
        this._teardownConfigEditorLayout = () => {
            teardownFind();
            this._setConfigFindHostActive(false);
            ta.removeEventListener('scroll', sync);
            wrap?.removeEventListener('wheel', onWheel);
            document.removeEventListener('keydown', onZoomKey, true);
            ro.disconnect();
            this._teardownConfigEditorLayout = null;
        };
        requestAnimationFrame(sync);
    }

    _setConfigFindHostActive(active) {
        window.chrome?.webview?.postMessage({ type: 'CONFIG_EDITOR_FIND_ACTIVE', active: !!active });
    }

    hasConfigFind() {
        return !!this._openConfigFind && !this.isConfigEditorParked();
    }

    openConfigFind() {
        if (this.isConfigEditorParked()) return;
        this._openConfigFind?.();
    }

    stepConfigFind(direction) {
        if (this.isConfigEditorParked()) return;
        this._stepConfigFind?.(direction);
    }

    _mountConfigFind(overlay) {
        const t = (key, fallback) => {
            const v = window._t(key);
            return v && v !== key ? v : fallback;
        };
        const bar = document.createElement('div');
        bar.className = 'config-find-bar';
        bar.hidden = true;
        bar.setAttribute('role', 'search');
        bar.innerHTML = `
            <div class="config-find-field">
                <input id="mod-config-find-input" class="config-find-input" type="text" spellcheck="false" autocomplete="off" placeholder="${escapeAttr(t('config_editor_find', 'Find'))}" aria-label="${escapeAttr(t('config_editor_find', 'Find'))}">
                <button type="button" class="config-find-clear" id="mod-config-find-clear" hidden aria-label="${escapeAttr(t('config_editor_find_clear', 'Clear'))}"><i data-lucide="x"></i></button>
            </div>
            <span id="mod-config-find-count" class="config-find-count" aria-live="polite"></span>
            <button type="button" class="config-find-btn" data-find="prev" title="${escapeAttr(t('config_editor_find_prev', 'Previous match'))}" aria-label="${escapeAttr(t('config_editor_find_prev', 'Previous match'))}"><i data-lucide="arrow-up"></i></button>
            <button type="button" class="config-find-btn" data-find="next" title="${escapeAttr(t('config_editor_find_next', 'Next match'))}" aria-label="${escapeAttr(t('config_editor_find_next', 'Next match'))}"><i data-lucide="arrow-down"></i></button>
            <button type="button" class="config-find-btn" data-find="case" aria-pressed="false" title="${escapeAttr(t('config_editor_find_case', 'Match case'))}" aria-label="${escapeAttr(t('config_editor_find_case', 'Match case'))}">Aa</button>
            <button type="button" class="config-find-btn" data-find="close" title="${escapeAttr(t('config_editor_find_close', 'Close'))}" aria-label="${escapeAttr(t('config_editor_find_close', 'Close'))}"><i data-lucide="x"></i></button>`;
        const host = overlay.querySelector('.config-editor-main') || overlay;
        host.appendChild(bar);
        try { window.lucide?.createIcons({ nodes: [bar] }); } catch (_) { }

        const input = bar.querySelector('#mod-config-find-input');
        const clearBtn = bar.querySelector('#mod-config-find-clear');
        const countEl = bar.querySelector('#mod-config-find-count');
        const caseBtn = bar.querySelector('[data-find="case"]');
        const state = { query: '', caseSensitive: false, active: 0 };
        this._configFindState = state;

        const editorText = () => overlay.querySelector('#mod-config-editor')?.value || '';

        const matchStarts = () => {
            const query = state.query;
            if (!query) return [];
            const text = editorText();
            const hay = state.caseSensitive ? text : text.toLowerCase();
            const needle = state.caseSensitive ? query : query.toLowerCase();
            const starts = [];
            let from = 0;
            while (from <= hay.length - needle.length) {
                const at = hay.indexOf(needle, from);
                if (at < 0) break;
                starts.push(at);
                from = at + Math.max(1, needle.length);
            }
            return starts;
        };

        const syncClear = () => {
            if (clearBtn) clearBtn.hidden = !input?.value;
        };

        const updateCount = (total) => {
            if (!countEl) return;
            if (!state.query) countEl.textContent = '';
            else countEl.textContent = total ? `${state.active + 1}/${total}` : '0/0';
            countEl.classList.toggle('config-find-count--empty', !!state.query && !total);
            syncClear();
        };

        const visibleTotal = () => {
            if (overlay.dataset.configView !== 'tree') return matchStarts().length;
            const tree = overlay.querySelector('#mod-config-json-tree');
            if (!tree || !state.query) return 0;
            const needle = state.caseSensitive ? state.query : state.query.toLowerCase();
            return [...tree.querySelectorAll('.jt-row, .jt-comment')].filter((row) => {
                const text = row.textContent || '';
                return (state.caseSensitive ? text : text.toLowerCase()).includes(needle);
            }).length;
        };

        const refresh = () => {
            const total = visibleTotal();
            if (total) state.active = ((state.active % total) + total) % total;
            else state.active = 0;
            updateCount(total);
            if (overlay.dataset.configView === 'tree') this._applyConfigFindMarks(overlay);
            else this._refreshConfigHighlight?.();
        };

        const open = () => {
            bar.hidden = false;
            input?.focus();
            input?.select();
        };

        const close = () => {
            if (bar.hidden) return false;
            bar.hidden = true;
            state.query = '';
            state.active = 0;
            if (input) input.value = '';
            updateCount(0);
            if (overlay.dataset.configView === 'tree') this._applyConfigFindMarks(overlay);
            else this._refreshConfigHighlight?.();
            return true;
        };

        const step = (direction) => {
            if (bar.hidden) {
                open();
                return;
            }
            const total = visibleTotal();
            if (!total) return;
            state.active = (state.active + direction + total) % total;
            refresh();
        };

        input?.addEventListener('input', () => {
            state.query = input.value || '';
            state.active = 0;
            refresh();
        });
        clearBtn?.addEventListener('click', (e) => {
            e.preventDefault();
            if (!input) return;
            input.value = '';
            state.query = '';
            state.active = 0;
            refresh();
            input.focus();
        });
        input?.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                e.stopPropagation();
                step(e.shiftKey ? -1 : 1);
            } else if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                close();
                overlay.querySelector('#mod-config-editor')?.focus();
            }
        });
        bar.querySelector('[data-find="prev"]')?.addEventListener('click', () => step(-1));
        bar.querySelector('[data-find="next"]')?.addEventListener('click', () => step(1));
        caseBtn?.addEventListener('click', () => {
            state.caseSensitive = !state.caseSensitive;
            caseBtn.classList.toggle('active', state.caseSensitive);
            caseBtn.setAttribute('aria-pressed', String(state.caseSensitive));
            state.active = 0;
            refresh();
        });
        bar.querySelector('[data-find="close"]')?.addEventListener('click', () => {
            close();
            overlay.querySelector('#mod-config-editor')?.focus();
        });

        this._openConfigFind = open;
        this._stepConfigFind = step;
        this._closeConfigFind = close;
        this._refreshConfigFind = refresh;

        return () => {
            bar.remove();
            state.query = '';
            this._configFindState = null;
            this._openConfigFind = null;
            this._stepConfigFind = null;
            this._closeConfigFind = null;
            this._refreshConfigFind = null;
            this._refreshConfigHighlight = null;
        };
    }

    _applyConfigFindMarks(overlay) {
        const root = overlay || document.getElementById('rename-mod-overlay');
        if (!root) return;
        const state = this._configFindState;
        const query = state?.query || '';
        const tree = root.querySelector('#mod-config-json-tree');
        tree?.querySelectorAll('.config-find-hit').forEach((el) => {
            el.classList.remove('config-find-hit', 'config-find-hit--active');
        });
        if (!query || !state) return;

        if (root.dataset.configView === 'tree' && tree) {
            const needle = state.caseSensitive ? query : query.toLowerCase();
            const rows = [...tree.querySelectorAll('.jt-row, .jt-comment')].filter((row) => {
                const text = row.textContent || '';
                return (state.caseSensitive ? text : text.toLowerCase()).includes(needle);
            });
            if (!rows.length) return;
            const active = ((state.active % rows.length) + rows.length) % rows.length;
            rows.forEach((row, i) => {
                row.classList.add('config-find-hit');
                if (i === active) row.classList.add('config-find-hit--active');
            });
            rows[active].scrollIntoView({ block: 'nearest' });
            return;
        }

        const bd = root.querySelector('#mod-config-highlight-backdrop');
        const ta = root.querySelector('#mod-config-editor');
        if (!bd) return;
        const text = ta?.value || '';
        const hay = state.caseSensitive ? text : text.toLowerCase();
        const needle = state.caseSensitive ? query : query.toLowerCase();
        const ranges = [];
        let from = 0;
        while (needle && from <= hay.length - needle.length) {
            const at = hay.indexOf(needle, from);
            if (at < 0) break;
            ranges.push({ start: at, end: at + needle.length });
            from = at + Math.max(1, needle.length);
        }
        if (!ranges.length) return;
        const active = ((state.active % ranges.length) + ranges.length) % ranges.length;
        ranges[active].active = true;
        this._markFindRanges(bd, ranges);
        const mark = bd.querySelector('.cfg-find-hit--active');
        if (mark && ta) {
            const top = mark.offsetTop - ta.clientHeight * 0.35;
            ta.scrollTop = Math.max(0, top);
            const left = mark.offsetLeft - 32;
            if (left < ta.scrollLeft || left > ta.scrollLeft + ta.clientWidth - 48) {
                ta.scrollLeft = Math.max(0, left);
            }
            bd.scrollTop = ta.scrollTop;
        }
    }

    _markFindRanges(root, ranges) {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        const nodes = [];
        while (walker.nextNode()) nodes.push(walker.currentNode);
        let pos = 0;
        for (const node of nodes) {
            const len = node.nodeValue?.length || 0;
            const nodeStart = pos;
            const nodeEnd = pos + len;
            const hits = ranges.filter((r) => r.end > nodeStart && r.start < nodeEnd);
            pos = nodeEnd;
            if (!hits.length || !node.parentNode) continue;
            const text = node.nodeValue || '';
            const frag = document.createDocumentFragment();
            let cursor = 0;
            hits.forEach((r) => {
                const a = Math.max(0, r.start - nodeStart);
                const b = Math.min(len, r.end - nodeStart);
                if (a > cursor) frag.appendChild(document.createTextNode(text.slice(cursor, a)));
                const mark = document.createElement('mark');
                mark.className = r.active ? 'cfg-find-hit cfg-find-hit--active' : 'cfg-find-hit';
                mark.textContent = text.slice(a, b);
                frag.appendChild(mark);
                cursor = b;
            });
            if (cursor < len) frag.appendChild(document.createTextNode(text.slice(cursor)));
            node.parentNode.replaceChild(frag, node);
        }
    }

    _mountConfigFontPopover(overlay) {
        const btn = overlay.querySelector('#mod-config-font-btn');
        if (!btn) return;
        const t = (key, fallback) => {
            const v = window._t(key);
            return v && v !== key ? v : fallback;
        };
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
            if (overlay.classList.contains('config-editor-parked')) return;
            if (document.querySelector('.themed-select-menu')) return;
            const hint = pop.querySelector('#mod-config-font-hint');
            if (hint && !hint.hidden) {
                e.preventDefault();
                e.stopPropagation();
                hint.hidden = true;
                const infoBtn = pop.querySelector('#mod-config-font-info');
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
            const cur = this._configEditorFont || getConfigEditorFont();
            pop = document.createElement('div');
            pop.className = 'config-font-popover';
            pop.setAttribute('role', 'dialog');
            pop.setAttribute('aria-label', t('config_editor_font', 'Editor font'));
            const hintText = t('config_editor_font_hint', 'Tip: Ctrl + Mouse Wheel in the editor changes the size. Ctrl + 0 resets it.');
            pop.innerHTML = `
                <div class="config-font-head">
                    <h3>${escapeHtmlSafe(t('config_editor_font', 'Editor font'))}</h3>
                    <div class="config-font-info-wrap">
                        <button type="button" class="config-font-info" id="mod-config-font-info" aria-expanded="false" aria-controls="mod-config-font-hint" aria-label="${escapeAttr(hintText)}">
                            <i data-lucide="info"></i>
                        </button>
                        <div class="config-font-hint" id="mod-config-font-hint" role="tooltip" hidden>${escapeHtmlSafe(hintText)}</div>
                    </div>
                </div>
                <span class="config-font-label">${escapeHtmlSafe(t('config_editor_font_size_label', 'Font size'))}</span>
                <div class="config-font-stepper">
                    <button type="button" data-font-step="-1" aria-label="-"><i data-lucide="minus"></i></button>
                    <span id="mod-config-font-size-value" class="config-font-value">${cur.size}px</span>
                    <button type="button" data-font-step="1" aria-label="+"><i data-lucide="plus"></i></button>
                </div>
                <label class="config-font-label" for="mod-config-font-family">${escapeHtmlSafe(t('config_editor_font_family_label', 'Font'))}</label>
                <select id="mod-config-font-family" class="tweak-select">
                    ${CONFIG_EDITOR_FONT_FAMILIES.map(f => `<option value="${escapeAttr(f.id)}" ${f.id === cur.family ? 'selected' : ''}>${escapeHtmlSafe(configEditorFontFamilyLabel(f))}</option>`).join('')}
                </select>
                <div class="config-font-actions">
                    <button type="button" class="btn-popup secondary" id="mod-config-font-reset">${escapeHtmlSafe(t('config_editor_font_reset', 'Reset'))}</button>
                </div>`;
            btn.parentElement.appendChild(pop);
            const br = btn.getBoundingClientRect();
            const pr = btn.parentElement.getBoundingClientRect();
            pop.style.top = `${br.bottom - pr.top + 6}px`;
            pop.style.right = `${Math.max(0, pr.right - br.right)}px`;
            try { window.lucide?.createIcons({ nodes: [pop] }); } catch (_) { }
            const infoBtn = pop.querySelector('#mod-config-font-info');
            const hint = pop.querySelector('#mod-config-font-hint');
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
                if (e.target.closest('#mod-config-font-info, #mod-config-font-hint')) return;
                setHintOpen(false);
            });
            pop.querySelectorAll('[data-font-step]').forEach(b => {
                b.addEventListener('click', () => this._stepConfigEditorFont(overlay, Number(b.dataset.fontStep)));
            });
            pop.querySelector('#mod-config-font-family')?.addEventListener('change', (e) => {
                const curNow = this._configEditorFont || getConfigEditorFont();
                this._setConfigEditorFont(overlay, { ...curNow, family: e.target.value });
            });
            pop.querySelector('#mod-config-font-reset')?.addEventListener('click', () => {
                this._setConfigEditorFont(overlay, { size: CONFIG_EDITOR_FONT_DEFAULT_SIZE, family: CONFIG_EDITOR_FONT_DEFAULT_FAMILY });
            });
            btn.setAttribute('aria-expanded', 'true');
            document.addEventListener('mousedown', onOutside, true);
            document.addEventListener('keydown', onKey, true);
        };
        btn.addEventListener('click', () => {
            if (pop) close();
            else open();
        });
        this._closeConfigFontPopover = close;
        const prevTeardown = this._teardownConfigEditorLayout;
        this._teardownConfigEditorLayout = () => {
            close();
            this._closeConfigFontPopover = null;
            prevTeardown?.();
            this._teardownConfigEditorLayout = null;
        };
    }

    _parseJsonRelaxed(text) {
        try {
            return { ok: true, value: jsonc.parse(text) };
        } catch (e) {
            return { ok: false, error: (e && e.message) ? e.message : String(e) };
        }
    }

    _normalizeModPathForMatch(value) {
        return String(value || '')
            .replace(/\\/g, '/')
            .trim()
            .replace(/\s+\./g, '.');
    }

    _modFileKeyFromOriginalName(originalName) {
        const n = this._normalizeModPathForMatch(originalName);
        return n.split('/').pop() || '';
    }

    _applyLocalModDetailsPatch(originalName, detailsValue) {
        const norm = (v) => String(v || '')
            .replace(/\\/g, '/')
            .trim()
            .replace(/\s+\./g, '.')
            .replace(/^Disabled\//i, '');
        const targetNorm = norm(originalName);
        const targetFile = String(this._modFileKeyFromOriginalName(originalName)).toLowerCase();
        const patchList = (mods) => {
            if (!Array.isArray(mods)) return;
            for (const mod of mods) {
                if (!mod) continue;
                const mn = norm(mod.originalName);
                const fk = String(this._modFileKeyFromOriginalName(mod.originalName)).toLowerCase();
                if (mn === targetNorm || fk === targetFile) {
                    mod.details = detailsValue;
                }
            }
        };
        patchList(this.data?.mods);
        if (window.app?.realData?.mods) patchList(window.app.realData.mods);
    }

    _applyLocalNexusPatch(originalName, nexusNow, nexusRemove, clearNexusLink) {
        const norm = (v) => String(v || '')
            .replace(/\\/g, '/')
            .trim()
            .replace(/\s+\./g, '.')
            .replace(/^Disabled\//i, '');
        const targetNorm = norm(originalName);
        const targetFile = String(this._modFileKeyFromOriginalName(originalName)).toLowerCase();

        const patchMod = (mod) => {
            if (!mod) return;
            const mn = norm(mod.originalName);
            const fk = String(this._modFileKeyFromOriginalName(mod.originalName)).toLowerCase();
            if (mn !== targetNorm && fk !== targetFile) return;

            if (clearNexusLink) {
                mod.url = '';
                mod.nexusModId = null;
                mod.nexusFileId = null;
                mod.nexusFileUploaded = null;
                mod.nexusFileVersion = '';
                mod.hasUpdate = false;
                mod.isUnverifiedLink = false;
                return;
            }

            if (nexusRemove.url) mod.url = '';
            else if (nexusNow.url) mod.url = nexusNow.url;

            if (nexusRemove.modId) mod.nexusModId = null;
            else if (nexusNow.modId) mod.nexusModId = nexusNow.modId;

            if (nexusRemove.fileId) mod.nexusFileId = null;
            else if (nexusNow.fileId) mod.nexusFileId = nexusNow.fileId;

            if (nexusRemove.uploaded) mod.nexusFileUploaded = null;
            else if (nexusNow.uploaded) mod.nexusFileUploaded = nexusNow.uploaded;

            if (nexusRemove.version) {
                mod.nexusFileVersion = '';
            } else if (nexusNow.version) {
                mod.nexusFileVersion = nexusNow.version;
                mod.version = nexusNow.version;
            }

            const hasModId = mod.nexusModId != null && Number(mod.nexusModId) > 0;
            const hasFileId = mod.nexusFileId != null && Number(mod.nexusFileId) > 0;
            mod.isUnverifiedLink = !!(hasModId && !hasFileId);
            mod.hasUpdate = false;
        };

        const patchList = (mods) => {
            if (!Array.isArray(mods)) return;
            for (const mod of mods) patchMod(mod);
        };

        patchList(this.data?.mods);
        if (window.app?.realData?.mods) patchList(window.app.realData.mods);

        this._modUpdatesByName.delete(originalName);
    }

    _clearPendingDetailsIfServerHasData(data) {
        if (!this._pendingModDetailsByFileKey?.size || !data?.mods) return;
        for (const mod of data.mods) {
            const fk = String(this._modFileKeyFromOriginalName(mod.originalName)).toLowerCase();
            if (this._pendingModDetailsByFileKey.has(fk) && String(mod.details || '').trim().length > 0) {
                this._pendingModDetailsByFileKey.delete(fk);
            }
        }
    }

    render(data) {
        if (data?.mods) this._mergeModUpdateFlags(data.mods);
        this.data = data;
        return ModsRenderer.render(this, data);
    }

    onMount(data) {
        this.setupEventListeners();
        ModsDndHandler.init(this);
        if (window.lucide) window.lucide.createIcons();
        this.updateDeployButtonText();
        applyModTypeBadgeStylesheet();
        this.filterModRowsInPlace((this.lastSearchTerm || '').trim().toLowerCase());
        this.requestModUpdateCheck();
        this._updateBulkUpdateButtonLabel();
    }

    _mergeModUpdateFlags(mods) {
        if (!Array.isArray(mods)) return mods;
        for (const mod of mods) {
            if (!mod?.originalName) continue;
            const cached = this._modUpdatesByName.get(mod.originalName);
            if (cached) {
                Object.assign(mod, cached);
            }
        }
        return mods;
    }

    requestModUpdateCheck() {
        const loggedIn = window.app?.realData?.managerSettings?.nexusLoggedIn;
        if (!loggedIn) return;
        const now = Date.now();
        if (this._lastModUpdateCheckAt && (now - this._lastModUpdateCheckAt) < 12000) return;
        if (this._modUpdateCheckInFlight) {
            this._modUpdateCheckPending = true;
            return;
        }
        this._modUpdateCheckInFlight = true;
        this._lastModUpdateCheckAt = now;
        window.chrome.webview.postMessage({ type: 'CHECK_MOD_UPDATES' });
    }

    forceModUpdateCheck() {
        const loggedIn = window.app?.realData?.managerSettings?.nexusLoggedIn;
        if (!loggedIn) {
            window.app?.showBanner?.({ type: 'warning', text: window._t('update_login_required') });
            return;
        }
        if (this._modUpdateCheckTimer) {
            clearTimeout(this._modUpdateCheckTimer);
            this._modUpdateCheckTimer = null;
        }
        this._modUpdateCheckPending = false;
        this._modUpdateCheckInFlight = true;
        this._lastModUpdateCheckAt = Date.now();
        window.app?.showBanner?.({ type: 'info', text: window._t('mods_check_updates_running') });
        window.chrome.webview.postMessage({ type: 'CHECK_MOD_UPDATES', force: true });
    }

    scheduleModUpdateCheck(delayMs = 700) {
        if (this._modUpdateCheckTimer) clearTimeout(this._modUpdateCheckTimer);
        this._modUpdateCheckTimer = setTimeout(() => {
            this._modUpdateCheckTimer = null;
            this.requestModUpdateCheck();
        }, delayMs);
    }

    _shouldRecheckModUpdates(oldData, newData) {
        const oldMap = new Map((oldData?.mods || []).map(m => [m.originalName, m]));
        for (const mod of (newData?.mods || [])) {
            if (!mod?.nexusModId) continue;
            const prev = oldMap.get(mod.originalName);
            if (!prev?.nexusModId) return true;
        }
        return false;
    }

    handleModUpdatesResult(payload) {
        this._modUpdateCheckInFlight = false;
        const updates = Array.isArray(payload?.updates) ? payload.updates : [];
        for (const u of updates) {
            const key = u.originalName;
            if (!key) continue;
            this._modUpdatesByName.set(key, {
                hasUpdate: !!u.hasUpdate,
                isUnverifiedLink: !!u.isUnverifiedLink,
                latestFileId: u.latestFileId,
                latestVersion: u.latestVersion || '',
                latestFileName: u.latestFileName || '',
                latestUploaded: u.latestUploaded
            });
        }
        if (this.data?.mods) this._mergeModUpdateFlags(this.data.mods);
        if (window.app?.realData?.mods) this._mergeModUpdateFlags(window.app.realData.mods);
        this._applyUpdateFlagsToVisibleRows();
        this._updateBulkUpdateButtonLabel();
        if (payload?.forced) {
            if (payload.error === 'nexus_not_connected') {
                window.app?.showBanner?.({ type: 'warning', text: window._t('update_login_required') });
            } else if (payload.busy) {
                window.app?.showBanner?.({ type: 'info', text: window._t('mods_check_updates_busy') });
            } else {
                const found = updates.filter(u => u.hasUpdate).length;
                window.app?.showBanner?.(found > 0
                    ? { type: 'success', text: window._t('mods_check_updates_found', found) }
                    : { type: 'success', text: window._t('mods_check_updates_none') });
            }
        }
        if (this._modUpdateCheckPending) {
            this._modUpdateCheckPending = false;
            this.scheduleModUpdateCheck(300);
        }
    }

    handleNexusResolveModLinkResult(payload) {
        const pending = this._pendingNexusResolve;
        this._pendingNexusResolve = null;
        if (pending && typeof pending.apply === 'function') {
            pending.apply(payload || {});
            return;
        }
        if (payload?.ok) {
            window.app?.showBanner?.({
                type: 'success',
                text: window._t('rename_mod_nexus_fetch_ok') || 'Nexus file data ready.'
            });
        } else if (payload?.error) {
            window.app?.showBanner?.({ type: 'error', text: String(payload.error) });
        }
    }

    getAvailableUpdateCount() {
        let count = 0;
        for (const [, cached] of this._modUpdatesByName) {
            if (cached?.hasUpdate) count++;
        }
        return count;
    }

    _updateBulkUpdateButtonLabel() {
        const labelEl = document.getElementById('mods-action-bulk-update-label');
        if (!labelEl) return;
        const count = this.getAvailableUpdateCount();
        labelEl.textContent = count > 0
            ? window._t('bulk_update_mods_count', count)
            : window._t('bulk_update_mods');
    }

    _closeModsActionsMenu() {
        const dropdown = document.getElementById('mods-actions-dropdown');
        const trigger = document.getElementById('btn-mods-actions');
        const wrap = trigger?.closest('.mods-actions-menu-wrap');
        if (dropdown) {
            dropdown.hidden = true;
            dropdown.classList.remove('mods-actions-dropdown--fixed');
            dropdown.style.top = '';
            dropdown.style.left = '';
            dropdown.style.maxHeight = '';
            if (wrap && dropdown.parentElement !== wrap) {
                wrap.appendChild(dropdown);
            } else if (!wrap && dropdown.parentElement === document.body) {
                dropdown.remove();
            }
        }
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
        this._modsActionsMenuIgnoreOutside = false;
    }

    _positionModsActionsMenu(trigger, menu) {
        if (!trigger || !menu) return;
        if (menu.parentElement !== document.body) {
            document.body.appendChild(menu);
        }
        menu.classList.add('mods-actions-dropdown--fixed');
        menu.hidden = false;
        const prevVis = menu.style.visibility;
        menu.style.visibility = 'hidden';
        const maxH = Math.min(window.innerHeight * 0.7, 480);
        menu.style.maxHeight = `${Math.round(maxH)}px`;
        const menuW = menu.offsetWidth || 220;
        const menuH = Math.min(menu.scrollHeight || maxH, maxH);
        menu.style.visibility = prevVis || '';

        const r = trigger.getBoundingClientRect();
        let top = r.bottom + 6;
        let left = r.right - menuW;
        if (top + menuH > window.innerHeight - 8) top = Math.max(8, r.top - menuH - 6);
        if (left < 8) left = 8;
        if (left + menuW > window.innerWidth - 8) left = window.innerWidth - menuW - 8;

        menu.style.top = `${Math.round(top)}px`;
        menu.style.left = `${Math.round(left)}px`;
    }

    _mountModsActionsMenu() {
        const trigger = document.getElementById('btn-mods-actions');
        const dropdown = document.getElementById('mods-actions-dropdown');
        const btnBulkUpdate = document.getElementById('mods-action-bulk-update');
        if (!trigger || !dropdown) return;

        if (this._modsActionsMenuAbort) {
            this._modsActionsMenuAbort.abort();
        }
        this._modsActionsMenuAbort = new AbortController();
        const { signal } = this._modsActionsMenuAbort;

        this._closeModsActionsMenu();

        trigger.addEventListener('click', (e) => {
            e.preventDefault();
            e.stopPropagation();
            const open = dropdown.hidden;
            if (open) {
                this._modsActionsMenuIgnoreOutside = true;
                this._positionModsActionsMenu(trigger, dropdown);
                trigger.setAttribute('aria-expanded', 'true');
                if (window.lucide) window.lucide.createIcons();
                queueMicrotask(() => { this._modsActionsMenuIgnoreOutside = false; });
            } else {
                this._closeModsActionsMenu();
            }
        }, { signal });

        document.addEventListener('click', (e) => {
            if (!document.getElementById('btn-mods-actions')) {
                this._closeModsActionsMenu();
                return;
            }
            if (this._modsActionsMenuIgnoreOutside) return;
            const inside =
                e.target.closest('.mods-actions-menu-wrap') ||
                e.target.closest('#mods-actions-dropdown');
            if (!inside) this._closeModsActionsMenu();
        }, { signal });

        window.addEventListener('resize', () => this._closeModsActionsMenu(), { signal });
        const btnAddMod = document.getElementById('mods-action-add-mod');
        if (btnAddMod) {
            btnAddMod.addEventListener('click', (e) => {
                e.stopPropagation();
                this._closeModsActionsMenu();
                window.chrome.webview.postMessage({ type: 'ADD_MOD' });
            }, { signal });
        }

        document.getElementById('mods-action-badge-color')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            const mods = this.data?.mods || window.app?.realData?.mods || [];
            void openTabBadgeColorModal({ mods, scope: 'mods' });
        }, { signal });

        document.getElementById('mods-action-check-updates')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            this.forceModUpdateCheck();
        }, { signal });

        document.getElementById('mods-action-backup-mods')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage({ type: 'BACKUP_MODS' });
            }
        }, { signal });

        document.getElementById('mods-action-preset-rename')?.addEventListener('click', async (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            const oldName = this.modGroupsManager.getActiveName();
            const next = await window.appPrompt({
                title: window._t('preset_rename') || 'Rename preset',
                promptLabel: window._t('preset_rename_prompt') || 'Rename preset:',
                defaultValue: oldName,
                okText: window._t('save') || 'Save'
            });
            if (!next || next.trim() === oldName) return;
            if (this.modGroupsManager.renamePreset(oldName, next.trim())) {
                this.currentPreset = next.trim();
                this.refreshUI();
            }
        }, { signal });

        document.getElementById('mods-action-preset-new')?.addEventListener('click', async (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            const name = await window.appPrompt({
                title: window._t('new_preset') || 'New…',
                promptLabel: window._t('new_preset_prompt') || 'Enter new preset name:',
                okText: window._t('create') || 'Create'
            });
            if (name && this.modGroupsManager.createPreset(name.trim())) {
                this.currentPreset = name.trim();
                this.refreshUI();
            }
        }, { signal });

        document.getElementById('mods-action-preset-duplicate')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            const created = this.modGroupsManager.duplicatePreset(this.modGroupsManager.getActiveName());
            if (created) {
                this.currentPreset = created;
                this.refreshUI();
            }
        }, { signal });

        document.getElementById('mods-action-preset-export')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            this.modGroupsManager.exportActive();
        }, { signal });

        document.getElementById('mods-action-preset-import')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            this.modGroupsManager.importPreset();
        }, { signal });

        document.getElementById('mods-action-preset-delete')?.addEventListener('click', async (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            const name = this.modGroupsManager.getActiveName();
            if (this.modGroupsManager.isDefaultPreset?.(name)) {
                window.app?.showBanner?.({ type: 'warning', text: window._t('cannot_delete_default_preset') });
                return;
            }
            const ok = await window.appConfirm({
                title: window._t('delete_preset') || 'Delete Preset',
                message: window._t('delete_preset_confirm', name),
                okText: window._t('delete'),
                cancelText: window._t('cancel'),
                danger: true
            });
            if (ok) {
                this.modGroupsManager.deletePreset(name);
                this.currentPreset = this.modGroupsManager.getActiveName();
                this.refreshUI();
            }
        }, { signal });

        document.getElementById('mods-action-transfer-to-other')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            void window.transferModsAcrossPlatforms?.('to-other');
        }, { signal });

        document.getElementById('mods-action-transfer-from-other')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            void window.transferModsAcrossPlatforms?.('from-other');
        }, { signal });

        const btnDeleteAll = document.getElementById('mods-action-delete-all');
        if (btnDeleteAll) {
            btnDeleteAll.addEventListener('click', async (e) => {
                e.stopPropagation();
                this._closeModsActionsMenu();
                const modCount = (this.data?.mods || window.app?.realData?.mods || []).length;
                const ok = await window.appConfirm({
                    title: window._t('delete_all_mods'),
                    message: window._t('delete_all_mods_confirm', modCount),
                    okText: window._t('delete_all_mods'),
                    cancelText: window._t('cancel'),
                    danger: true
                });
                if (ok) {
                    window.chrome.webview.postMessage({ type: 'DELETE_ALL_MODS' });
                }
            }, { signal });
        }

        if (btnBulkUpdate) {
            btnBulkUpdate.addEventListener('click', async (e) => {
                e.stopPropagation();
                this._closeModsActionsMenu();
                const count = this.getAvailableUpdateCount();
                const ok = await window.appConfirm({
                    title: window._t('bulk_update_mods'),
                    message: count > 0
                        ? window._t('bulk_update_confirm', count)
                        : window._t('bulk_update_check_confirm'),
                    okText: window._t('bulk_update_mods'),
                    cancelText: window._t('cancel')
                });
                if (ok) {
                    this._bulkUpdateInProgress = true;
                    btnBulkUpdate.disabled = true;
                    window.chrome.webview.postMessage({ type: 'BULK_UPDATE_MODS' });
                }
            }, { signal });
        }

        this._updateBulkUpdateButtonLabel();
        this.updateDeployButtonText();
    }

    handleBulkUpdateStarted(payload) {
        this._bulkUpdateInProgress = true;
        const total = payload?.total || 0;
        window.app?.showBanner?.({ type: 'info', text: window._t('bulk_update_started', total) });
    }

    handleBulkUpdateProgress(payload) {
        const current = payload?.current || 0;
        const total = payload?.total || 0;
        const modName = payload?.modName || '';
        window.app?.showBanner?.({
            type: 'info',
            text: window._t('bulk_update_progress', current, total, modName)
        });
    }

    handleBulkUpdateComplete(payload) {
        this._bulkUpdateInProgress = false;
        const btn = document.getElementById('mods-action-bulk-update');
        if (btn) btn.disabled = false;
        const updated = payload?.updated || 0;
        const failed = payload?.failed || 0;
        if (payload?.error) {
            window.app?.showBanner?.({ type: 'error', text: payload.error });
        } else {
            window.app?.showBanner?.({
                type: updated > 0 ? 'success' : 'info',
                text: window._t('bulk_update_complete_summary', updated, failed)
            });
        }
        this.requestModUpdateCheck();
    }

    _applyUpdateFlagsToVisibleRows() {
        document.querySelectorAll('.mod-row').forEach((row) => {
            const name = row.dataset.name;
            const mod = (this.data?.mods || []).find(m => m.originalName === name) ||
                (window.app?.realData?.mods || []).find(m => m.originalName === name);
            if (!mod) return;
            const cached = this._modUpdatesByName.get(name);
            if (cached) Object.assign(mod, cached);

            const versionCell = row.querySelector('.mod-version-text');
            if (versionCell) {
                const v = (mod.version && String(mod.version).trim()) ? String(mod.version).trim() : '-';
                versionCell.textContent = v;
            }

            const updateSlot = row.querySelector('.action-slot-update');
            const unverifiedSlot = row.querySelector('.action-slot-unverified');
            if (updateSlot) {
                updateSlot.innerHTML = buildModUpdateButtonHtml(mod);
            }
            if (unverifiedSlot) {
                unverifiedSlot.innerHTML = buildModUnverifiedButtonHtml(mod);
            }
            if (window.lucide) {
                const iconNodes = [];
                if (updateSlot) iconNodes.push(updateSlot);
                if (unverifiedSlot) iconNodes.push(unverifiedSlot);
                if (iconNodes.length) window.lucide.createIcons({ nodes: iconNodes });
            }
        });
    }

    initGlobalHandlers() {
        if (!_modsGlobalEventsAttached) {
            _modsGlobalEventsAttached = true;
        }

        if (!window.handleDeleteMod) {
            window.handleDeleteMod = async (name) => {
                await deleteModWithConfirmation(name);
            };
        }

        if (!window.toggleLooseModExpand) {
            window.toggleLooseModExpand = (el) => {
                const name = el?.getAttribute?.('data-name') || el?.dataset?.name;
                if (!name) return;
                const mgr = window.app?.sections?.mods || this;
                if (!mgr.expandedLooseKeys) mgr.expandedLooseKeys = new Set();
                if (mgr.expandedLooseKeys.has(name)) mgr.expandedLooseKeys.delete(name);
                else mgr.expandedLooseKeys.add(name);
                if (typeof mgr.refreshUI === 'function') mgr.refreshUI();
            };
        }
        
        if (!window.nuclearDelete) {
            window.nuclearDelete = async (el) => {
                const name = el?.getAttribute?.('data-name') || el?.dataset?.name;
                if (!name) return;
                const msg = `[NUCLEAR] Deleting: ${name}`;
                console.log(msg);
                if (window.logToScreen) window.logToScreen(msg);
                await deleteModWithConfirmation(name);
            };
        }

        if (!window.nuclearEdit) {
            window.nuclearEdit = (el) => {
                const btn = el?.closest?.('.btn-edit-mod') || el;
                const name = btn?.getAttribute?.('data-name') || btn?.dataset?.name;
                if (!name) return;
                const openTab = btn.getAttribute?.('data-open-tab') || 'general';
                console.log("[NUCLEAR] Edit requested for:", name, "tab:", openTab);
                void this._openRenameModalAfterParkedEditor(name, openTab);
            };
        }

        if (!window.nuclearModUpdate) {
            window.nuclearModUpdate = (el) => {
                if (!el || el.disabled || el.classList.contains('btn-mod-state-off') || el.classList.contains('btn-mod-update-none')) {
                    window.app?.showBanner?.({ type: 'info', text: window._t('mod_already_up_to_date') || 'This mod is already up to date.' });
                    return;
                }
                if (this._bulkUpdateInProgress) {
                    window.app?.showBanner?.({ type: 'warning', text: window._t('bulk_update_in_progress') });
                    return;
                }
                const originalName = el.getAttribute('data-name');
                const modId = parseInt(el.getAttribute('data-mod-id') || '0', 10);
                const fileId = parseInt(el.getAttribute('data-file-id') || '0', 10);
                const fileName = el.getAttribute('data-file-name') || 'mod_update';
                const fileVersion = el.getAttribute('data-file-version') || '';
                const fileUploadedRaw = el.getAttribute('data-file-uploaded');
                if (!modId || !fileId) {
                    window.app?.showBanner?.({
                        type: 'warning',
                        text: window._t('mod_update_unavailable') || 'This mod is not linked to Nexus or no update file is available.'
                    });
                    return;
                }
                const payload = {
                    type: 'UPDATE_MOD_FROM_NEXUS',
                    originalName,
                    modId,
                    fileId,
                    fileName,
                    fileVersion
                };
                if (fileUploadedRaw) payload.fileUploaded = parseInt(fileUploadedRaw, 10);
                window.app?.showBanner?.({ type: 'info', text: window._t('mod_update_starting') });
                window.chrome.webview.postMessage(payload);
            };
        }

        if (!window.handleModToggle) {
            window.handleModToggle = (checkbox, originalName) => {
                window._modsToggleInProgress = true;
                
                const modName = originalName.replace(/^Disabled\//, '');
                const isEnabled = checkbox.checked;
                console.log(`[TOGGLE] ${modName} -> ${isEnabled ? 'enabled' : 'disabled'}`);
                
                window.chrome.webview.postMessage({ 
                    type: 'TOGGLE_MOD', 
                    name: modName, 
                    enabled: isEnabled 
                });

                try {
                    const mgr = window.app?.modGroups || window.modsManager?.modGroupsManager;
                    const preset = mgr?.getActive?.();
                    if (preset) {
                        const norm = (n) => (mgr?.constructor?.normalizeModKey
                            ? mgr.constructor.normalizeModKey(n)
                            : String(n || '').replace(/^Disabled\//i, ''));
                        const clean = norm(modName);
                        const en = new Set((preset.enabled || []).map(norm).filter(Boolean));
                        if (isEnabled) en.add(clean);
                        else en.delete(clean);
                        preset.enabled = [...en];
                        const members = Array.isArray(preset.mods) ? preset.mods.map(norm) : [];
                        if (isEnabled && members.length > 0 && !members.some(m => m.toLowerCase() === clean.toLowerCase())) {
                            preset.mods = [...members, clean];
                        }
                        mgr.save?.(true);
                    }
                } catch {  }

                const row = checkbox.closest('.mod-row');
                if (row) {
                    if (isEnabled) {
                        row.classList.remove('mod-disabled');
                    } else {
                        row.classList.add('mod-disabled');
                    }
                }
                
                setTimeout(() => {
                    window._modsToggleInProgress = false;
                }, 500);
            };
        }
    }

    handleModFileContent(data) {
        try {
            const overlay = document.getElementById('rename-mod-overlay');
            if (!overlay) return;
            const select = overlay.querySelector('#mod-config-file-select');
            const editor = overlay.querySelector('#mod-config-editor');
            const backdrop = overlay.querySelector('#mod-config-highlight-backdrop');
            const status = overlay.querySelector('#mod-config-file-status');
            const saveBtn = overlay.querySelector('#mod-config-save');
            const validateBtn = overlay.querySelector('#mod-config-validate');
            const formatBtn = overlay.querySelector('#mod-config-format');
            if (!editor) return;
            const expected = select ? (select.value || '') : (overlay.dataset.configRelativePath || '');
            if (String(data.relativePath || '') !== String(expected || '')) return;

            const err = data.error ? String(data.error) : '';
            const setStatus = (text, level = 'neutral') => {
                const t = text || '';
                if (!status) return;
                status.textContent = t;
                status.className = 'mod-config-status';
                if (level === 'error') status.classList.add('mod-config-status--error');
                else if (level === 'success') status.classList.add('mod-config-status--success');
                else status.classList.add('text-muted');
            };
            setStatus(err, err ? 'error' : 'neutral');

            if (err) {
                editor.value = '';
                this._configBaseline = null;
                editor.disabled = true;
                if (saveBtn) saveBtn.disabled = true;
                if (validateBtn) validateBtn.disabled = true;
                if (formatBtn) formatBtn.disabled = true;
                if (backdrop) backdrop.innerHTML = '\n';
                return;
            }

            editor.value = String(data.content ?? '');
            this._configBaseline = editor.value;
            editor.disabled = false;
            setStatus('', 'neutral');

            overlay.dataset.configEncoding = String(data.encoding || 'utf-8');
            const encodingEl = overlay.querySelector('#mod-config-encoding');
            if (encodingEl) {
                const label = String(data.encodingLabel || 'UTF-8');
                encodingEl.textContent = label;
                encodingEl.title = window._t('config_editor_encoding', label);
                encodingEl.hidden = false;
            }

            const isJson = String(data.relativePath || '').toLowerCase().endsWith('.json');
            if (backdrop) {
                backdrop.innerHTML = this._renderConfigHighlight(editor.value, isJson) + '\n';
                backdrop.scrollTop = editor.scrollTop;
                this._syncConfigEditorBackdropPad(overlay);
            }

            if (saveBtn) saveBtn.disabled = false;
            if (validateBtn) validateBtn.disabled = !isJson;
            if (formatBtn) formatBtn.disabled = !isJson;
            this._onConfigContentLoaded?.(isJson);
        } catch (e) {
            console.warn('[MODS] handleModFileContent failed', e);
        }
    }

    handleModFileWriteResult(data) {
        const waiter = this._configSaveWaiter;
        this._configSaveWaiter = null;
        try {
            const overlay = document.getElementById('rename-mod-overlay');
            if (!overlay) return;
            if (data.ok && this._configPendingSave != null) this._configBaseline = this._configPendingSave;
            this._configPendingSave = null;
            const status = overlay.querySelector('#mod-config-file-status');
            if (!status) return;
            if (data.ok) {
                if (data.applied != null) {
                    const count = Number(data.applied) || 0;
                    const target = String(data.appliedTarget || 'INI');
                    status.textContent = count === 1
                        ? window._t('config_editor_saved_applied_one', target)
                        : window._t('config_editor_saved_applied', count, target);
                } else {
                    status.textContent = window._t('config_editor_saved');
                }
                status.className = 'mod-config-status mod-config-status--success';
            } else {
                const msg = data.errorKey
                    ? window._t(data.errorKey, ...(Array.isArray(data.errorArgs) ? data.errorArgs : []))
                    : String(data.error || window._t('config_editor_save_failed'));
                status.textContent = msg;
                status.className = 'mod-config-status mod-config-status--error';
            }
        } catch (e) {
            console.warn('[MODS] handleModFileWriteResult failed', e);
        } finally {
            if (waiter) waiter(!!data?.ok);
        }
    }

    isConfigEditorOpen() {
        return !!document.querySelector('#rename-mod-overlay.config-editor-page:not(.config-editor-parked)');
    }

    isConfigEditorParked() {
        return !!document.querySelector('#rename-mod-overlay.config-editor-page.config-editor-parked');
    }

    configEditorOriginSection() {
        return document.querySelector('#rename-mod-overlay.config-editor-page')?.dataset.originSection || '';
    }

    parkConfigEditor() {
        const overlay = document.querySelector('#rename-mod-overlay.config-editor-page');
        if (!overlay || overlay.classList.contains('config-editor-parked')) return;
        this._closeConfigFontPopover?.();
        this._closeConfigFind?.();
        if (overlay.contains(document.activeElement)) document.activeElement.blur();
        overlay.classList.add('config-editor-parked');
        document.querySelector('.main-container')?.classList.remove('config-editor-open');
        this._setConfigFindHostActive(false);
    }

    unparkConfigEditor() {
        const overlay = document.querySelector('#rename-mod-overlay.config-editor-page.config-editor-parked');
        if (!overlay) return;
        overlay.classList.remove('config-editor-parked');
        document.querySelector('.main-container')?.classList.add('config-editor-open');
        this._setConfigFindHostActive(true);
        const titleEl = document.getElementById('section-title');
        const fileName = String(overlay.dataset.configRelativePath || '').split('/').pop();
        if (titleEl && fileName) titleEl.textContent = fileName;
        requestAnimationFrame(() => this._syncConfigEditorBackdropPad(overlay));
    }

    async _openRenameModalAfterParkedEditor(name, tab) {
        if (this.isConfigEditorParked() && !(await this.requestCloseConfigEditor())) return;
        this.showRenameModal(name, tab);
    }

    isConfigEditorDirty() {
        const overlay = document.querySelector('#rename-mod-overlay.config-editor-page');
        if (!overlay || this._configBaseline == null) return false;
        const active = document.activeElement;
        if (active && active.classList && active.classList.contains('jt-input') && overlay.contains(active)) active.blur();
        const editor = overlay.querySelector('#mod-config-editor');
        if (!editor || editor.disabled) return false;
        return (editor.value ?? '') !== this._configBaseline;
    }

    async requestCloseConfigEditor() {
        const overlay = document.querySelector('#rename-mod-overlay.config-editor-page');
        if (!overlay) return true;
        if (this._configClosePrompting) return false;
        if (!this.isConfigEditorDirty()) {
            this.hideRenameModal();
            return true;
        }
        const fileName = String(overlay.querySelector('#rename-mod-input')?.value || overlay.dataset.configRelativePath || '').split('/').pop();
        this._configClosePrompting = true;
        let choice;
        try {
            choice = await window.appConfirm({
                title: window._t('config_unsaved_title'),
                message: window._t('config_unsaved_message', fileName),
                okText: window._t('config_unsaved_save'),
                altText: window._t('config_unsaved_discard'),
                cancelText: window._t('config_unsaved_stay'),
            });
        } finally {
            this._configClosePrompting = false;
        }
        if (choice === 'alt') {
            this.hideRenameModal();
            return true;
        }
        if (choice !== true) return false;
        const saveBtn = overlay.querySelector('#mod-config-save');
        if (!saveBtn || saveBtn.disabled) return false;
        const saved = await new Promise((resolve) => {
            const timer = setTimeout(() => {
                if (this._configSaveWaiter === done) this._configSaveWaiter = null;
                resolve(false);
            }, 15000);
            const done = (ok) => { clearTimeout(timer); resolve(ok); };
            this._configSaveWaiter = done;
            saveBtn.click();
        });
        if (!saved || !document.body.contains(overlay)) return false;
        this.hideRenameModal();
        return true;
    }

    handleModFileAdoptResult(data) {
        try {
            const overlay = document.getElementById('rename-mod-overlay');
            if (!overlay) return;
            const status = overlay.querySelector('#mod-config-file-status');
            if (!status) return;
            if (data.ok) {
                const tgt = String(data.targetMod || 'mod');
                const msg = data.alreadyOwned
                    ? window._t('config_editor_adopt_already', tgt)
                    : window._t('config_editor_adopted', tgt);
                status.textContent = msg;
                status.className = 'mod-config-status mod-config-status--success';
            } else {
                const msg = String(data.error || window._t('config_editor_adopt_failed'));
                status.textContent = msg;
                status.className = 'mod-config-status mod-config-status--error';
            }
        } catch (e) {
            console.warn('[MODS] handleModFileAdoptResult failed', e);
        }
    }

    _setModFileDropTarget(name) {
        const next = name || null;
        if (this._modFileDropTarget === next) return;
        this._modFileDropTarget = next;
        window.chrome?.webview?.postMessage?.({ type: 'SET_MOD_FILE_DROP_TARGET', name: next });
    }

    handleModContents(data) {
        const overlay = document.getElementById('rename-mod-overlay');
        const openFor = overlay?.dataset?.filesModName;
        if (!openFor || !data?.name ||
            this._normalizeModPathForMatch(openFor) !== this._normalizeModPathForMatch(data.name)) return;

        const list = document.getElementById('rename-mod-files-list');
        const countLabel = document.getElementById('rename-mod-files-count');
        if (!list) return;

        const esc = (value) => String(value || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
        const entries = Array.isArray(data.entries) ? data.entries : [];
        const maxRows = 2000;

        if (countLabel) {
            countLabel.textContent = `${window._t('rename_mod_files_current')} (${entries.length})`;
        }
        if (data.error && entries.length === 0) {
            list.innerHTML = `<li class="rename-mod-files-status--error">${esc(data.error)}</li>`;
            return;
        }
        if (entries.length === 0) {
            list.innerHTML = `<li class="text-muted">${esc(window._t('rename_mod_files_none'))}</li>`;
            return;
        }

        const sorted = entries
            .map(e => String(e?.path || '').replace(/\//g, '\\'))
            .filter(Boolean)
            .sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
        let html = sorted.slice(0, maxRows).map(p => `<li title="${esc(p)}">${esc(p)}</li>`).join('');
        if (sorted.length > maxRows) {
            html += `<li class="text-muted">${esc(String(window._t('rename_mod_files_more')).replace('{count}', String(sorted.length - maxRows)))}</li>`;
        }
        list.innerHTML = html;
    }

    handleModFilesAdded(data) {
        const overlay = document.getElementById('rename-mod-overlay');
        const openFor = overlay?.dataset?.filesModName;
        const matches = openFor && data?.name &&
            this._normalizeModPathForMatch(openFor) === this._normalizeModPathForMatch(data.name);
        const status = matches ? document.getElementById('rename-mod-files-status') : null;
        const setStatus = (text, isError = false) => {
            if (!status) return;
            status.hidden = !text;
            status.textContent = text || '';
            status.classList.toggle('rename-mod-files-status--error', !!isError);
        };

        if (data?.type === 'MOD_FILES_ADDING') {
            setStatus(window._t('rename_mod_files_adding'));
            overlay?.querySelector('.rename-mod-files-dropzone')?.classList.add('busy');
            return;
        }

        overlay?.querySelector('.rename-mod-files-dropzone')?.classList.remove('busy');
        if (!data?.ok) {
            setStatus(data?.error || window._t('rename_mod_files_failed'), true);
            return;
        }
        if (!matches) return;

        const reopenName = data.newName || data.name;
        const count = data.count || 0;
        setTimeout(() => {
            this.showRenameModal(reopenName, 'files');
            const refreshed = document.getElementById('rename-mod-files-status');
            if (refreshed) {
                refreshed.hidden = false;
                refreshed.textContent = String(window._t('rename_mod_files_added')).replace('{count}', String(count));
            }
        }, 150);
    }

    hideRenameModal() {
        this._teardownConfigEditorLayout?.();
        this._configJsonTree?.destroy();
        this._configJsonTree = null;
        this._onConfigContentLoaded = null;
        this._setModFileDropTarget(null);
        this._pendingNexusResolve = null;
        const overlay = document.getElementById('rename-mod-overlay');
        if (overlay) {
            const wasPage = overlay.classList.contains('config-editor-page');
            overlay.remove();
            if (wasPage) {
                document.querySelector('.main-container')?.classList.remove('config-editor-open');
                const titleEl = document.getElementById('section-title');
                const app = window.app;
                if (titleEl && app?.currentSection && typeof app.formatSectionTitle === 'function')
                    titleEl.textContent = app.formatSectionTitle(app.currentSection);
            }
        }
        this._configBaseline = null;
        this._configPendingSave = null;
        const pendingWaiter = this._configSaveWaiter;
        this._configSaveWaiter = null;
        if (pendingWaiter) pendingWaiter(false);
        if (this.renameModalKeyHandler) {
            document.removeEventListener('keydown', this.renameModalKeyHandler);
            this.renameModalKeyHandler = null;
        }
    }

    showRenameModal(currentName, initialTab = 'general') {
        this.hideRenameModal();
        try { window.dismissAppConfirm?.(); } catch (_) { }

        const escapeHtml = (value) => String(value || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');

        const name = currentName || '';
        const normalizeOriginalName = (value) => String(value || '')
            .replace(/\\/g, '/')
            .trim()
            .replace(/\s+\./g, '.')
            .replace(/^Disabled\//i, '');
        const toFileKey = (value) => normalizeOriginalName(value).split('/').pop() || '';
        const editableName = name
            .replace(/^Disabled\//, '')
            .replace(/^Bundles\//, '')
            .replace(/^Strings\//, '')
            .replace(/^GameRoot\//, '');
        const isCoreIni = /^CoreIni\//i.test(editableName);
        const addToBaseName = String(editableName || '').replace(/^CoreIni\//i, '').split('/').pop() || '';
        const isAddToOverlay = /^F76-Manager-AddTo(Custom|Prefs|Fallout76)\.ini$/i.test(addToBaseName)
            || /^F76AddTo(Custom|Prefs|Fallout76)\.ini$/i.test(addToBaseName);
        const currentNameNormalized = normalizeOriginalName(name);
        const currentFileKey = toFileKey(name);
        const rawNameNorm = this._normalizeModPathForMatch(name);
        const modList = this.data?.mods || window.app?.realData?.mods || [];
        const currentMod = modList
            .filter((mod) => {
                const modNormalized = normalizeOriginalName(mod.originalName);
                const modRawNorm = this._normalizeModPathForMatch(mod.originalName);
                return modNormalized === currentNameNormalized ||
                    toFileKey(mod.originalName) === currentFileKey ||
                    modRawNorm === rawNameNorm;
            })
            .sort((a, b) => {
                const aRaw = this._normalizeModPathForMatch(a.originalName) === rawNameNorm ? 2 : 0;
                const bRaw = this._normalizeModPathForMatch(b.originalName) === rawNameNorm ? 2 : 0;
                if (aRaw !== bRaw) return bRaw - aRaw;
                const aExact = normalizeOriginalName(a.originalName) === currentNameNormalized ? 1 : 0;
                const bExact = normalizeOriginalName(b.originalName) === currentNameNormalized ? 1 : 0;
                if (aExact !== bExact) return bExact - aExact;
                const aHasDetails = (a.details || '').trim().length > 0 ? 1 : 0;
                const bHasDetails = (b.details || '').trim().length > 0 ? 1 : 0;
                return bHasDetails - aHasDetails;
            })[0];

        const editableConfigExts = new Set(['.json', '.txt', '.ini', '.toml']);
        const modFiles = Array.isArray(currentMod?.files) ? currentMod.files : [];
        const configFiles = modFiles
            .map(f => String(f || '').replace(/\\/g, '/').trim())
            .filter(f => {
                const idx = f.lastIndexOf('.');
                const dotExt = idx >= 0 ? f.substring(idx).toLowerCase() : '';
                return editableConfigExts.has(dotExt);
            });
        const fromList = (currentMod?.details != null && String(currentMod.details).trim().length > 0)
            ? currentMod.details
            : '';
        const fromPending = this._pendingModDetailsByFileKey.get(String(currentFileKey).toLowerCase());
        const existingDetails = fromList || (fromPending != null ? fromPending : '');

        const normPath = String(name || '').replace(/\\/g, '/').trim();
        const lower = normPath.toLowerCase();
        const isConfigRow = lower.endsWith('.ini') || lower.endsWith('.json') || lower.endsWith('.txt') || lower.endsWith('.toml');
        const isLooseMod = !!(currentMod?.isLoose || currentMod?.type === 'loose' || /^(Disabled\/)?Loose\//i.test(normPath));
        const isPlainBa2Mod = !isLooseMod && lower.endsWith('.ba2') && !currentMod?.isBundle;
        const supportsFiles = !isConfigRow &&
            (isLooseMod || isPlainBa2Mod) &&
            !/^(Disabled\/)?(Bundles|Strings|GameRoot|CoreIni)\//i.test(normPath);
        const mainContainer = document.querySelector('.main-container');
        const pageMode = isConfigRow && !!mainContainer;
        const overlay = document.createElement('div');
        overlay.id = 'rename-mod-overlay';
        overlay.className = pageMode ? 'config-editor-page rename-mod-overlay' : 'modal-overlay active rename-mod-overlay';

        if (isConfigRow) {
            const jsonMode = lower.endsWith('.json');
            const configSpellCheck = !!(window.app?.realData?.managerSettings?.configEditorSpellCheck);
            overlay.dataset.configRelativePath = normPath;
            overlay.innerHTML = `
                <div class="custom-modal polished rename-mod-modal rename-mod-modal-config" role="${pageMode ? 'region' : 'dialog'}" ${pageMode ? '' : 'aria-modal="true"'} aria-label="${escapeHtml(editableName || window._t('mods'))}">
                    <div class="modal-body rename-mod-body">
                        <div class="config-editor-topbar">
                            <div class="config-editor-toprow">
                                ${pageMode ? `<button type="button" class="btn-popup secondary config-editor-back" id="rename-mod-close-top"><i data-lucide="arrow-left"></i> ${escapeHtml(window._t('config_editor_back'))}</button>` : ``}
                                <label class="rename-mod-label" for="rename-mod-input">${escapeHtml(window._t('config_editor_file'))}</label>
                                <input id="rename-mod-input" class="rename-mod-input" type="text" value="${escapeHtml(editableName)}" ${isCoreIni ? 'readonly' : ''} />
                                <div class="mod-config-actions mod-config-actions-inline config-editor-actions">
                                    ${jsonMode ? `
                                    <div class="config-view-toggle mod-config-json-only" role="tablist" aria-label="${escapeAttr(window._t('config_editor_view'))}">
                                        <button type="button" class="config-view-btn" id="mod-config-view-tree" role="tab" aria-selected="false">${escapeHtml(window._t('config_editor_view_tree'))}</button>
                                        <button type="button" class="config-view-btn active" id="mod-config-view-text" role="tab" aria-selected="true">${escapeHtml(window._t('config_editor_view_text'))}</button>
                                    </div>
                                    <button type="button" class="btn-popup secondary mod-config-json-only config-tree-only config-icon-btn" id="mod-config-tree-fold" title="${escapeAttr(window._t('config_editor_collapse_all'))}" aria-label="${escapeAttr(window._t('config_editor_collapse_all'))}"><i data-lucide="chevrons-down-up"></i></button>` : ``}
                                    ${jsonMode ? `<button type="button" class="btn-popup secondary mod-config-json-only" id="mod-config-validate" disabled>${escapeHtml(window._t('config_editor_validate_json'))}</button>` : ``}
                                    ${jsonMode ? `<button type="button" class="btn-popup secondary mod-config-json-only" id="mod-config-format" disabled>${escapeHtml(window._t('config_editor_format_json'))}</button>` : ``}
                                    <button type="button" class="btn-popup secondary config-icon-btn" id="mod-config-font-btn" aria-haspopup="dialog" aria-expanded="false" title="${escapeAttr(window._t('config_editor_font'))}" aria-label="${escapeAttr(window._t('config_editor_font'))}"><i data-lucide="type"></i></button>
                                </div>
                                ${pageMode ? `` : `<button type="button" class="close-status" id="rename-mod-close-top" aria-label="${escapeHtml(window._t('cancel'))}">&times;</button>`}
                            </div>
                        </div>

                        <div class="config-editor-main">
                            <div class="config-editor-pane config-editor-pane-left" role="group" aria-label="${escapeAttr(editableName)}">
                                <div class="mod-config-single config-editor-single">
                                    <div class="mod-config-editor-wrap">
                                        <div id="mod-config-highlight-backdrop"></div>
                                        <textarea id="mod-config-editor" class="rename-mod-details mod-config-editor mod-config-editor-single" placeholder="${escapeAttr(window._t('config_editor_loading'))}" disabled spellcheck="${configSpellCheck}"></textarea>
                                        ${jsonMode ? `<div id="mod-config-json-tree" role="tree" aria-label="${escapeAttr(editableName)}"></div>` : ``}
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>
                    <div class="modal-footer rename-mod-footer">
                        <div id="mod-config-file-status" class="mod-config-status text-muted" aria-live="polite"></div>
                        <span id="mod-config-encoding" class="mod-config-encoding" hidden></span>
                        ${pageMode ? `` : `<button type="button" class="btn-popup secondary" id="rename-mod-cancel">${window._t('cancel')}</button>`}
                        ${isCoreIni ? `` : `<button type="button" class="btn-popup secondary" id="mod-config-rename" disabled><i data-lucide="edit-3"></i> ${escapeHtml(window._t('config_editor_rename'))}</button>`}
                        <button type="button" class="btn-popup primary" id="mod-config-save" disabled title="${escapeAttr(window._t('config_editor_save_tooltip'))}"><i data-lucide="save"></i> ${escapeHtml(window._t('config_editor_save_file'))}</button>
                    </div>
                </div>
            `;
        } else {
            overlay.innerHTML = `
                <div class="custom-modal polished rename-mod-modal" role="dialog" aria-modal="true" aria-label="${escapeHtml(editableName || window._t('mods'))}">
                    <nav class="rename-mod-tabs" role="tablist" aria-label="${escapeAttr(window._t('rename_mod_tabs_label'))}">
                        <button type="button" class="rename-mod-tab active" data-tab="general" role="tab" aria-selected="true" id="rename-mod-tab-general">${escapeHtml(window._t('rename_mod_tab_general'))}</button>
                        <button type="button" class="rename-mod-tab" data-tab="nexus" role="tab" aria-selected="false" id="rename-mod-tab-nexus">${escapeHtml(window._t('rename_mod_tab_nexus'))}</button>
                        ${supportsFiles ? `<button type="button" class="rename-mod-tab" data-tab="files" role="tab" aria-selected="false" id="rename-mod-tab-files">${escapeHtml(window._t('rename_mod_tab_files'))}</button>` : ``}
                        <button type="button" class="close-status" id="rename-mod-close-top" aria-label="${escapeHtml(window._t('cancel'))}">&times;</button>
                    </nav>
                    <div class="rename-mod-name-row">
                        <label class="rename-mod-label" for="rename-mod-input">${window._t('rename_mod_name_label')}</label>
                        <input id="rename-mod-input" class="rename-mod-input" type="text" value="${escapeHtml(editableName)}" />
                    </div>
                    <div class="modal-body rename-mod-body">
                        <div class="rename-mod-tab-panel active" data-panel="general" role="tabpanel" aria-labelledby="rename-mod-tab-general">
                            <label class="rename-mod-label" for="rename-mod-details">${window._t('rename_mod_details_label')}</label>
                            <textarea id="rename-mod-details" class="rename-mod-details" placeholder="${escapeHtml(window._t('rename_mod_details_placeholder'))}">${escapeHtml(existingDetails)}</textarea>
                        </div>
                        <div class="rename-mod-tab-panel" data-panel="nexus" role="tabpanel" aria-labelledby="rename-mod-tab-nexus" hidden>
                            <p class="rename-mod-type-hint rename-mod-tab-lead">${escapeHtml(window._t('rename_mod_nexus_hint'))}</p>
                            <label class="rename-mod-label" for="rename-mod-nexus-url">${window._t('rename_mod_nexus_url_label')}</label>
                            <input id="rename-mod-nexus-url" class="rename-mod-input" type="text" placeholder="${escapeAttr(window._t('rename_mod_nexus_url_placeholder'))}" value="${escapeAttr(currentMod?.url || '')}" />
                            <label class="rename-mod-label" for="rename-mod-nexus-mod-id">${window._t('rename_mod_nexus_mod_id_label')}</label>
                            <div class="rename-mod-nexus-id-row">
                                <input id="rename-mod-nexus-mod-id" class="rename-mod-input" type="text" inputmode="numeric" placeholder="12345" value="${escapeAttr(currentMod?.nexusModId != null ? String(currentMod.nexusModId) : '')}" />
                                <button type="button" class="btn-popup secondary rename-mod-nexus-fetch" id="rename-mod-nexus-fetch" title="${escapeAttr(window._t('rename_mod_nexus_fetch') || 'Fetch from Nexus')}">
                                    <i data-lucide="download-cloud"></i>
                                    <span>${escapeHtml(window._t('rename_mod_nexus_fetch') || 'Fetch from Nexus')}</span>
                                </button>
                            </div>
                            <p class="rename-mod-type-hint" id="rename-mod-nexus-fetch-status" hidden></p>
                            <label class="rename-mod-label" for="rename-mod-nexus-version">${window._t('rename_mod_nexus_version_label')}</label>
                            <input id="rename-mod-nexus-version" class="rename-mod-input" type="text" placeholder="1.0" value="${escapeAttr(currentMod?.nexusFileVersion || currentMod?.version || '')}" />
                            <label class="rename-mod-label" for="rename-mod-nexus-file-id">${window._t('rename_mod_nexus_file_id_label')}</label>
                            <input id="rename-mod-nexus-file-id" class="rename-mod-input" type="text" inputmode="numeric" placeholder="${escapeAttr(window._t('rename_mod_nexus_file_id_placeholder'))}" value="${escapeAttr(currentMod?.nexusFileId != null ? String(currentMod.nexusFileId) : '')}" />
                            <label class="rename-mod-label" for="rename-mod-nexus-uploaded">${window._t('rename_mod_nexus_uploaded_label')}</label>
                            <input id="rename-mod-nexus-uploaded" class="rename-mod-input" type="text" inputmode="numeric" placeholder="${escapeAttr(window._t('rename_mod_nexus_uploaded_placeholder'))}" value="${escapeAttr(currentMod?.nexusFileUploaded != null ? String(currentMod.nexusFileUploaded) : '')}" />
                            <p class="rename-mod-type-hint">${escapeHtml(window._t('rename_mod_nexus_file_id_hint'))}</p>
                        </div>
                        ${supportsFiles ? `
                        <div class="rename-mod-tab-panel" data-panel="files" role="tabpanel" aria-labelledby="rename-mod-tab-files" hidden>
                            <label class="rename-mod-label">${escapeHtml(window._t('rename_mod_files_legend'))}</label>
                            <div class="rename-mod-files-actions">
                                <button type="button" class="btn-popup secondary" id="rename-mod-files-import-archive"><i data-lucide="file-archive"></i> ${escapeHtml(window._t('rename_mod_files_import_archive'))}</button>
                                <button type="button" class="btn-popup secondary" id="rename-mod-files-import-folder"><i data-lucide="folder-open"></i> ${escapeHtml(window._t('rename_mod_files_import_folder'))}</button>
                                <div class="rename-mod-files-hint-wrap">
                                    <button type="button" class="rename-mod-files-hint-icon" id="rename-mod-files-hint-btn" aria-expanded="false" aria-controls="rename-mod-files-hint-popover" aria-label="${escapeAttr(window._t('rename_mod_files_drop_hint'))}"><i data-lucide="alert-triangle"></i></button>
                                    <div class="rename-mod-files-hint-popover" id="rename-mod-files-hint-popover" role="tooltip" hidden>${escapeHtml(window._t('rename_mod_files_drop_hint'))}</div>
                                </div>
                            </div>
                            <div class="rename-mod-files-dropzone" id="rename-mod-files-dropzone">
                                <i data-lucide="download"></i>
                            </div>
                            <p class="rename-mod-type-hint rename-mod-files-status" id="rename-mod-files-status" aria-live="polite" hidden></p>
                            <label class="rename-mod-label" id="rename-mod-files-count">${escapeHtml(window._t('rename_mod_files_current'))}</label>
                            <ul class="rename-mod-files-list" id="rename-mod-files-list"><li class="text-muted">${escapeHtml(window._t('rename_mod_files_loading'))}</li></ul>
                        </div>` : ``}
                    </div>
                    <div class="modal-footer rename-mod-footer">
                        <button type="button" class="btn-popup secondary" id="rename-mod-cancel">${window._t('cancel')}</button>
                        <button type="button" class="btn-popup primary" id="rename-mod-confirm"><i data-lucide="save"></i> ${window._t('rename_mod_save')}</button>
                    </div>
                </div>
            `;
        }

        if (pageMode) {
            overlay.dataset.originSection = window.app?.currentSection || '';
            mainContainer.appendChild(overlay);
            mainContainer.classList.add('config-editor-open');
            const titleEl = document.getElementById('section-title');
            if (titleEl) titleEl.textContent = String(editableName || '').split('/').pop() || editableName;
        } else {
            document.body.appendChild(overlay);
        }
        if (window.lucide) {
            try { window.lucide.createIcons({ nodes: [overlay] }); } catch (_) {
                try { window.lucide.createIcons(); } catch (__) { }
            }
        }

        if (!isConfigRow) {
            const tabBtns = overlay.querySelectorAll('.rename-mod-tab');
            const tabPanels = overlay.querySelectorAll('.rename-mod-tab-panel');
            const switchRenameTab = (tabId) => {
                tabBtns.forEach((btn) => {
                    const active = btn.dataset.tab === tabId;
                    btn.classList.toggle('active', active);
                    btn.setAttribute('aria-selected', active ? 'true' : 'false');
                });
                tabPanels.forEach((panel) => {
                    const show = panel.dataset.panel === tabId;
                    panel.classList.toggle('active', show);
                    panel.hidden = !show;
                });
                this._setModFileDropTarget(tabId === 'files' && supportsFiles ? name : null);
                if (tabId === 'files' && supportsFiles && !contentsRequested) {
                    contentsRequested = true;
                    window.chrome?.webview?.postMessage?.({ type: 'LIST_MOD_CONTENTS', name });
                }
            };
            let contentsRequested = false;
            tabBtns.forEach((btn) => {
                btn.addEventListener('click', () => switchRenameTab(btn.dataset.tab));
            });
            let tabToOpen = 'general';
            if (initialTab === 'nexus') tabToOpen = 'nexus';
            else if (initialTab === 'files' && supportsFiles) tabToOpen = 'files';
            switchRenameTab(tabToOpen);

            if (supportsFiles) {
                overlay.dataset.filesModName = name;
                const requestAddFiles = (mode) => {
                    window.chrome?.webview?.postMessage?.({ type: 'ADD_FILES_TO_MOD', name, mode });
                };
                document.getElementById('rename-mod-files-import-archive')
                    ?.addEventListener('click', () => requestAddFiles('archive'));
                document.getElementById('rename-mod-files-import-folder')
                    ?.addEventListener('click', () => requestAddFiles('folder'));

                const hintBtn = document.getElementById('rename-mod-files-hint-btn');
                const hintPopover = document.getElementById('rename-mod-files-hint-popover');
                const setHintOpen = (open) => {
                    if (!hintBtn || !hintPopover) return;
                    hintPopover.hidden = !open;
                    hintBtn.setAttribute('aria-expanded', String(open));
                    hintBtn.classList.toggle('active', open);
                };
                hintBtn?.addEventListener('click', (e) => {
                    e.stopPropagation();
                    setHintOpen(hintPopover.hidden);
                });
                overlay.addEventListener('click', (e) => {
                    if (hintPopover && !hintPopover.hidden && !e.target.closest('.rename-mod-files-hint-wrap')) setHintOpen(false);
                });
                overlay.addEventListener('keydown', (e) => {
                    if (e.key === 'Escape' && hintPopover && !hintPopover.hidden) {
                        e.stopPropagation();
                        setHintOpen(false);
                        hintBtn?.focus();
                    }
                }, true);
            }
        }

        const cancelBtn = document.getElementById('rename-mod-cancel');
        const closeTopBtn = document.getElementById('rename-mod-close-top');
        const confirmBtn = document.getElementById('rename-mod-confirm');
        const renameInput = document.getElementById('rename-mod-input');
        const detailsInput = document.getElementById('rename-mod-details');
        const nexusUrlInput = document.getElementById('rename-mod-nexus-url');
        const nexusModIdInput = document.getElementById('rename-mod-nexus-mod-id');
        const nexusVersionInput = document.getElementById('rename-mod-nexus-version');
        const nexusFileIdInput = document.getElementById('rename-mod-nexus-file-id');
        const nexusUploadedInput = document.getElementById('rename-mod-nexus-uploaded');
        const nexusFetchBtn = document.getElementById('rename-mod-nexus-fetch');
        const nexusFetchStatus = document.getElementById('rename-mod-nexus-fetch-status');
        const initialNexusUrl = (currentMod?.url || '').trim();
        const initialNexusModId = currentMod?.nexusModId != null ? String(currentMod.nexusModId) : '';
        const initialNexusVersion = (currentMod?.nexusFileVersion || currentMod?.version || '').trim();
        const initialNexusFileId = currentMod?.nexusFileId != null ? String(currentMod.nexusFileId) : '';
        const initialNexusUploaded = currentMod?.nexusFileUploaded != null ? String(currentMod.nexusFileUploaded) : '';
        this._pendingNexusResolve = null;
        const configSelect = document.getElementById('mod-config-file-select');
        const configStatus = document.getElementById('mod-config-file-status');
        const configEditor = document.getElementById('mod-config-editor');
        const configBackdrop = document.getElementById('mod-config-highlight-backdrop');
        const configValidate = document.getElementById('mod-config-validate');
        const configFormat = document.getElementById('mod-config-format');
        const configSave = document.getElementById('mod-config-save');
        const configRename = document.getElementById('mod-config-rename');

        const closeAction = () => this.hideRenameModal();
        const parseNexusModIdFromUrl = (url) => {
            const m = String(url || '').match(/nexusmods\.com\/fallout76\/mods\/(\d+)/i);
            return m ? parseInt(m[1], 10) : null;
        };

        const parseOptionalLong = (raw) => {
            const n = parseInt(String(raw || '').trim(), 10);
            return Number.isFinite(n) && n > 0 ? n : null;
        };

        const getNexusFieldInputs = () => {
            const url = nexusUrlInput ? String(nexusUrlInput.value || '').trim() : '';
            const modIdRaw = nexusModIdInput ? parseInt(String(nexusModIdInput.value || '').trim(), 10) : NaN;
            const modIdEntered = Number.isFinite(modIdRaw) && modIdRaw > 0 ? modIdRaw : null;
            const version = nexusVersionInput ? String(nexusVersionInput.value || '').trim() : '';
            const fileId = nexusFileIdInput ? parseOptionalLong(nexusFileIdInput.value) : null;
            const uploaded = nexusUploadedInput ? parseOptionalLong(nexusUploadedInput.value) : null;
            let modId = modIdEntered;
            if (!modId && url) {
                modId = parseNexusModIdFromUrl(url);
            }
            return { url, modIdEntered, modId, version, fileId, uploaded };
        };

        const setNexusFetchStatus = (text, isError = false) => {
            if (!nexusFetchStatus) return;
            if (!text) {
                nexusFetchStatus.hidden = true;
                nexusFetchStatus.textContent = '';
                return;
            }
            nexusFetchStatus.hidden = false;
            nexusFetchStatus.textContent = text;
            nexusFetchStatus.classList.toggle('rename-mod-nexus-fetch-status--error', !!isError);
        };

        if (nexusFetchBtn) {
            nexusFetchBtn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                const loggedIn = !!(window.app?.realData?.managerSettings?.nexusLoggedIn
                    ?? window.app?.realData?.nexusLoggedIn);
                if (!loggedIn) {
                    setNexusFetchStatus(window._t('rename_mod_nexus_fetch_login') || 'Log in to Nexus Mods in Settings first.', true);
                    return;
                }
                const fields = getNexusFieldInputs();
                if (!fields.modId) {
                    setNexusFetchStatus(window._t('rename_mod_nexus_fetch_need_id') || 'Enter a Nexus URL or mod ID first.', true);
                    return;
                }
                nexusFetchBtn.disabled = true;
                setNexusFetchStatus(window._t('rename_mod_nexus_fetching') || 'Fetching from Nexus…');
                this._pendingNexusResolve = {
                    modId: fields.modId,
                    apply: (payload) => {
                        if (payload?.ok) {
                            if (nexusModIdInput && payload.nexusModId) nexusModIdInput.value = String(payload.nexusModId);
                            if (nexusUrlInput && payload.url) nexusUrlInput.value = String(payload.url);
                            if (nexusFileIdInput && payload.nexusFileId) nexusFileIdInput.value = String(payload.nexusFileId);
                            if (nexusVersionInput && payload.nexusFileVersion) nexusVersionInput.value = String(payload.nexusFileVersion);
                            if (nexusUploadedInput && payload.nexusFileUploaded != null) nexusUploadedInput.value = String(payload.nexusFileUploaded);
                            updateConfirmState();

                            const nexusNow = getNexusFieldInputs();
                            const nexusRemove = buildNexusRemoveFlags(nexusNow);
                            const clearNexusLink = false;
                            const metaPayload = {
                                nexusUrl: nexusNow.url || payload.url || '',
                                nexusFileVersion: nexusNow.version || payload.nexusFileVersion || '',
                                clearNexusLink
                            };
                            if (Object.keys(nexusRemove).length) metaPayload.nexusRemove = nexusRemove;
                            if (nexusNow.modId || payload.nexusModId) metaPayload.nexusModId = nexusNow.modId || payload.nexusModId;
                            if (nexusNow.fileId || payload.nexusFileId) metaPayload.nexusFileId = nexusNow.fileId || payload.nexusFileId;
                            if (nexusNow.uploaded != null || payload.nexusFileUploaded != null) {
                                metaPayload.nexusFileUploaded = nexusNow.uploaded ?? payload.nexusFileUploaded;
                            }

                            this._applyLocalNexusPatch(name, nexusNow, nexusRemove, clearNexusLink);
                            this._applyUpdateFlagsToVisibleRows();
                            window.chrome?.webview?.postMessage?.({
                                type: 'UPDATE_MOD_METADATA',
                                originalName: name,
                                metadata: metaPayload
                            });

                            if (payload.hasUpdate && payload.nexusModId && payload.nexusFileId) {
                                setNexusFetchStatus(window._t('rename_mod_nexus_fetch_updating') || 'Downloading latest file from Nexus…');
                                window.chrome?.webview?.postMessage?.({
                                    type: 'UPDATE_MOD_FROM_NEXUS',
                                    originalName: name,
                                    modId: payload.nexusModId,
                                    fileId: payload.nexusFileId,
                                    fileName: payload.fileName || name,
                                    fileVersion: payload.nexusFileVersion || '',
                                    fileUploaded: payload.nexusFileUploaded ?? null
                                });
                                closeAction();
                            } else if (payload.nexusFileId) {
                                setNexusFetchStatus(window._t('rename_mod_nexus_fetch_uptodate') || 'Already up to date with this Nexus file.');
                            } else {
                                setNexusFetchStatus(window._t('rename_mod_nexus_fetch_ok') || 'Nexus link saved.');
                            }
                        } else {
                            setNexusFetchStatus(payload?.error || window._t('rename_mod_nexus_fetch_failed') || 'Could not fetch Nexus data.', true);
                        }
                        nexusFetchBtn.disabled = false;
                    }
                };
                window.chrome?.webview?.postMessage?.({
                    type: 'NEXUS_RESOLVE_MOD_LINK',
                    modId: fields.modId,
                    url: fields.url || '',
                    fileId: fields.fileId || null,
                    version: fields.version || ''
                });
            });
        }

        const buildNexusRemoveFlags = (fields) => {
            const remove = {};
            if (initialNexusUrl && !fields.url) remove.url = true;
            if (initialNexusModId && !fields.modIdEntered) remove.modId = true;
            if (initialNexusFileId && !fields.fileId) remove.fileId = true;
            if (initialNexusUploaded && !fields.uploaded) remove.uploaded = true;
            if (initialNexusVersion && !fields.version) remove.version = true;
            return remove;
        };

        const submitAction = () => {
            const newName = renameInput.value;
            const detailsValue = detailsInput ? detailsInput.value : '';
            const hasNameChanged = newName !== editableName;
            const hasDetailsChanged = detailsValue !== existingDetails;
            const nexusNow = getNexusFieldInputs();
            const nexusRemove = buildNexusRemoveFlags(nexusNow);
            const hasNexusChanged =
                nexusNow.url !== initialNexusUrl ||
                String(nexusNow.modIdEntered || '') !== initialNexusModId ||
                nexusNow.version !== initialNexusVersion ||
                String(nexusNow.fileId || '') !== initialNexusFileId ||
                String(nexusNow.uploaded || '') !== initialNexusUploaded;
            if (!newName || !newName.trim() || (!hasNameChanged && !hasDetailsChanged && !hasNexusChanged)) return;

            if (hasNexusChanged) {
                const clearNexusLink =
                    !nexusNow.modIdEntered && !nexusNow.url && !nexusNow.fileId && !nexusNow.uploaded;
                const metaPayload = {
                    nexusUrl: nexusNow.url,
                    nexusFileVersion: nexusNow.version,
                    clearNexusLink
                };
                if (Object.keys(nexusRemove).length) metaPayload.nexusRemove = nexusRemove;
                if (nexusNow.modId) metaPayload.nexusModId = nexusNow.modId;
                if (nexusNow.fileId) metaPayload.nexusFileId = nexusNow.fileId;
                if (nexusNow.uploaded) metaPayload.nexusFileUploaded = nexusNow.uploaded;
                this._applyLocalNexusPatch(name, nexusNow, nexusRemove, clearNexusLink);
                this._applyUpdateFlagsToVisibleRows();
                window.chrome.webview.postMessage({
                    type: 'UPDATE_MOD_METADATA',
                    originalName: name,
                    metadata: metaPayload
                });
                setTimeout(() => this.requestModUpdateCheck(), 800);
            }

            if (hasNameChanged) {
                this._applyLocalModDetailsPatch(name, detailsValue);
                this._pendingModDetailsByFileKey.set(String(this._modFileKeyFromOriginalName(newName)).toLowerCase(), detailsValue);
                window.chrome.webview.postMessage({
                    type: 'RENAME_MOD',
                    currentName: name,
                    newName: newName,
                    details: detailsValue
                });
            } else if (hasDetailsChanged) {
                this._applyLocalModDetailsPatch(name, detailsValue);
                this._pendingModDetailsByFileKey.set(String(currentFileKey).toLowerCase(), detailsValue);
                window.chrome.webview.postMessage({
                    type: 'SAVE_MOD_DETAILS',
                    name: name,
                    details: detailsValue
                });
            }

            closeAction();
        };
        const updateConfirmState = () => {
            if (!confirmBtn) return;
            const newName = renameInput.value;
            const detailsValue = detailsInput ? detailsInput.value : '';
            const hasNameChanged = newName !== editableName;
            const hasDetailsChanged = detailsValue !== existingDetails;
            const nexusNow = getNexusFieldInputs();
            const hasNexusChanged =
                nexusNow.url !== initialNexusUrl ||
                String(nexusNow.modIdEntered || '') !== initialNexusModId ||
                nexusNow.version !== initialNexusVersion ||
                String(nexusNow.fileId || '') !== initialNexusFileId ||
                String(nexusNow.uploaded || '') !== initialNexusUploaded;
            const isDisabled = !newName || !newName.trim() || (!hasNameChanged && !hasDetailsChanged && !hasNexusChanged);
            confirmBtn.disabled = isDisabled;
            confirmBtn.classList.toggle('disabled', isDisabled);
        };

        const setConfigStatus = (text, level = 'neutral') => {
            if (!configStatus) return;
            const t = text ?? '';
            configStatus.textContent = t;
            configStatus.className = 'mod-config-status';
            if (level === 'error') configStatus.classList.add('mod-config-status--error');
            else if (level === 'success') configStatus.classList.add('mod-config-status--success');
            else configStatus.classList.add('text-muted');
        };

        const updateConfigHighlighting = () => {
            if (!configBackdrop || !configEditor) return;
            const isJson = String(getCurrentConfigRelativePath()).toLowerCase().endsWith('.json');
            configBackdrop.innerHTML = this._renderConfigHighlight(configEditor.value, isJson) + '\n';
            configBackdrop.scrollTop = configEditor.scrollTop;
            this._syncConfigEditorBackdropPad(overlay);
            this._applyConfigFindMarks?.(overlay);
        };
        this._refreshConfigHighlight = updateConfigHighlighting;

        const isValidConfigFileBaseName = (value) => {
            const v = String(value ?? '').trim();
            if (!v) return { ok: false, value: '', error: window._t('config_editor_name_required') };
            if (/[\\/]/.test(v)) return { ok: false, value: v, error: window._t('config_editor_name_no_folders') };
            return { ok: true, value: v, error: '' };
        };

        const updateConfigRenameState = () => {
            if (!isConfigRow || !configRename || !renameInput) return;
            const next = String(renameInput.value ?? '');
            const parsed = isValidConfigFileBaseName(next);
            const changed = parsed.ok && parsed.value !== String(editableName || '');
            const disabled = !changed;
            configRename.disabled = disabled;
            configRename.classList.toggle('disabled', disabled);
        };

        const isJsonPath = (p) => String(p || '').toLowerCase().endsWith('.json');
        const getCurrentConfigRelativePath = () =>
            (configSelect?.value || overlay?.dataset?.configRelativePath || normPath || '');
        const enableConfigButtons = (enabled) => {
            if (configSave) configSave.disabled = !enabled;
            const jsonMode = enabled && isJsonPath(getCurrentConfigRelativePath());
            if (configValidate) configValidate.disabled = !jsonMode;
            if (configFormat) configFormat.disabled = !jsonMode;

            const overlay = document.getElementById('rename-mod-overlay');
            if (overlay) {
                overlay.querySelectorAll('.mod-config-json-only').forEach((el) => {
                    el.style.display = jsonMode ? '' : 'none';
                });
            }
        };

        const isConfigLikePath = (p) => {
            const lp = String(p || '').toLowerCase();
            return lp.endsWith('.ini') || lp.endsWith('.json') || lp.endsWith('.txt') || lp.endsWith('.toml');
        };

        const findOwningModsForRelativePath = (relPath) => {
            const rel = String(relPath || '').replace(/\\/g, '/').trim().toLowerCase();
            const mods = (window.app && window.app.realData && Array.isArray(window.app.realData.mods))
                ? window.app.realData.mods
                : [];
            return mods
                .filter(m => m && !isConfigLikePath(m.originalName))
                .filter(m => {
                    const mKey = String(m.originalName || '').replace(/\\/g, '/').trim().toLowerCase();
                    if (!mKey || mKey === rel) return false;
                    const files = Array.isArray(m.files) ? m.files : [];
                    return files.some(f => String(f || '').replace(/\\/g, '/').trim().toLowerCase() === rel);
                })
                .map(m => ({
                    key: String(m.originalName || m.name || '').trim(),
                    label: String(m.name || m.originalName || '').replace(/^Disabled\//i, ''),
                }))
                .filter(m => m.key);
        };

        if (configSelect && configEditor) {
            configSelect.addEventListener('change', () => {
                const rel = configSelect.value || '';
                configEditor.value = '';
                this._configBaseline = null;
                configEditor.disabled = true;
                enableConfigButtons(false);
                updateConfigHighlighting();
                if (!rel) {
                    setConfigStatus('');
                    return;
                }
                setConfigStatus(window._t('config_editor_loading'));
                window.chrome.webview.postMessage({ type: 'READ_MOD_FILE', name: name, relativePath: rel });
            });
            enableConfigButtons(false);
        }

        if (configValidate && configEditor) {
            configValidate.addEventListener('click', () => {
                try {
                    const parsed = this._parseJsonRelaxed(configEditor.value || '');
                    if (parsed.ok) setConfigStatus(window._t('config_editor_json_valid'), 'success');
                    else setConfigStatus(window._t('config_editor_json_invalid', parsed.error), 'error');
                } catch (e) {
                    setConfigStatus(window._t('config_editor_json_invalid', e.message), 'error');
                }
            });
        }

        if (configFormat && configEditor) {
            configFormat.addEventListener('click', () => {
                try {
                    const parsed = this._parseJsonRelaxed(configEditor.value || '');
                    if (!parsed.ok) {
                        setConfigStatus(window._t('config_editor_json_invalid', parsed.error), 'error');
                        return;
                    }
                    configEditor.value = jsonc.format(configEditor.value || '', 2);
                    updateConfigHighlighting();
                    setConfigStatus(window._t('config_editor_json_formatted'), 'success');
                } catch (e) {
                    setConfigStatus(window._t('config_editor_json_invalid', e.message), 'error');
                }
            });
        }

        this._configJsonTree?.destroy();
        this._configJsonTree = null;
        this._onConfigContentLoaded = null;
        const configTreeEl = document.getElementById('mod-config-json-tree');
        if (isConfigRow && configTreeEl && configEditor) {
            const viewTreeBtn = document.getElementById('mod-config-view-tree');
            const viewTextBtn = document.getElementById('mod-config-view-text');
            const foldBtn = document.getElementById('mod-config-tree-fold');
            const tree = new JsonTreeEditor(configTreeEl, {
                onChange: (value, edit) => {
                    const current = configEditor.value || '';
                    const patched = edit ? jsonc.applyEdit(current, edit) : null;
                    if (patched !== null) {
                        configEditor.value = patched;
                        tree.setComments(jsonc.commentMap(patched));
                        setConfigStatus('');
                    } else if (jsonc.hasComments(current)) {
                        tree.setValue(jsonc.parse(current), jsonc.commentMap(current));
                        syncFoldButton();
                        setConfigStatus(window._t('config_editor_tree_comment_conflict'), 'error');
                        return;
                    } else {
                        configEditor.value = JSON.stringify(value, null, 2);
                        tree.setComments(new Map());
                    }
                    updateConfigHighlighting();
                }
            });
            this._configJsonTree = tree;

            const syncFoldButton = () => {
                if (!foldBtn) return;
                const expanded = tree.allExpanded;
                const label = window._t(expanded ? 'config_editor_collapse_all' : 'config_editor_expand_all');
                foldBtn.title = label;
                foldBtn.setAttribute('aria-label', label);
                foldBtn.innerHTML = `<i data-lucide="${expanded ? 'chevrons-down-up' : 'chevrons-up-down'}"></i>`;
                try { window.lucide?.createIcons({ nodes: [foldBtn] }); } catch (_) { }
            };

            const setConfigView = (view) => {
                if (view === 'tree') {
                    const text = configEditor.value || '';
                    const parsed = this._parseJsonRelaxed(text);
                    if (!parsed.ok) {
                        setConfigStatus(window._t('config_editor_json_invalid', parsed.error), 'error');
                        view = 'text';
                    } else {
                        tree.setValue(parsed.value, jsonc.commentMap(text));
                        syncFoldButton();
                    }
                }
                overlay.dataset.configView = view;
                viewTreeBtn?.classList.toggle('active', view === 'tree');
                viewTreeBtn?.setAttribute('aria-selected', String(view === 'tree'));
                viewTextBtn?.classList.toggle('active', view === 'text');
                viewTextBtn?.setAttribute('aria-selected', String(view === 'text'));
                if (configFormat) configFormat.disabled = view === 'tree' || configEditor.disabled;
                if (view === 'text') {
                    updateConfigHighlighting();
                    requestAnimationFrame(() => this._syncConfigEditorBackdropPad(overlay));
                } else {
                    this._refreshConfigFind?.();
                }
                return view;
            };

            viewTreeBtn?.addEventListener('click', () => {
                if (!configEditor.disabled) setConfigView('tree');
            });
            viewTextBtn?.addEventListener('click', () => setConfigView('text'));
            foldBtn?.addEventListener('click', () => {
                tree.toggleAll();
                syncFoldButton();
            });

            this._onConfigContentLoaded = (isJson) => {
                setConfigView(isJson ? 'tree' : 'text');
            };
        }

        if (configSave && configEditor) {
            configSave.addEventListener('click', () => {
                const rel = getCurrentConfigRelativePath();
                if (!rel) return;
                setConfigStatus(window._t('config_editor_saving'));
                this._configPendingSave = configEditor.value ?? '';
                window.chrome.webview.postMessage({
                    type: 'WRITE_MOD_FILE',
                    name: name,
                    relativePath: rel,
                    content: configEditor.value ?? '',
                    applyToIni: isAddToOverlay,
                    encoding: overlay.dataset.configEncoding || 'utf-8'
                });
            });
        }

        if (configRename && renameInput) {
            const doRename = () => {
                const parsed = isValidConfigFileBaseName(renameInput.value);
                if (!parsed.ok) {
                    setConfigStatus(parsed.error, 'error');
                    return;
                }
                if (parsed.value === String(editableName || '')) return;
                setConfigStatus(window._t('config_editor_renaming'));
                window.chrome.webview.postMessage({
                    type: 'RENAME_MOD',
                    currentName: name,
                    newName: parsed.value,
                    details: ''
                });
                closeAction();
            };
            configRename.addEventListener('click', doRename);
            renameInput.addEventListener('input', () => {
                const parsed = isValidConfigFileBaseName(renameInput.value);
                if (parsed.ok) setConfigStatus('');
                updateConfigRenameState();
            });
            updateConfigRenameState();
        }


        if (isConfigRow && configEditor)
        {
            setConfigStatus(window._t('config_editor_loading'));
            window.chrome.webview.postMessage({ type: 'READ_MOD_FILE', name: name, relativePath: normPath });
        }

        if (configEditor) {
            configEditor.addEventListener('input', () => updateConfigHighlighting());
            updateConfigHighlighting();
        }
        if (isConfigRow) {
            this._setupConfigEditorLayout(overlay);
            this._mountConfigFontPopover(overlay);
        }

        if (cancelBtn) cancelBtn.onclick = closeAction;
        if (closeTopBtn) closeTopBtn.onclick = pageMode ? () => { void this.requestCloseConfigEditor(); } : closeAction;
        if (confirmBtn) confirmBtn.onclick = submitAction;
        if (renameInput && confirmBtn) renameInput.oninput = updateConfirmState;
        if (detailsInput) detailsInput.oninput = updateConfirmState;
        if (nexusUrlInput) nexusUrlInput.oninput = updateConfirmState;
        if (nexusModIdInput) nexusModIdInput.oninput = updateConfirmState;
        if (nexusVersionInput) nexusVersionInput.oninput = updateConfirmState;
        if (nexusFileIdInput) nexusFileIdInput.oninput = updateConfirmState;
        if (nexusUploadedInput) nexusUploadedInput.oninput = updateConfirmState;

        if (!pageMode) {
            overlay.addEventListener('click', (e) => {
                if (e.target === overlay) closeAction();
            });
        }

        this.renameModalKeyHandler = (e) => {
            if (overlay.classList.contains('config-editor-parked')) return;
            if (pageMode && document.querySelector('#confirm-modal.active')) return;
            if (e.key === 'Escape') {
                if (this._closeConfigFind?.()) {
                    e.preventDefault();
                    return;
                }
                e.preventDefault();
                if (pageMode) void this.requestCloseConfigEditor();
                else closeAction();
                return;
            }
            if (isConfigRow && (e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && String(e.key).toLowerCase() === 's') {
                e.preventDefault();
                const active = document.activeElement;
                if (active && active.classList && active.classList.contains('jt-input')) active.blur();
                if (configSave && !configSave.disabled) configSave.click();
                return;
            }
            const ae = document.activeElement;
            if (ae && ae.closest && ae.closest('.config-find-bar')) return;
            const inTabs = ae && ae.closest && ae.closest('.rename-mod-tabs');
            const isMultilineEditor = ae && (
                ae === detailsInput ||
                ae === configEditor ||
                ae.tagName === 'TEXTAREA' ||
                ae.isContentEditable
            );
            if (pageMode && !(ae && overlay.contains(ae))) return;
            if (e.key === 'Enter' && !isMultilineEditor && !inTabs) {
                e.preventDefault();
                submitAction();
            }
        };
        document.addEventListener('keydown', this.renameModalKeyHandler);

        updateConfirmState();
        if (renameInput) {
            renameInput.focus();
            renameInput.select();
        }
    }

    updateModStatus(name, isEnabled) {
        const order = Array.from(document.querySelectorAll('.mod-row'))
            .filter(tr => {
                const cb = tr.querySelector('.mod-select');
                return cb && cb.checked;
            })
            .map(tr => tr.dataset.name);

        window.chrome.webview.postMessage({ type: 'UPDATE_MOD_ORDER', order: order });
    }

    filterModRowsInPlace(searchTerm) {
        const rows = document.querySelectorAll('.mod-row');
        const term = (searchTerm || '').trim().toLowerCase();
        rows.forEach(row => {
            const searchText = (row.dataset.searchText || '').toLowerCase();
            const show = !term || searchText.includes(term);
            row.style.display = show ? '' : 'none';
            const parentName = row.dataset.name;
            if (parentName) {
                document.querySelectorAll('.mod-loose-files-row').forEach((child) => {
                    if (child.dataset.parent === parentName) {
                        child.style.display = show ? '' : 'none';
                    }
                });
            }
        });
        const selectAll = document.getElementById('select-all-mods');
        if (selectAll) {
            const visibleRows = Array.from(rows).filter(r => r.style.display !== 'none');
            const visibleChecked = visibleRows.filter(r => r.querySelector('.mod-select')?.checked);
            selectAll.checked = visibleRows.length > 0 && visibleChecked.length === visibleRows.length;
            selectAll.indeterminate = visibleChecked.length > 0 && visibleChecked.length < visibleRows.length;
        }
    }

    refreshUI() {
        if (window.app && window.app.realData) {
            const activeId = document.activeElement ? document.activeElement.id : null;
            const selectionStart = activeId === 'mods-search' ? document.activeElement.selectionStart : 0;
            const selectionEnd = activeId === 'mods-search' ? document.activeElement.selectionEnd : 0;
            
            const container = document.querySelector('.mods-table-container');
            const scrollTop = container ? container.scrollTop : 0;

            const contentArea = document.getElementById('content-area');
            if (contentArea) {
                contentArea.classList.add('content-refreshing');
                contentArea.innerHTML = `<div class="section-content">${this.render(window.app.realData)}</div>`;
                this.onMount(window.app.realData);
                requestAnimationFrame(() => {
                    requestAnimationFrame(() => contentArea.classList.remove('content-refreshing'));
                });
            }

            const newContainer = document.querySelector('.mods-table-container');
            if (newContainer) newContainer.scrollTop = scrollTop;

            if (activeId === 'mods-search') {
                const searchInput = document.getElementById('mods-search');
                if (searchInput) {
                    searchInput.focus();
                    searchInput.setSelectionRange(selectionStart, selectionEnd);
                }
            }
        }
    }

    updateValues(data) {
        const priorData = this.data;
        this._clearPendingDetailsIfServerHasData(data);
        if (data?.mods) this._mergeModUpdateFlags(data.mods);

        if (_pendingDeleteName && Array.isArray(data?.mods)) {
            const stillThere = data.mods.some(m =>
                String(m?.originalName || '') === _pendingDeleteName
            );
            if (!stillThere) clearDeleteModInFlight();
        }

        const modSig = (mods) => (mods || [])
            .map(m => [
                String(m?.originalName || ''),
                String(m?.type || ''),
                m?.isLoose ? '1' : '0',
                String(m?.status || ''),
                Array.isArray(m?.files) ? m.files.join(',') : ''
            ].join('|'))
            .sort()
            .join('\n');
        const contentChanged = modSig(priorData?.mods) !== modSig(data?.mods);

        if (!contentChanged && this.tryInPlaceUpdate(data)) {
            this._applyUpdateFlagsToVisibleRows();
            if (this._shouldRecheckModUpdates(priorData, data)) {
                this.scheduleModUpdateCheck();
            }
            return;
        }
        this.data = data;
        this.refreshUI();
    }

    tryInPlaceUpdate(data) {
        if (!this.data) return false;
        
        const cleanName = n => n ? n.replace(/^Disabled\//, '') : '';
        const fileKeyOf = (n) => this._modFileKeyFromOriginalName(n);
        const expected = this.getSortedFilteredMods(data);
        const modsTbody = document.getElementById('mods-list-body');
        const currentRows = modsTbody
            ? Array.from(modsTbody.querySelectorAll(':scope > tr.mod-row'))
            : Array.from(document.querySelectorAll('#mods-list-body .mod-row, .mods-table .mod-row'));
        
        if (expected.length === currentRows.length - 1 && expected.length >= 1) {
            const expectedIdentities = new Set();
            for (const m of expected) {
                expectedIdentities.add(cleanName(m.originalName));
                const fk = fileKeyOf(m.originalName);
                if (fk) expectedIdentities.add(`__fk__${fk.toLowerCase()}`);
            }
            const rowMatchesExpected = (row) => {
                if (expectedIdentities.has(cleanName(row.dataset.name))) return true;
                const rfk = fileKeyOf(row.dataset.name);
                return !!(rfk && expectedIdentities.has(`__fk__${rfk.toLowerCase()}`));
            };
            const unmatched = currentRows.filter((row) => !rowMatchesExpected(row));
            if (unmatched.length === 1 && unmatched[0].parentNode) {
                const removedName = unmatched[0].dataset.name;
                unmatched[0].remove();
                if (removedName) {
                    document.querySelectorAll('.mod-loose-files-row').forEach((child) => {
                        if (child.dataset.parent === removedName) child.remove();
                    });
                }
                this.data = data;
                this.updateDeployButtonText();
                this.updateSelectAllCheckbox(expected);
                return true;
            }
        }

        if (expected.length > currentRows.length) {
            const tbody = modsTbody || document.getElementById('mods-list-body');
            if (!tbody) return false;

            const modMatchesIdentity = (mod, identities) => {
                if (identities.has(cleanName(mod.originalName))) return true;
                const fk = fileKeyOf(mod.originalName);
                return !!(fk && identities.has(`__fk__${fk.toLowerCase()}`));
            };
            const rowMatchesMod = (row, mod) => {
                if (cleanName(row.dataset.name) === cleanName(mod.originalName)) return true;
                const rfk = fileKeyOf(row.dataset.name);
                const mfk = fileKeyOf(mod.originalName);
                return !!(rfk && mfk && rfk.toLowerCase() === mfk.toLowerCase());
            };

            const currentIdentities = new Set();
            for (const row of currentRows) {
                currentIdentities.add(cleanName(row.dataset.name));
                const fk = fileKeyOf(row.dataset.name);
                if (fk) currentIdentities.add(`__fk__${fk.toLowerCase()}`);
            }

            const newMods = expected.filter((m) => !modMatchesIdentity(m, currentIdentities));
            if (newMods.length !== expected.length - currentRows.length) return false;

            const newModsByIndex = newMods
                .map((mod) => ({
                    mod,
                    modIndex: expected.findIndex((m) => rowMatchesMod(
                        { dataset: { name: mod.originalName } },
                        m
                    )),
                }))
                .sort((a, b) => a.modIndex - b.modIndex);

            for (const { mod, modIndex } of newModsByIndex) {
                const temp = document.createElement('tbody');
                temp.innerHTML = ModsRenderer.renderModRow(mod, this).trim();
                const newRows = Array.from(temp.querySelectorAll('tr'));
                if (newRows.length === 0) return false;

                const rows = Array.from(tbody.querySelectorAll(':scope > tr.mod-row'));
                let insertBefore = null;
                for (let i = modIndex + 1; i < expected.length; i++) {
                    const anchor = rows.find((r) => rowMatchesMod(r, expected[i]));
                    if (anchor) {
                        insertBefore = anchor;
                        break;
                    }
                }

                for (const newRow of newRows) {
                    if (insertBefore) tbody.insertBefore(newRow, insertBefore);
                    else tbody.appendChild(newRow);
                }
                if (window.lucide) window.lucide.createIcons({ nodes: newRows });
            }

            this.data = data;
            this.filterModRowsInPlace((this.lastSearchTerm || '').trim().toLowerCase());
            this.updateDeployButtonText();
            this.updateSelectAllCheckbox(expected);
            if (newMods.some(m => m.nexusModId)) {
                this.scheduleModUpdateCheck();
            }
            return true;
        }
        
        if (expected.length !== currentRows.length) return false;
        
        const newModsByCleanName = new Map();
        const newModsByFileKey = new Map();
        for (const mod of expected) {
            newModsByCleanName.set(cleanName(mod.originalName), mod);
            const fk = fileKeyOf(mod.originalName);
            if (fk) newModsByFileKey.set(fk.toLowerCase(), mod);
        }
        
        for (const row of currentRows) {
            const rowCleanName = cleanName(row.dataset.name);
            let mod = newModsByCleanName.get(rowCleanName);
            if (!mod) {
                const rfk = fileKeyOf(row.dataset.name);
                if (rfk) mod = newModsByFileKey.get(rfk.toLowerCase());
            }
            
            if (!mod) return false;
            
            if (row.dataset.name !== mod.originalName) {
                row.dataset.name = mod.originalName;
                row.querySelectorAll('[data-name]').forEach(el => el.dataset.name = mod.originalName);
            }
            
            const isEnabled = mod.status === 'enabled';
            const cb = row.querySelector('.mod-select');
            if (cb && cb.checked !== isEnabled) {
                cb.checked = isEnabled;
            }
            
            if (isEnabled) {
                row.classList.remove('mod-disabled');
            } else {
                row.classList.add('mod-disabled');
            }
            
            newModsByCleanName.delete(cleanName(mod.originalName));
        }
        
        if (newModsByCleanName.size > 0) return false;
        
        this.data = data;
        this.updateDeployButtonText();
        this.updateSelectAllCheckbox(expected);
        return true;
    }
    
    updateSelectAllCheckbox(mods) {
        const selectAll = document.getElementById('select-all-mods');
        if (selectAll && mods.length > 0) {
            selectAll.checked = mods.every(m => m.status === 'enabled');
        }
    }

    getSortedFilteredMods(data) {
        const mods = data.mods || [];
        const searchTerm = (this.lastSearchTerm || '').toLowerCase();

        let filteredMods = mods.filter(m => {
            const original = String(m?.originalName || '').toLowerCase();
            if (original.endsWith('.ini') || original.endsWith('.json') || original.endsWith('.txt') || original.endsWith('.toml')) return false;

            const matchesSearch = (m.name || "").toLowerCase().includes(searchTerm) ||
                (m.originalName || "").toLowerCase().includes(searchTerm);
            if (!matchesSearch) return false;

            if (m.isBundle && m.status === 'disabled') return false;

            const baseName = original.split('/').pop() || '';
            const isStringMod = /\.(strings|dlstrings|ilstrings)$/i.test(baseName);
            if (!isStringMod && this.modGroupsManager && !this.modGroupsManager.shouldShowMod(m.originalName, m.files)) {
                return false;
            }
            return true;
        });

        filteredMods.sort((a, b) => {
            const loA = typeof a.loadOrder === 'number' ? a.loadOrder : 9999;
            const loB = typeof b.loadOrder === 'number' ? b.loadOrder : 9999;
            return loA - loB;
        });
        
        return filteredMods;
    }

    updateDeployButtonText() {
        const labelEl = document.getElementById('btn-deploy-all-label');
        if (!labelEl) return;
        const count = Array.from(document.querySelectorAll('.mod-select')).filter(c => c.checked).length;
        labelEl.textContent = count > 0
            ? window._t('deploy_n_mods', count)
            : window._t('deploy_mods');
    }

    setupEventListeners() {
        const searchInput = document.getElementById('mods-search');
        const searchClear = document.getElementById('mods-search-clear');
        if (searchInput) {
            searchInput.addEventListener('input', (e) => {
                const next = String(e.target.value ?? '');
                this.lastSearchTerm = next;
                this.filterModRowsInPlace(next.trim().toLowerCase());
            });
        }
        if (searchClear && searchInput) {
            searchClear.addEventListener('click', () => {
                searchInput.value = '';
                this.lastSearchTerm = '';
                this.filterModRowsInPlace('');
                searchInput.focus();
            });
        }

        this._mountModsActionsMenu();

        const btnDeployAll = document.getElementById('btn-deploy-all');
        if (btnDeployAll) {
            btnDeployAll.addEventListener('click', () => {
                const norm = (n) => (this.modGroupsManager?.constructor?.normalizeModKey
                    ? this.modGroupsManager.constructor.normalizeModKey(n)
                    : String(n || '').replace(/^Disabled\//i, ''));
                const preset = this.modGroupsManager?.getActive?.();
                const members = Array.isArray(preset?.mods) ? preset.mods.map(String) : [];
                const memberSet = new Set(members.map(m => norm(m).toLowerCase()).filter(Boolean));
                const activeName = this.modGroupsManager?.getActiveName?.() || 'Preset';

                if (memberSet.size === 0) {
                    window.app?.showBanner?.({
                        type: 'warning',
                        text: window._t?.('preset_deploy_empty')
                            || `“${activeName}” has no mods. Add mods to this preset before deploying.`
                    });
                    window.chrome.webview.postMessage({
                        type: 'DEPLOY_ALL',
                        force: false,
                        mods: [],
                        presetScoped: true
                    });
                    return;
                }

                const tbody = document.getElementById('mods-list-body');
                const rows = tbody
                    ? Array.from(tbody.querySelectorAll(':scope > tr.mod-row'))
                    : Array.from(document.querySelectorAll('#mods-list-body .mod-row'));

                let modsToDeploy = rows
                    .filter(tr => tr.querySelector('.mod-select')?.checked)
                    .map(tr => tr.dataset.name)
                    .filter(name => name && memberSet.has(norm(name).toLowerCase()));

                if (modsToDeploy.length === 0) {
                    const allMods = this.data?.mods || window.app?.realData?.mods || [];
                    modsToDeploy = allMods
                        .filter(m => m?.status === 'enabled' && memberSet.has(norm(m.originalName).toLowerCase()))
                        .map(m => m.originalName)
                        .filter(Boolean);
                }

                window.chrome.webview.postMessage({
                    type: 'DEPLOY_ALL',
                    force: false,
                    mods: modsToDeploy,
                    presetScoped: true
                });
            });
        }

        const presetTrigger = document.getElementById('btn-preset-menu');
        const presetDropdown = document.getElementById('preset-menu-dropdown');
        if (presetTrigger && presetDropdown) {
            if (this._presetMenuAbort) this._presetMenuAbort.abort();
            this._presetMenuAbort = new AbortController();
            const { signal: presetSignal } = this._presetMenuAbort;

            const closePresetMenu = () => {
                presetDropdown.hidden = true;
                presetTrigger.setAttribute('aria-expanded', 'false');
            };

            const applyPreset = (val) => {
                if (!val) return;
                try {
                    const leaving = this.modGroupsManager.getActive();
                    if ((leaving?.mods || []).length > 0) {
                        const allMods = this.app?.realData?.mods || window.app?.realData?.mods || [];
                        this.modGroupsManager.syncEnabledFromMods(allMods);
                    }
                } catch {  }
                this.currentPreset = val;
                this.modGroupsManager.activePreset = val;
                this.modGroupsManager.save(true);
                this.modGroupsManager.applyActive();
                this.refreshUI();
            };

            presetTrigger.addEventListener('click', (e) => {
                e.stopPropagation();
                const open = presetDropdown.hidden;
                presetDropdown.hidden = !open;
                presetTrigger.setAttribute('aria-expanded', open ? 'true' : 'false');
                if (open && window.lucide) window.lucide.createIcons();
            }, { signal: presetSignal });

            document.addEventListener('click', (e) => {
                if (!document.getElementById('btn-preset-menu')) return;
                if (!e.target.closest('.preset-menu-wrap')) closePresetMenu();
            }, { signal: presetSignal });

            presetDropdown.querySelectorAll('.preset-menu-item[data-preset]').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const val = btn.getAttribute('data-preset');
                    closePresetMenu();
                    if (!val || val === this.modGroupsManager.getActiveName()) return;
                    applyPreset(val);
                }, { signal: presetSignal });
            });

            presetDropdown.querySelector('[data-preset-action="new"]')?.addEventListener('click', async (e) => {
                e.stopPropagation();
                closePresetMenu();
                const name = await window.appPrompt({
                    title: window._t('new_preset') || 'New…',
                    promptLabel: window._t('new_preset_prompt') || 'Enter new preset name:',
                    okText: window._t('create') || 'Create'
                });
                if (name && this.modGroupsManager.createPreset(name.trim())) {
                    this.currentPreset = name.trim();
                    this.modGroupsManager.applyActive();
                    this.refreshUI();
                }
            }, { signal: presetSignal });

            presetDropdown.querySelector('[data-preset-action="receive-updates"]')?.addEventListener('click', (e) => {
                e.stopPropagation();
                const globalOn = !!(this.data?.managerSettings?.updateModsInAllPresets
                    || window.app?.realData?.managerSettings?.updateModsInAllPresets);
                if (globalOn) return;
                const name = this.modGroupsManager.getActiveName();
                const next = !this.modGroupsManager.getReceiveUpdates(name);
                this.modGroupsManager.setReceiveUpdates(name, next);
                closePresetMenu();
                this.refreshUI();
            }, { signal: presetSignal });

            presetDropdown.querySelector('[data-preset-action="delete"]')?.addEventListener('click', async (e) => {
                e.stopPropagation();
                closePresetMenu();
                const name = this.modGroupsManager.getActiveName();
                if (this.modGroupsManager.isDefaultPreset?.(name)) {
                    window.app?.showBanner?.({ type: 'warning', text: window._t('cannot_delete_default_preset') });
                    return;
                }
                const ok = await window.appConfirm({
                    title: window._t('delete_preset') || 'Delete Preset',
                    message: window._t('delete_preset_confirm', name),
                    okText: window._t('delete'),
                    cancelText: window._t('cancel'),
                    danger: true
                });
                if (ok) {
                    this.modGroupsManager.deletePreset(name);
                    this.currentPreset = this.modGroupsManager.getActiveName();
                    this.modGroupsManager.applyActive();
                    this.refreshUI();
                }
            }, { signal: presetSignal });
        }

        document.getElementById('btn-preset-add-from-files')?.addEventListener('click', () => {
            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage({ type: 'ADD_MOD' });
            }
        });

        const selectAll = document.getElementById('select-all-mods');
        if (selectAll) {
            selectAll.addEventListener('click', (e) => {
                e.stopPropagation();
                const checked = selectAll.checked;
                const names = [];
                document.querySelectorAll('.mod-select').forEach(cb => {
                    cb.checked = checked;
                    const row = cb.closest('.mod-row');
                    if (row) {
                        const modName = row.dataset.name.replace(/^Disabled\//, '');
                        names.push(modName);
                        if (checked) {
                            row.classList.remove('mod-disabled');
                        } else {
                            row.classList.add('mod-disabled');
                        }
                    }
                });
                if (names.length > 0) {
                    window.chrome.webview.postMessage({
                        type: 'BULK_TOGGLE_MODS',
                        enabled: checked,
                        names
                    });
                    try {
                        const mgr = this.modGroupsManager;
                        const preset = mgr?.getActive?.();
                        if (preset) {
                            const norm = (n) => (mgr?.constructor?.normalizeModKey
                                ? mgr.constructor.normalizeModKey(n)
                                : String(n || '').replace(/^Disabled\//i, ''));
                            const en = new Set((preset.enabled || []).map(norm).filter(Boolean));
                            for (const n of names) {
                                const clean = norm(n);
                                if (!clean) continue;
                                if (checked) en.add(clean);
                                else en.delete(clean);
                            }
                            preset.enabled = [...en];
                            mgr.save?.(true);
                        }
                    } catch {  }
                }
                this.updateDeployButtonText();
            });
        }


        const tbody = document.getElementById('mods-list-body');
        if (tbody) {
            tbody.addEventListener('pointerdown', (e) => {
                if (e.target.closest('.btn-delete-mod') || e.target.closest('.btn-edit-mod') || e.target.closest('.mod-select') || e.target.closest('.checkbox-cell')) {
                    e.stopPropagation();
                }
            });
        }

        this._mountModsContextMenu();
    }

    _closeModsContextMenu() {
        document.querySelectorAll('.mods-row-context-menu').forEach(m => m.remove());
        if (this._modsContextAbort) {
            this._modsContextAbort.abort();
            this._modsContextAbort = null;
        }
    }

    _mountModsContextMenu() {
        const listRoot = document.querySelector('.mods-list-container') || document.getElementById('mods-list-body')?.closest('.mods-page');
        const tbody = document.getElementById('mods-list-body');
        if (!tbody && !listRoot) return;

        if (this._modsContextRootAbort) this._modsContextRootAbort.abort();
        this._modsContextRootAbort = new AbortController();
        const { signal } = this._modsContextRootAbort;

        const openMenu = (e, row) => {
            e.preventDefault();
            e.stopPropagation();
            this._closeModsContextMenu();

            this._modsContextAbort = new AbortController();
            const dismissSignal = this._modsContextAbort.signal;

            const menu = document.createElement('div');
            menu.className = 'bundle-menu-dropdown file-entry-context-menu mods-row-context-menu';

            if (!row) {
                menu.innerHTML = `
                    <div class="bundle-menu-item" data-action="add-mod">
                        <i data-lucide="plus"></i>
                        <span>${window._t('add_mod') || 'Add Mod'}</span>
                    </div>
                `;
            } else {
                const name = row.dataset.name || '';
                const checkbox = row.querySelector('.mod-select');
                const enabled = !!checkbox?.checked;
                const updateBtn = row.querySelector('.btn-mod-update:not(.btn-mod-state-off):not([disabled])');
                const hasUpdate = !!updateBtn;
                const presetMgr = this.modGroupsManager;
                const activePreset = presetMgr?.getActiveName?.() || '';
                const inPreset = !!presetMgr?.isModInActivePreset?.(name);
                const addTargets = (presetMgr?.listNames?.() || [])
                    .filter(p => p !== activePreset && !presetMgr.isModInPreset(p, name));
                const presetAddHtml = addTargets.length
                    ? addTargets.map(p => `
                    <div class="bundle-menu-item" data-action="preset-add" data-preset="${escapeAttr(p)}">
                        <i data-lucide="folder-plus"></i>
                        <span>${escapeAttr(window._t('preset_add_to', p))}</span>
                    </div>`).join('')
                    : `
                    <div class="bundle-menu-item bundle-menu-item--muted">
                        <i data-lucide="folder-plus"></i>
                        <span>${escapeAttr(window._t('preset_in_all_presets'))}</span>
                    </div>`;

                menu.innerHTML = `
                    <div class="bundle-menu-item" data-action="edit">
                        <i data-lucide="pencil"></i>
                        <span>${window._t('edit') || 'Edit'}</span>
                    </div>
                    <div class="bundle-menu-item" data-action="nexus">
                        <i data-lucide="link"></i>
                        <span>${window._t('rename_mod_tab_nexus') || 'Nexus'}</span>
                    </div>
                    <div class="bundle-menu-item" data-action="toggle">
                        <i data-lucide="${enabled ? 'toggle-right' : 'toggle-left'}"></i>
                        <span>${enabled ? (window._t('disable') || 'Disable') : (window._t('enable') || 'Enable')}</span>
                    </div>
                    ${hasUpdate ? `
                    <div class="bundle-menu-item" data-action="update">
                        <i data-lucide="download-cloud"></i>
                        <span>${window._t('mod_update_available') || 'Update This Mod'}</span>
                    </div>` : ''}
                    <div class="bundle-menu-divider" role="separator"></div>
                    ${presetAddHtml}
                    <div class="bundle-menu-item${inPreset ? '' : ' bundle-menu-item--muted'}" data-action="preset-remove">
                        <i data-lucide="folder-minus"></i>
                        <span>${escapeAttr(window._t('preset_remove_from', activePreset))}</span>
                    </div>
                    <div class="bundle-menu-divider" role="separator"></div>
                    <div class="bundle-menu-item danger" data-action="delete">
                        <i data-lucide="trash-2"></i>
                        <span>${window._t('delete') || 'Delete'}</span>
                    </div>
                `;

                menu.querySelector('[data-action="edit"]')?.addEventListener('click', (ev) => {
                    ev.stopPropagation();
                    this._closeModsContextMenu();
                    void this._openRenameModalAfterParkedEditor(name, 'general');
                });
                menu.querySelector('[data-action="nexus"]')?.addEventListener('click', (ev) => {
                    ev.stopPropagation();
                    this._closeModsContextMenu();
                    void this._openRenameModalAfterParkedEditor(name, 'nexus');
                });
                menu.querySelector('[data-action="toggle"]')?.addEventListener('click', (ev) => {
                    ev.stopPropagation();
                    this._closeModsContextMenu();
                    if (!checkbox) return;
                    checkbox.checked = !enabled;
                    if (window.handleModToggle) window.handleModToggle(checkbox, name);
                    else checkbox.dispatchEvent(new Event('change', { bubbles: true }));
                });
                menu.querySelector('[data-action="update"]')?.addEventListener('click', (ev) => {
                    ev.stopPropagation();
                    this._closeModsContextMenu();
                    if (updateBtn && window.nuclearModUpdate) window.nuclearModUpdate(updateBtn);
                });
                const cleanName = String(name).replace(/^Disabled\//i, '');
                menu.querySelectorAll('[data-action="preset-add"]').forEach(item => {
                    item.addEventListener('click', (ev) => {
                        ev.stopPropagation();
                        this._closeModsContextMenu();
                        const target = item.dataset.preset;
                        if (!target || !cleanName) return;
                        presetMgr.addModsToPreset(target, [cleanName]);
                        window.app?.showBanner?.({ type: 'success', text: window._t('preset_added_to', target) });
                    });
                });
                menu.querySelector('[data-action="preset-remove"]')?.addEventListener('click', (ev) => {
                    ev.stopPropagation();
                    this._closeModsContextMenu();
                    if (!inPreset || !activePreset || !cleanName) return;
                    presetMgr.removeModsFromPreset(activePreset, [cleanName]);
                    this.refreshUI?.();
                });
                menu.querySelector('[data-action="delete"]')?.addEventListener('click', async (ev) => {
                    ev.stopPropagation();
                    this._closeModsContextMenu();
                    await deleteModWithConfirmation(name);
                });
            }

            menu.querySelector('[data-action="add-mod"]')?.addEventListener('click', (ev) => {
                ev.stopPropagation();
                this._closeModsContextMenu();
                window.chrome?.webview?.postMessage?.({ type: 'ADD_MOD' });
            });

            document.body.appendChild(menu);
            if (window.lucide) window.lucide.createIcons();

            const pad = 8;
            let left = e.clientX;
            let top = e.clientY;
            const mw = menu.offsetWidth || 220;
            const mh = menu.offsetHeight || 200;
            if (left + mw > window.innerWidth - pad) left = window.innerWidth - mw - pad;
            if (top + mh > window.innerHeight - pad) top = window.innerHeight - mh - pad;
            menu.style.position = 'fixed';
            menu.style.left = `${Math.max(pad, left)}px`;
            menu.style.top = `${Math.max(pad, top)}px`;
            menu.style.zIndex = '10060';

            const closeOnDismiss = (ev) => {
                if (!menu.contains(ev.target)) this._closeModsContextMenu();
            };
            setTimeout(() => {
                document.addEventListener('click', closeOnDismiss, { signal: dismissSignal });
                document.addEventListener('contextmenu', closeOnDismiss, { signal: dismissSignal });
                document.addEventListener('scroll', () => this._closeModsContextMenu(), { signal: dismissSignal, capture: true });
                document.addEventListener('keydown', (ev) => {
                    if (ev.key === 'Escape') this._closeModsContextMenu();
                }, { signal: dismissSignal });
            }, 0);
        };

        tbody?.addEventListener('contextmenu', (e) => {
            const row = e.target.closest('tr.mod-row');
            if (row && tbody.contains(row)) {
                if (e.target.closest('a, input, button, .mod-select, .btn-icon')) {
                }
                openMenu(e, row);
                return;
            }
            openMenu(e, null);
        }, { signal });

        listRoot?.addEventListener('contextmenu', (e) => {
            if (e.target.closest('tr.mod-row')) return;
            if (tbody && tbody.contains(e.target)) return;
            if (!listRoot.contains(e.target)) return;
            openMenu(e, null);
        }, { signal });
    }
}