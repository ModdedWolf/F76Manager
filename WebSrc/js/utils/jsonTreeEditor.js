const COLOR_RE = /^(#)([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$|^(0x)([0-9a-f]{6})$/i;
const LARGE_TREE_NODES = 1500;
const LARGE_TREE_OPEN_DEPTH = 2;
const INDENT_PX = 18;
const COMMENT_GUTTER_PX = 18;

const isContainer = (v) => v !== null && typeof v === 'object';
const hasOwn = (o, k) => Object.prototype.hasOwnProperty.call(o, k);

function tr(key, fallback, ...args) {
    const v = typeof window._t === 'function' ? window._t(key, ...args) : '';
    return v && v !== key ? v : fallback;
}

function typeOf(v) {
    if (v === null) return 'null';
    if (Array.isArray(v)) return 'array';
    return typeof v;
}

function parseColor(str) {
    const m = COLOR_RE.exec(String(str));
    if (!m) return null;
    const prefix = m[1] || m[3];
    const hex = m[2] || m[4];
    let rgb = hex.length === 3 ? hex.split('').map((c) => c + c).join('') : hex.slice(0, 6);
    return {
        prefix,
        hex,
        css: `#${hex.length === 3 ? rgb : hex}`,
        pickerValue: `#${rgb.toLowerCase()}`,
        alpha: hex.length === 8 ? hex.slice(6) : '',
        upper: /[A-F]/.test(hex) && !/[a-f]/.test(hex),
    };
}

function formatColor(original, picked) {
    let hex = picked.replace(/^#/, '');
    if (original.hex.length === 3 && hex[0] === hex[1] && hex[2] === hex[3] && hex[4] === hex[5]) {
        hex = hex[0] + hex[2] + hex[4];
    }
    hex += original.alpha;
    hex = original.upper ? hex.toUpperCase() : hex.toLowerCase();
    return original.prefix + hex;
}

function countNodes(v) {
    if (!isContainer(v)) return 1;
    let n = 1;
    for (const k of Object.keys(v)) n += countNodes(v[k]);
    return n;
}

function replaceEntries(obj, entries) {
    for (const k of Object.keys(obj)) delete obj[k];
    for (const [k, v] of entries) obj[k] = v;
}

const SORT_KEY_FIELDS = ['name', 'id', 'key', 'title'];

function sortKey(v) {
    if (isContainer(v) && !Array.isArray(v)) {
        for (const k of SORT_KEY_FIELDS) {
            if (typeof v[k] === 'string') return v[k];
        }
        return JSON.stringify(v);
    }
    if (Array.isArray(v)) return JSON.stringify(v);
    return String(v);
}

function coerceEdit(original, text) {
    if (typeof original === 'string') return text;
    const t = text.trim();
    if (t === 'true') return true;
    if (t === 'false') return false;
    if (t === 'null') return null;
    if (t !== '' && Number.isFinite(Number(t))) return Number(t);
    return text;
}

export class JsonTreeEditor {
    constructor(container, { onChange } = {}) {
        this.container = container;
        this.onChange = typeof onChange === 'function' ? onChange : () => {};
        this.value = null;
        this.comments = new Map();
        this.collapsed = new WeakSet();
        this.allExpanded = true;
        this._menu = null;
        this._menuCloser = null;
        this._onClick = (e) => this._handleClick(e);
        container.classList.add('json-tree');
        container.addEventListener('click', this._onClick);
    }

    setComments(comments) {
        this.comments = comments instanceof Map ? comments : new Map();
    }

    setValue(value, comments) {
        this.value = value;
        this.setComments(comments);
        this.collapsed = new WeakSet();
        this.allExpanded = true;
        if (countNodes(value) > LARGE_TREE_NODES) {
            this._collapseFrom(value, 0, LARGE_TREE_OPEN_DEPTH);
            this.allExpanded = false;
        }
        this.render();
    }

    getValue() {
        return this.value;
    }

    expandAll() {
        this.collapsed = new WeakSet();
        this.allExpanded = true;
        this.render();
    }

    collapseAll() {
        this.collapsed = new WeakSet();
        this._collapseFrom(this.value, 0, 1);
        this.allExpanded = false;
        this.render();
    }

    toggleAll() {
        if (this.allExpanded) this.collapseAll();
        else this.expandAll();
        return this.allExpanded;
    }

    destroy() {
        this._closeMenu();
        this.container.removeEventListener('click', this._onClick);
        this.container.innerHTML = '';
        this.container.classList.remove('json-tree');
    }

    render() {
        const scroll = this.container.scrollTop;
        this._closeMenu();
        this.container.innerHTML = '';
        this.container.appendChild(this._renderNode(null, null, 0));
        this.container.scrollTop = scroll;
    }

    _collapseFrom(v, depth, openDepth) {
        if (!isContainer(v)) return;
        if (depth >= openDepth) this.collapsed.add(v);
        for (const k of Object.keys(v)) this._collapseFrom(v[k], depth + 1, openDepth);
    }

    _valueAt(meta) {
        return meta.parent == null ? this.value : meta.parent[meta.key];
    }

    _setValueAt(meta, v) {
        if (meta.parent == null) this.value = v;
        else meta.parent[meta.key] = v;
    }

    _emit(edit) {
        this.onChange(this.value, edit);
    }

    _renderNode(parent, key, depth, path = []) {
        const value = parent == null ? this.value : parent[key];
        const type = typeOf(value);
        const node = document.createElement('div');
        node.className = 'jt-node';
        node._jt = { parent, key, depth, path };
        const notes = this.comments.get(JSON.stringify(path));

        if (notes) this._appendCommentLines(node, notes.leading, depth);

        const row = document.createElement('div');
        row.className = 'jt-row';
        row.style.paddingLeft = `${depth * INDENT_PX}px`;
        node.appendChild(row);

        const container = isContainer(value);
        const open = container && !this.collapsed.has(value);

        const caret = document.createElement('span');
        caret.className = container ? 'jt-caret' : 'jt-caret jt-caret--leaf';
        caret.textContent = container ? (open ? '\u25BC' : '\u25B6') : '';
        row.appendChild(caret);

        const keyEl = document.createElement('span');
        if (parent == null) {
            keyEl.className = 'jt-key jt-key--root';
            keyEl.textContent = container ? type : 'value';
        } else if (Array.isArray(parent)) {
            keyEl.className = 'jt-key jt-key--index';
            keyEl.textContent = String(key);
        } else {
            keyEl.className = 'jt-key jt-key--editable';
            keyEl.textContent = key;
            keyEl.title = tr('json_tree_rename_key', 'Click to rename');
        }
        row.appendChild(keyEl);

        if (container) {
            const count = document.createElement('span');
            count.className = 'jt-count';
            const n = Object.keys(value).length;
            count.textContent = type === 'array' ? `[${n}]` : `{${n}}`;
            row.appendChild(count);
        } else {
            const sep = document.createElement('span');
            sep.className = 'jt-sep';
            sep.textContent = ':';
            row.appendChild(sep);

            if (type === 'string') {
                const color = parseColor(value);
                if (color) {
                    const sw = document.createElement('span');
                    sw.className = 'jt-swatch';
                    sw.style.background = color.css;
                    sw.title = tr('json_tree_pick_color', 'Pick color');
                    row.appendChild(sw);
                }
            }

            const valEl = document.createElement('span');
            valEl.className = `jt-value jt-type-${type}`;
            if (type === 'string' && value === '') {
                valEl.classList.add('jt-empty');
                valEl.textContent = 'value';
            } else {
                valEl.textContent = type === 'string' ? value : String(value);
            }
            valEl.title = type === 'boolean'
                ? tr('json_tree_toggle', 'Click to toggle')
                : tr('json_tree_edit', 'Click to edit');
            row.appendChild(valEl);
        }

        if (notes && notes.trailing.length) {
            const inline = document.createElement('span');
            inline.className = 'jt-comment-inline';
            inline.textContent = notes.trailing.join(' ');
            row.appendChild(inline);
        }

        const actions = document.createElement('span');
        actions.className = 'jt-actions';
        if (Array.isArray(parent)) {
            for (const [cls, glyph, label, disabled] of [
                ['jt-act--up', '\u2191', tr('json_tree_move_up', 'Move up'), key === 0],
                ['jt-act--down', '\u2193', tr('json_tree_move_down', 'Move down'), key === parent.length - 1],
            ]) {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.className = `jt-act ${cls}`;
                btn.textContent = glyph;
                btn.title = label;
                btn.disabled = disabled;
                actions.appendChild(btn);
            }
        }
        if (type === 'array' && value.length > 1) {
            const sort = document.createElement('button');
            sort.type = 'button';
            sort.className = 'jt-act jt-act--sort';
            sort.textContent = '\u21C5';
            sort.title = tr('json_tree_sort', 'Sort');
            actions.appendChild(sort);
        }
        if (container || parent != null) {
            const add = document.createElement('button');
            add.type = 'button';
            add.className = 'jt-act jt-act--add';
            add.textContent = '+';
            add.title = container
                ? tr('json_tree_add_child', 'Add child')
                : tr('json_tree_add_after', 'Add after');
            actions.appendChild(add);
        }
        if (parent != null) {
            const rm = document.createElement('button');
            rm.type = 'button';
            rm.className = 'jt-act jt-act--remove';
            rm.textContent = '\u00D7';
            rm.title = tr('json_tree_remove', 'Remove');
            actions.appendChild(rm);
        }
        row.appendChild(actions);

        if (open) {
            const kids = document.createElement('div');
            kids.className = 'jt-children';
            for (const k of Object.keys(value)) {
                const childKey = Array.isArray(value) ? Number(k) : k;
                kids.appendChild(this._renderNode(value, childKey, depth + 1, [...path, childKey]));
            }
            node.appendChild(kids);
        }
        if (notes && (open || !container)) {
            this._appendCommentLines(node, notes.footer, container ? depth + 1 : depth);
        }
        return node;
    }

    _appendCommentLines(node, lines, depth) {
        for (const text of lines || []) {
            const el = document.createElement('div');
            el.className = 'jt-comment';
            el.style.paddingLeft = `${depth * INDENT_PX + COMMENT_GUTTER_PX}px`;
            el.textContent = text;
            node.appendChild(el);
        }
    }

    _rerender(node) {
        if (!this.container.contains(node)) return null;
        const { parent, key, depth, path } = node._jt;
        const fresh = this._renderNode(parent, key, depth, path);
        node.replaceWith(fresh);
        return fresh;
    }

    _parentNode(node) {
        return node.parentElement ? node.parentElement.closest('.jt-node') : null;
    }

    _childNode(node, key) {
        const kids = node.querySelector(':scope > .jt-children');
        if (!kids) return null;
        for (const el of kids.children) {
            if (el._jt && el._jt.key === key) return el;
        }
        return null;
    }

    _handleClick(e) {
        const t = e.target;
        if (!(t instanceof Element)) return;
        if (t.closest('.jt-menu') || t.closest('.jt-input')) return;
        if (t.closest('.jt-comment') || t.closest('.jt-comment-inline')) return;
        const node = t.closest('.jt-node');
        if (!node || !this.container.contains(node)) return;
        const meta = node._jt;
        const value = this._valueAt(meta);

        if (t.classList.contains('jt-caret') || t.classList.contains('jt-count') ||
            (t.classList.contains('jt-key') && isContainer(value) && !t.classList.contains('jt-key--editable'))) {
            if (!isContainer(value)) return;
            if (this.collapsed.has(value)) this.collapsed.delete(value);
            else this.collapsed.add(value);
            this._rerender(node);
            return;
        }
        if (t.classList.contains('jt-act--remove')) {
            this._remove(node);
            return;
        }
        if (t.classList.contains('jt-act--add')) {
            this._openAddMenu(t, node, isContainer(value) ? 'child' : 'sibling');
            return;
        }
        if (t.classList.contains('jt-act--up') || t.classList.contains('jt-act--down')) {
            this._move(node, t.classList.contains('jt-act--up') ? -1 : 1);
            return;
        }
        if (t.classList.contains('jt-act--sort')) {
            this._openMenu(t, [
                [tr('json_tree_sort_asc', 'Sort A-Z'), () => this._sort(node, 1)],
                [tr('json_tree_sort_desc', 'Sort Z-A'), () => this._sort(node, -1)],
            ]);
            return;
        }
        if (t.classList.contains('jt-swatch')) {
            this._pickColor(t, node);
            return;
        }
        if (t.classList.contains('jt-key--editable')) {
            this._editKey(node, t);
            return;
        }
        if (t.classList.contains('jt-value')) {
            if (typeof value === 'boolean') {
                this._setValueAt(meta, !value);
                this._emit({ op: 'set', path: meta.path, value: !value });
                this._rerender(node);
                return;
            }
            this._editValue(node, t);
        }
    }

    _inlineInput(target, initial, { onCommit, onCancel }) {
        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'jt-input';
        input.value = initial;
        input.spellcheck = false;
        const size = () => { input.size = Math.max(4, input.value.length + 1); };
        size();
        let done = false;
        const finish = (commit) => {
            if (done) return;
            done = true;
            if (commit) onCommit(input.value, input);
            else onCancel();
        };
        input.addEventListener('input', size);
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                e.stopPropagation();
                finish(true);
            } else if (e.key === 'Escape') {
                e.preventDefault();
                e.stopPropagation();
                finish(false);
            }
        });
        input.addEventListener('blur', () => finish(true));
        target.replaceWith(input);
        input.focus();
        input.select();
        return input;
    }

    _editValue(node, valEl) {
        const meta = node._jt;
        const original = this._valueAt(meta);
        const initial = typeof original === 'string' ? original : String(original);
        this._inlineInput(valEl, initial, {
            onCommit: (text) => {
                const next = coerceEdit(original, text);
                if (Object.is(next, original)) {
                    this._rerender(node);
                    return;
                }
                this._setValueAt(meta, next);
                this._emit({ op: 'set', path: meta.path, value: next });
                this._rerender(node);
            },
            onCancel: () => this._rerender(node),
        });
    }

    _editKey(node, keyEl) {
        const { parent, key, path } = node._jt;
        this._inlineInput(keyEl, key, {
            onCommit: (text, input) => {
                if (text === key) {
                    this._rerender(node);
                    return;
                }
                if (hasOwn(parent, text)) {
                    const flash = document.createElement('span');
                    flash.className = 'jt-key jt-key--editable jt-key--error';
                    flash.textContent = key;
                    flash.title = tr('json_tree_key_exists', `"${text}" already exists`, text);
                    input.replaceWith(flash);
                    setTimeout(() => {
                        if (flash.isConnected) this._rerender(node);
                    }, 900);
                    return;
                }
                replaceEntries(parent, Object.entries(parent).map(([k, v]) => [k === key ? text : k, v]));
                const parentNode = this._parentNode(node);
                this._emit({ op: 'rename', path, newKey: text });
                if (parentNode) this._rerender(parentNode);
            },
            onCancel: () => this._rerender(node),
        });
    }

    _remove(node) {
        const { parent, key, path } = node._jt;
        if (parent == null) return;
        if (Array.isArray(parent)) parent.splice(key, 1);
        else delete parent[key];
        const parentNode = this._parentNode(node);
        this._emit({ op: 'remove', path });
        if (parentNode) this._rerender(parentNode);
    }

    _uniqueKey(obj) {
        let k = 'newKey';
        let i = 2;
        while (hasOwn(obj, k)) k = `newKey${i++}`;
        return k;
    }

    _insert(node, mode, kind) {
        const fresh = kind === 'object' ? {} : kind === 'array' ? [] : '';
        let target;
        let targetNode;
        let newKey;
        let containerPath;
        let index;

        if (mode === 'child') {
            target = this._valueAt(node._jt);
            targetNode = node;
            containerPath = node._jt.path;
            index = Object.keys(target).length;
            if (Array.isArray(target)) {
                target.push(fresh);
                newKey = target.length - 1;
            } else {
                newKey = this._uniqueKey(target);
                target[newKey] = fresh;
            }
            this.collapsed.delete(target);
        } else {
            const { parent, key, path } = node._jt;
            target = parent;
            targetNode = this._parentNode(node);
            containerPath = path.slice(0, -1);
            if (Array.isArray(parent)) {
                parent.splice(key + 1, 0, fresh);
                newKey = key + 1;
                index = key + 1;
            } else {
                newKey = this._uniqueKey(parent);
                index = Object.keys(parent).indexOf(key) + 1;
                const entries = [];
                for (const [k, v] of Object.entries(parent)) {
                    entries.push([k, v]);
                    if (k === key) entries.push([newKey, fresh]);
                }
                replaceEntries(parent, entries);
            }
        }
        if (!targetNode) return;
        this._emit({
            op: 'insert',
            path: containerPath,
            index,
            key: Array.isArray(target) ? undefined : newKey,
            value: fresh,
        });
        const rendered = this._rerender(targetNode);
        if (!rendered) return;

        const added = this._childNode(rendered, newKey);
        if (!added) return;
        added.scrollIntoView({ block: 'nearest' });
        const row = added.querySelector(':scope > .jt-row');
        if (!Array.isArray(target)) {
            const keyEl = row.querySelector('.jt-key--editable');
            if (keyEl) this._editKey(added, keyEl);
        } else if (kind === 'value') {
            const valEl = row.querySelector('.jt-value');
            if (valEl) this._editValue(added, valEl);
        }
    }

    _applyOrder(node, order) {
        const arr = this._valueAt(node._jt);
        const copy = arr.slice();
        order.forEach((from, i) => { arr[i] = copy[from]; });
        this._emit({ op: 'reorder', path: node._jt.path, order });
        return this._rerender(node);
    }

    _move(node, delta) {
        const { parent, key } = node._jt;
        if (!Array.isArray(parent)) return;
        const to = key + delta;
        if (to < 0 || to >= parent.length) return;
        const parentNode = this._parentNode(node);
        if (!parentNode) return;
        const order = parent.map((_, i) => i);
        order[key] = to;
        order[to] = key;
        const rendered = this._applyOrder(parentNode, order);
        const moved = rendered && this._childNode(rendered, to);
        if (!moved) return;
        moved.scrollIntoView({ block: 'nearest' });
        const btn = moved.querySelector(`:scope > .jt-row .${delta < 0 ? 'jt-act--up' : 'jt-act--down'}`);
        if (btn && !btn.disabled) btn.focus();
    }

    _sort(node, dir) {
        const arr = this._valueAt(node._jt);
        if (!Array.isArray(arr) || arr.length < 2) return;
        const keys = arr.map(sortKey);
        const order = arr.map((_, i) => i).sort((a, b) =>
            (keys[a].localeCompare(keys[b], undefined, { numeric: true, sensitivity: 'base' }) * dir) || a - b);
        if (order.every((v, i) => v === i)) return;
        this.collapsed.delete(arr);
        this._applyOrder(node, order);
    }

    _openAddMenu(anchor, node, mode) {
        this._openMenu(anchor, [
            ['value', tr('json_tree_add_value', 'Value')],
            ['object', tr('json_tree_add_object', 'Object {}')],
            ['array', tr('json_tree_add_array', 'Array []')],
        ].map(([kind, label]) => [label, () => this._insert(node, mode, kind)]));
    }

    _openMenu(anchor, items) {
        this._closeMenu();
        const menu = document.createElement('div');
        menu.className = 'jt-menu';
        for (const [label, run] of items) {
            const b = document.createElement('button');
            b.type = 'button';
            b.textContent = label;
            b.addEventListener('click', (e) => {
                e.stopPropagation();
                this._closeMenu();
                run();
            });
            menu.appendChild(b);
        }
        const cr = this.container.getBoundingClientRect();
        const ar = anchor.getBoundingClientRect();
        menu.style.top = `${ar.bottom - cr.top + this.container.scrollTop + 2}px`;
        menu.style.left = `${ar.left - cr.left + this.container.scrollLeft}px`;
        this.container.appendChild(menu);
        this._menu = menu;

        this._menuCloser = (e) => {
            if (e.type === 'keydown') {
                if (e.key !== 'Escape') return;
                e.preventDefault();
                e.stopPropagation();
            } else if (menu.contains(e.target)) {
                return;
            }
            this._closeMenu();
        };
        setTimeout(() => {
            document.addEventListener('mousedown', this._menuCloser, true);
            document.addEventListener('keydown', this._menuCloser, true);
        }, 0);
    }

    _closeMenu() {
        if (this._menuCloser) {
            document.removeEventListener('mousedown', this._menuCloser, true);
            document.removeEventListener('keydown', this._menuCloser, true);
            this._menuCloser = null;
        }
        if (this._menu) {
            this._menu.remove();
            this._menu = null;
        }
    }

    _pickColor(swatch, node) {
        const meta = node._jt;
        const original = parseColor(this._valueAt(meta));
        if (!original) return;
        this.container.querySelectorAll('.jt-color-input').forEach((el) => el.remove());
        const picker = document.createElement('input');
        picker.type = 'color';
        picker.className = 'jt-color-input';
        picker.value = original.pickerValue;
        const cr = this.container.getBoundingClientRect();
        const sr = swatch.getBoundingClientRect();
        picker.style.top = `${sr.top - cr.top + this.container.scrollTop}px`;
        picker.style.left = `${sr.left - cr.left + this.container.scrollLeft}px`;
        this.container.appendChild(picker);

        picker.addEventListener('input', () => {
            swatch.style.background = picker.value;
        });
        picker.addEventListener('change', () => {
            const next = formatColor(original, picker.value);
            picker.remove();
            if (next === this._valueAt(meta)) return;
            this._setValueAt(meta, next);
            this._emit({ op: 'set', path: meta.path, value: next });
            this._rerender(node);
        });
        picker.click();
    }
}
