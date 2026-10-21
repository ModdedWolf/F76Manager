import { escapeAttr } from './htmlSafe.js';

const HIT = 'f76-page-find';
const HIT_ACTIVE = 'f76-page-find-active';
const MODAL_SELECTOR = '#confirm-modal.active, #conflict-popup-overlay, .modal-overlay.active:not(.config-editor-parked)';
const SKIP_SELECTOR = 'script, style, noscript, template, textarea, select, svg, .config-find-bar';
const MENU_SELECTOR = '.mods-actions-dropdown, .preset-menu-dropdown';

const t = (key, fallback, ...args) => {
    const v = window._t?.(key, ...args);
    if (v && v !== key) return v;
    return args.reduce((text, arg, i) => text.split(`{${i}}`).join(String(arg)), fallback);
};

const isRendered = (el) => {
    if (typeof el.checkVisibility !== 'function') return true;
    if (el.checkVisibility({ checkVisibilityCSS: true, visibilityProperty: true })) return true;
    return getComputedStyle(el).display === 'contents';
};

function collectText(root, live, out = [], inMenu = false, path = '') {
    let index = 0;
    for (const child of root.childNodes) {
        if (child.nodeType === Node.TEXT_NODE) {
            if (child.nodeValue.trim()) out.push({ node: child, sig: `${path}|${child.nodeValue}` });
            continue;
        }
        if (child.nodeType !== Node.ELEMENT_NODE) continue;
        const childPath = `${path}/${child.tagName}${index++}`;
        if (child.matches(SKIP_SELECTOR)) continue;
        const menu = child.matches(MENU_SELECTOR);
        if (!menu) {
            if (child.hidden) continue;
            if (live && !inMenu ? !isRendered(child) : child.style?.display === 'none') continue;
        }
        collectText(child, live, out, inMenu || menu, childPath);
    }
    return out;
}

export function installPageFind({ views = null } = {}) {
    const bar = document.createElement('div');
    bar.className = 'config-find-bar page-find-bar';
    bar.hidden = true;
    bar.setAttribute('role', 'search');
    bar.innerHTML = `
        <div class="config-find-field">
            <input class="config-find-input" type="text" spellcheck="false" autocomplete="off" placeholder="${escapeAttr(t('config_editor_find', 'Find'))}" aria-label="${escapeAttr(t('config_editor_find', 'Find'))}">
            <button type="button" class="config-find-clear" hidden aria-label="${escapeAttr(t('config_editor_find_clear', 'Clear'))}"><i data-lucide="x"></i></button>
        </div>
        <span class="config-find-count" aria-live="polite"></span>
        <button type="button" class="config-find-btn" data-find="prev" title="${escapeAttr(t('config_editor_find_prev', 'Previous match'))}" aria-label="${escapeAttr(t('config_editor_find_prev', 'Previous match'))}"><i data-lucide="arrow-up"></i></button>
        <button type="button" class="config-find-btn" data-find="next" title="${escapeAttr(t('config_editor_find_next', 'Next match'))}" aria-label="${escapeAttr(t('config_editor_find_next', 'Next match'))}"><i data-lucide="arrow-down"></i></button>
        <button type="button" class="config-find-btn" data-find="case" aria-pressed="false" title="${escapeAttr(t('config_editor_find_case', 'Match case'))}" aria-label="${escapeAttr(t('config_editor_find_case', 'Match case'))}">Aa</button>
        <button type="button" class="config-find-btn" data-find="close" title="${escapeAttr(t('config_editor_find_close', 'Close'))}" aria-label="${escapeAttr(t('config_editor_find_close', 'Close'))}"><i data-lucide="x"></i></button>
        <div class="page-find-hint" hidden></div>`;
    document.body.appendChild(bar);
    try { window.lucide?.createIcons({ nodes: [bar] }); } catch (_) { }

    const input = bar.querySelector('.config-find-input');
    const clearBtn = bar.querySelector('.config-find-clear');
    const countEl = bar.querySelector('.config-find-count');
    const hintEl = bar.querySelector('.page-find-hint');
    const caseBtn = bar.querySelector('[data-find="case"]');
    const state = { query: '', caseSensitive: false, active: 0, segments: [], ranges: [] };
    const highlights = typeof Highlight === 'function' ? CSS.highlights : null;
    let cache = null;
    let observer = null;
    let refreshTimer = 0;

    const topModal = () => {
        const modals = document.querySelectorAll(MODAL_SELECTOR);
        return modals.length ? modals[modals.length - 1] : null;
    };

    const needle = () => (state.caseSensitive ? state.query : state.query.toLowerCase());

    const positions = (text, n) => {
        const hay = state.caseSensitive ? text : text.toLowerCase();
        const out = [];
        for (let at = hay.indexOf(n); at >= 0 && at + n.length <= text.length; at = hay.indexOf(n, at + n.length)) out.push(at);
        return out;
    };

    const rangesFor = (entries, n) => {
        const ranges = [];
        for (const { node } of entries) {
            for (const at of positions(node.nodeValue, n)) {
                const range = document.createRange();
                range.setStart(node, at);
                range.setEnd(node, at + n.length);
                ranges.push(range);
            }
        }
        return ranges;
    };

    const liveEntries = (...roots) => roots.filter(Boolean).flatMap((root) => collectText(root, true));

    const liveContentEntries = () => liveEntries(
        document.getElementById('content-area'),
        ...document.querySelectorAll('body > .mods-actions-dropdown:not([hidden])')
    );

    const ensureIndex = (list) => {
        const stamp = views?.stamp?.();
        if (!cache || cache.stamp !== stamp) cache = { stamp, byKey: new Map(), shared: new Map(), emptyLive: new Set() };
        let added = false;
        for (const view of list) {
            if (cache.byKey.has(view.key)) continue;
            added = true;
            let entries = [];
            const html = views.html(view);
            if (html != null) {
                const tpl = document.createElement('template');
                tpl.innerHTML = html;
                entries = collectText(tpl.content, false).map((e) => ({ sig: e.sig, text: e.node.nodeValue }));
            }
            cache.byKey.set(view.key, entries);
        }
        if (!added) return cache;
        cache.shared.clear();
        const bySection = new Map();
        list.forEach((view) => bySection.set(view.section, [...(bySection.get(view.section) || []), view]));
        for (const [section, group] of bySection) {
            if (group.length < 2) continue;
            let shared = new Set(cache.byKey.get(group[0].key).map((e) => e.sig));
            for (const view of group.slice(1)) {
                const sigs = new Set(cache.byKey.get(view.key).map((e) => e.sig));
                shared = new Set([...shared].filter((s) => sigs.has(s)));
            }
            cache.shared.set(section, shared);
        }
        return cache;
    };

    const rebuild = () => {
        position();
        state.segments = [];
        state.ranges = [];
        if (!state.query) return;
        const n = needle();
        const live = (key, entries, view = null) => {
            const ranges = rangesFor(entries, n);
            state.segments.push({ key, view, live: true, ranges, count: ranges.length });
        };
        const modal = topModal();
        const list = !modal && views ? views.list() : [];
        if (!list.length) {
            live('page', liveEntries(modal || document.getElementById('app') || document.body));
        } else {
            live('chrome', liveEntries(document.getElementById('sidebar'), document.querySelector('.top-bar')));
            const index = ensureIndex(list);
            const owners = new Map();
            list.forEach((v) => { if (!owners.has(v.section)) owners.set(v.section, v.key); });
            list.forEach((v) => { if (v.current) owners.set(v.section, v.key); });
            if (!list.some((v) => v.current)) live('content', liveContentEntries());
            const queryKey = `${state.caseSensitive ? 1 : 0}${n}`;
            for (const view of list) {
                const seenKey = `${view.key}\u0000${queryKey}`;
                if (view.current) {
                    live(view.key, liveContentEntries(), view);
                    if (!state.segments[state.segments.length - 1].count) index.emptyLive.add(seenKey);
                    continue;
                }
                const shared = owners.get(view.section) === view.key ? null : index.shared.get(view.section);
                let count = 0;
                for (const e of index.emptyLive.has(seenKey) ? [] : index.byKey.get(view.key) || []) {
                    if (shared?.has(e.sig)) continue;
                    count += positions(e.text, n).length;
                }
                state.segments.push({ key: view.key, view, live: false, count });
            }
        }
        state.ranges = state.segments.filter((s) => s.live).flatMap((s) => s.ranges);
        const total = totalCount();
        state.active = total ? ((state.active % total) + total) % total : 0;
    };

    const totalCount = () => state.segments.reduce((sum, s) => sum + s.count, 0);

    const locate = (globalIndex) => {
        let offset = 0;
        let liveOffset = 0;
        for (let i = 0; i < state.segments.length; i++) {
            const seg = state.segments[i];
            if (globalIndex < offset + seg.count) {
                const local = globalIndex - offset;
                return { seg, index: i, offset, local, liveIndex: seg.live ? liveOffset + local : -1 };
            }
            offset += seg.count;
            if (seg.live) liveOffset += seg.count;
        }
        return null;
    };

    const offsetOf = (segIndex) => state.segments.slice(0, segIndex).reduce((sum, s) => sum + s.count, 0);

    const globalOfLive = (liveIndex) => {
        let offset = 0;
        let remaining = liveIndex;
        for (const seg of state.segments) {
            if (seg.live && remaining < seg.count) return offset + remaining;
            if (seg.live) remaining -= seg.count;
            offset += seg.count;
        }
        return 0;
    };

    const pickInitial = () => {
        const segs = state.segments;
        const current = segs.findIndex((s) => s.view?.current || s.key === 'content' || s.key === 'page');
        const order = current < 0 ? segs.map((_, i) => i) : [current, 0, ...segs.map((_, i) => i).slice(current + 1), ...segs.map((_, i) => i).slice(1, current)];
        const hit = order.find((i) => segs[i]?.count);
        state.active = hit == null ? 0 : offsetOf(hit);
    };

    const position = () => {
        const topBar = document.querySelector('.top-bar');
        const top = !topModal() && topBar ? topBar.getBoundingClientRect().bottom + 10 : 10;
        bar.style.top = `${Math.round(top)}px`;
    };

    const activeRange = () => {
        const loc = locate(state.active);
        return loc?.seg.live ? state.ranges[loc.liveIndex] : null;
    };

    const hiddenMenuOf = (range) => {
        const menu = range?.startContainer?.parentElement?.closest(MENU_SELECTOR);
        return menu?.hidden ? menu : null;
    };

    const menuTrigger = (menu) => menu.parentElement?.querySelector(':scope > [aria-haspopup]');

    const isPending = () => {
        const loc = locate(state.active);
        if (!loc) return false;
        return !loc.seg.live || !!hiddenMenuOf(state.ranges[loc.liveIndex]);
    };

    const paint = () => {
        if (!highlights) return;
        const all = new Highlight();
        state.ranges.forEach((r) => all.add(r));
        highlights.set(HIT, all);
        const range = activeRange();
        if (range) {
            const active = new Highlight(range);
            active.priority = 1;
            highlights.set(HIT_ACTIVE, active);
        } else {
            highlights.delete(HIT_ACTIVE);
        }
    };

    const hintText = () => {
        const loc = totalCount() ? locate(state.active) : null;
        if (!loc) return '';
        if (!loc.seg.live) {
            const where = views?.label?.(loc.seg.view);
            return where ? t('page_find_elsewhere', 'On {0} · press Enter to go there', where) : '';
        }
        return hiddenMenuOf(state.ranges[loc.liveIndex]) ? t('page_find_in_menu', 'In a menu · press Enter to open it') : '';
    };

    const render = () => {
        const total = totalCount();
        if (!state.query) countEl.textContent = '';
        else countEl.textContent = total ? `${state.active + 1}/${total}` : '0/0';
        countEl.classList.toggle('config-find-count--empty', !!state.query && !total);
        clearBtn.hidden = !input.value;
        const hint = hintText();
        hintEl.hidden = !hint;
        hintEl.textContent = hint;
        paint();
    };

    const revealActive = ({ openMenu }) => {
        let range = activeRange();
        const menu = hiddenMenuOf(range);
        if (menu && !openMenu) {
            menuTrigger(menu)?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
            return;
        }
        if (menu) {
            const node = range.startContainer;
            const at = range.startOffset;
            menuTrigger(menu)?.click();
            rebuild();
            const liveIndex = state.ranges.findIndex((r) => r.startContainer === node && r.startOffset === at);
            if (liveIndex >= 0) state.active = globalOfLive(liveIndex);
            range = activeRange();
        }
        range?.startContainer?.parentElement?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    };

    const goToActive = (direction) => {
        for (let guard = state.segments.length + 1; guard > 0; guard--) {
            const loc = locate(state.active);
            if (!loc || loc.seg.live) break;
            const { seg, local } = loc;
            views.activate(seg.view);
            rebuild();
            const index = state.segments.findIndex((s) => s.key === seg.key);
            const landed = state.segments[index];
            const total = totalCount();
            if (!total) break;
            if (landed?.count) {
                state.active = offsetOf(index) + Math.min(local, landed.count - 1);
                break;
            }
            const next = index < 0 ? state.active : offsetOf(index) + (direction < 0 ? -1 : 0);
            state.active = ((next % total) + total) % total;
        }
        revealActive({ openMenu: true });
        render();
    };

    const refresh = () => {
        rebuild();
        render();
    };

    const scheduleRefresh = (mutations) => {
        if (mutations.every((m) => bar.contains(m.target))) return;
        clearTimeout(refreshTimer);
        refreshTimer = setTimeout(refresh, 120);
    };

    const open = () => {
        position();
        if (bar.hidden) {
            bar.hidden = false;
            cache = null;
            observer = new MutationObserver(scheduleRefresh);
            observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class', 'hidden'] });
            if (state.query) refresh();
        }
        input.focus();
        input.select();
    };

    const close = () => {
        if (bar.hidden) return false;
        bar.hidden = true;
        observer?.disconnect();
        observer = null;
        clearTimeout(refreshTimer);
        state.segments = [];
        state.ranges = [];
        state.active = 0;
        hintEl.hidden = true;
        highlights?.delete(HIT);
        highlights?.delete(HIT_ACTIVE);
        return true;
    };

    const step = (direction) => {
        if (bar.hidden) {
            open();
            return;
        }
        rebuild();
        const total = totalCount();
        if (!total) {
            render();
            return;
        }
        if (!(isPending() && direction > 0)) state.active = (state.active + direction + total) % total;
        goToActive(direction);
    };

    const search = () => {
        rebuild();
        pickInitial();
        revealActive({ openMenu: false });
        render();
    };

    input.addEventListener('input', () => {
        state.query = input.value || '';
        search();
    });
    clearBtn.addEventListener('click', (e) => {
        e.preventDefault();
        input.value = '';
        state.query = '';
        search();
        input.focus();
    });
    input.addEventListener('keydown', (e) => {
        if (e.key === 'Enter') {
            e.preventDefault();
            e.stopPropagation();
            step(e.shiftKey ? -1 : 1);
        } else if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            close();
        }
    });
    bar.querySelector('[data-find="prev"]').addEventListener('click', () => step(-1));
    bar.querySelector('[data-find="next"]').addEventListener('click', () => step(1));
    caseBtn.addEventListener('click', () => {
        state.caseSensitive = !state.caseSensitive;
        caseBtn.classList.toggle('active', state.caseSensitive);
        caseBtn.setAttribute('aria-pressed', String(state.caseSensitive));
        search();
    });
    bar.querySelector('[data-find="close"]').addEventListener('click', close);
    bar.addEventListener('click', (e) => e.stopPropagation());

    return { open, step, close };
}
