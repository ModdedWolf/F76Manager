const COLOR_RE = /^(#)([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$|^(0x)([0-9a-f]{6})$/i;
const LARGE_TREE_NODES = 1500;
const LARGE_TREE_OPEN_DEPTH = 2;
const INDENT_PX = 18;

const isContainer = (v) => v !== null && typeof v === 'object';
const hasOwn = (o, k) => Object.prototype.hasOwnProperty.call(o, k);

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
        this.collapsed = new WeakSet();
        this.allExpanded = true;
        this._menu = null;
        this._menuCloser = null;
        this._onClick = (e) => this._handleClick(e);
        container.classList.add('json-tree');
        container.addEventListener('click', this._onClick);
    }

    setValue(value) {
        this.value = value;
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

    _emit() {
        this.onChange(this.value);
    }

    _renderNode(parent, key, depth) {
        const value = parent == null ? this.value : parent[key];
        const type = typeOf(value);
        const node = document.createElement('div');
        node.className = 'jt-node';
        node._jt = { parent, key, depth };

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
            keyEl.title = 'Click to rename';
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
                    sw.title = 'Pick color';
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
            valEl.title = type === 'boolean' ? 'Click to toggle' : 'Click to edit';
            row.appendChild(valEl);
        }

        const actions = document.createElement('span');
        actions.className = 'jt-actions';
        if (container || parent != null) {
            const add = document.createElement('button');
            add.type = 'button';
            add.className = 'jt-act jt-act--add';
            add.textContent = '+';
            add.title = container ? 'Add child' : 'Add after';
            actions.appendChild(add);
        }
        if (parent != null) {
            const rm = document.createElement('button');
            rm.type = 'button';
            rm.className = 'jt-act jt-act--remove';
            rm.textContent = '\u00D7';
            rm.title = 'Remove';
            actions.appendChild(rm);
        }
        row.appendChild(actions);

        if (open) {
            const kids = document.createElement('div');
            kids.className = 'jt-children';
            for (const k of Object.keys(value)) {
                kids.appendChild(this._renderNode(value, Array.isArray(value) ? Number(k) : k, depth + 1));
            }
            node.appendChild(kids);
        }
        return node;
    }

    _rerender(node) {
        const { parent, key, depth } = node._jt;
        const fresh = this._renderNode(parent, key, depth);
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
                this._rerender(node);
                this._emit();
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
                this._rerender(node);
                this._emit();
            },
            onCancel: () => this._rerender(node),
        });
    }

    _editKey(node, keyEl) {
        const { parent, key } = node._jt;
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
                    flash.title = `"${text}" already exists`;
                    input.replaceWith(flash);
                    setTimeout(() => {
                        if (flash.isConnected) this._rerender(node);
                    }, 900);
                    return;
                }
                replaceEntries(parent, Object.entries(parent).map(([k, v]) => [k === key ? text : k, v]));
                const parentNode = this._parentNode(node);
                if (parentNode) this._rerender(parentNode);
                this._emit();
            },
            onCancel: () => this._rerender(node),
        });
    }

    _remove(node) {
        const { parent, key } = node._jt;
        if (parent == null) return;
        if (Array.isArray(parent)) parent.splice(key, 1);
        else delete parent[key];
        const parentNode = this._parentNode(node);
        if (parentNode) this._rerender(parentNode);
        this._emit();
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

        if (mode === 'child') {
            target = this._valueAt(node._jt);
            targetNode = node;
            if (Array.isArray(target)) {
                target.push(fresh);
                newKey = target.length - 1;
            } else {
                newKey = this._uniqueKey(target);
                target[newKey] = fresh;
            }
            this.collapsed.delete(target);
        } else {
            const { parent, key } = node._jt;
            target = parent;
            targetNode = this._parentNode(node);
            if (Array.isArray(parent)) {
                parent.splice(key + 1, 0, fresh);
                newKey = key + 1;
            } else {
                newKey = this._uniqueKey(parent);
                const entries = [];
                for (const [k, v] of Object.entries(parent)) {
                    entries.push([k, v]);
                    if (k === key) entries.push([newKey, fresh]);
                }
                replaceEntries(parent, entries);
            }
        }
        if (!targetNode) return;
        const rendered = this._rerender(targetNode);
        this._emit();

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

    _openAddMenu(anchor, node, mode) {
        this._closeMenu();
        const menu = document.createElement('div');
        menu.className = 'jt-menu';
        for (const [kind, label] of [['value', 'Value'], ['object', 'Object {}'], ['array', 'Array []']]) {
            const b = document.createElement('button');
            b.type = 'button';
            b.textContent = label;
            b.addEventListener('click', (e) => {
                e.stopPropagation();
                this._closeMenu();
                this._insert(node, mode, kind);
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
            this._rerender(node);
            this._emit();
        });
        picker.click();
    }
}
