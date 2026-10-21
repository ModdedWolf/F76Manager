const PUNCT = '{}[],:';

export function scan(text) {
    const src = String(text ?? '');
    const n = src.length;
    const tokens = [];
    let i = 0;
    while (i < n) {
        const c = src[i];
        if (c === ' ' || c === '\t' || c === '\n' || c === '\r' || c === '\uFEFF') {
            i++;
            continue;
        }
        const start = i;
        if (c === '"') {
            i++;
            while (i < n && src[i] !== '"' && src[i] !== '\n') i += src[i] === '\\' ? 2 : 1;
            if (i < n && src[i] === '"') i++;
            tokens.push({ type: 'string', start, end: Math.min(i, n) });
            continue;
        }
        if (c === '/' && src[i + 1] === '/') {
            while (i < n && src[i] !== '\n' && src[i] !== '\r') i++;
            tokens.push({ type: 'comment', block: false, start, end: i });
            continue;
        }
        if (c === '/' && src[i + 1] === '*') {
            const close = src.indexOf('*/', i + 2);
            i = close < 0 ? n : close + 2;
            tokens.push({ type: 'comment', block: true, start, end: i });
            continue;
        }
        if (PUNCT.includes(c)) {
            i++;
            tokens.push({ type: 'punct', start, end: i });
            continue;
        }
        while (i < n && !PUNCT.includes(src[i]) && !' \t\n\r"/'.includes(src[i])) i++;
        if (i === start) i++;
        tokens.push({ type: 'literal', start, end: i });
    }
    return tokens;
}

function significant(tokens) {
    return tokens.filter((t) => t.type !== 'comment');
}

export function strip(text) {
    const src = String(text ?? '');
    const tokens = scan(src);
    const sig = significant(tokens);
    const dropped = new Set();
    for (let k = 0; k < sig.length; k++) {
        const t = sig[k];
        if (src[t.start] !== ',' || t.type !== 'punct') continue;
        const next = sig[k + 1];
        if (!next || src[next.start] === '}' || src[next.start] === ']') dropped.add(t.start);
    }
    let out = '';
    let pos = 0;
    for (const t of tokens) {
        const isComment = t.type === 'comment';
        if (!isComment && !dropped.has(t.start)) continue;
        out += src.slice(pos, t.start);
        out += src.slice(t.start, t.end).replace(/[^\r\n]/g, ' ');
        pos = t.end;
    }
    return out + src.slice(pos);
}

export function parse(text) {
    return JSON.parse(strip(text));
}

export function parseTree(text) {
    const src = String(text ?? '');
    const sig = significant(scan(src));
    let k = 0;
    const ch = (t) => (t ? src[t.start] : '');

    const parseValue = () => {
        const t = sig[k];
        if (!t) throw new Error('Unexpected end of input');
        if (t.type === 'punct' && ch(t) === '{') return parseContainer('object', '}');
        if (t.type === 'punct' && ch(t) === '[') return parseContainer('array', ']');
        if (t.type === 'punct') throw new Error(`Unexpected '${ch(t)}' at ${t.start}`);
        k++;
        return { type: 'value', start: t.start, end: t.end };
    };

    const parseContainer = (type, close) => {
        const open = sig[k++];
        const node = { type, start: open.start, end: -1, closeStart: -1, children: [] };
        for (;;) {
            const t = sig[k];
            if (!t) throw new Error('Unterminated container');
            if (t.type === 'punct' && ch(t) === close) {
                node.closeStart = t.start;
                node.end = t.end;
                k++;
                return node;
            }
            const child = { start: t.start, end: -1, comma: -1 };
            if (type === 'object') {
                if (t.type !== 'string') throw new Error(`Expected property name at ${t.start}`);
                child.keyStart = t.start;
                child.keyEnd = t.end;
                child.key = JSON.parse(src.slice(t.start, t.end));
                k++;
                const colon = sig[k];
                if (!colon || ch(colon) !== ':') throw new Error(`Expected ':' at ${colon ? colon.start : src.length}`);
                child.colonEnd = colon.end;
                k++;
            }
            child.value = parseValue();
            child.end = child.value.end;
            node.children.push(child);
            const sep = sig[k];
            if (sep && sep.type === 'punct' && ch(sep) === ',') {
                child.comma = sep.start;
                k++;
            } else if (!sep || ch(sep) !== close) {
                throw new Error(`Expected ',' or '${close}' at ${sep ? sep.start : src.length}`);
            }
        }
    };

    try {
        const root = parseValue();
        if (k !== sig.length) return null;
        return root;
    } catch (_) {
        return null;
    }
}

export function hasComments(text) {
    return scan(text).some((t) => t.type === 'comment');
}

export function commentMap(text) {
    const src = String(text ?? '');
    const map = new Map();
    const comments = scan(src).filter((t) => t.type === 'comment');
    if (!comments.length) return map;
    const root = parseTree(src);
    if (!root) return map;

    const slot = (path) => {
        const k = JSON.stringify(path);
        let e = map.get(k);
        if (!e) {
            e = { leading: [], trailing: [], footer: [] };
            map.set(k, e);
        }
        return e;
    };
    const within = (a, b) => comments.filter((c) => c.start >= a && c.end <= b);
    const sameLine = (a, b) => !/[\r\n]/.test(src.slice(a, b));
    const raw = (c) => src.slice(c.start, c.end);

    const walk = (node, path) => {
        if (node.type === 'value') return;
        let prevEnd = node.start + 1;
        let prevPath = null;
        node.children.forEach((child, i) => {
            const childPath = [...path, node.type === 'array' ? i : child.key];
            for (const c of within(prevEnd, child.start)) {
                if (prevPath && sameLine(prevEnd, c.start)) slot(prevPath).trailing.push(raw(c));
                else slot(childPath).leading.push(raw(c));
            }
            if (node.type === 'object') {
                for (const c of within(child.keyEnd, child.value.start)) slot(childPath).trailing.push(raw(c));
            }
            walk(child.value, childPath);
            if (child.comma >= 0) {
                for (const c of within(child.end, child.comma)) slot(childPath).trailing.push(raw(c));
                prevEnd = child.comma + 1;
            } else {
                prevEnd = child.end;
            }
            prevPath = childPath;
        });
        for (const c of within(prevEnd, node.closeStart)) {
            if (prevPath && sameLine(prevEnd, c.start)) slot(prevPath).trailing.push(raw(c));
            else slot(path).footer.push(raw(c));
        }
    };

    for (const c of within(0, root.start)) slot([]).leading.push(raw(c));
    walk(root, []);
    for (const c of within(root.end, src.length)) slot([]).footer.push(raw(c));
    return map;
}

function findNode(root, path) {
    let node = root;
    let entry = null;
    let parent = null;
    for (const seg of path) {
        if (!node || node.type === 'value') return null;
        let idx = -1;
        if (node.type === 'array') {
            idx = typeof seg === 'number' ? seg : -1;
        } else {
            for (let j = node.children.length - 1; j >= 0; j--) {
                if (node.children[j].key === String(seg)) { idx = j; break; }
            }
        }
        if (idx < 0 || idx >= node.children.length) return null;
        parent = node;
        entry = node.children[idx];
        entry.index = idx;
        node = entry.value;
    }
    return { node, entry, parent };
}

function detectEol(src) {
    return src.includes('\r\n') ? '\r\n' : '\n';
}

function lineStartOf(src, pos) {
    let i = pos;
    while (i > 0 && src[i - 1] !== '\n' && src[i - 1] !== '\r') i--;
    return i;
}

function lineEndOf(src, pos) {
    let i = pos;
    while (i < src.length && src[i] !== '\n' && src[i] !== '\r') i++;
    return i;
}

function indentAt(src, pos) {
    const ls = lineStartOf(src, pos);
    let i = ls;
    while (i < src.length && (src[i] === ' ' || src[i] === '\t')) i++;
    return src.slice(ls, i);
}

function startsLine(src, pos) {
    return /^[ \t]*$/.test(src.slice(lineStartOf(src, pos), pos));
}

function detectIndentUnit(src) {
    let best = null;
    for (const line of src.split(/\r?\n/)) {
        const m = /^([ \t]+)([^\s*])/.exec(line);
        if (!m) continue;
        if (m[1].startsWith('\t')) return '\t';
        if (best === null || m[1].length < best.length) best = m[1];
    }
    return best || '  ';
}

function serialize(value, baseIndent, unit, eol) {
    const json = JSON.stringify(value, null, unit);
    if (json === undefined) return 'null';
    return json.split('\n').join(eol + baseIndent);
}

function splice(src, edits) {
    const sorted = edits.map((e, idx) => ({ ...e, idx })).sort((a, b) => b.start - a.start || b.idx - a.idx);
    let out = src;
    for (const e of sorted) out = out.slice(0, e.start) + (e.text || '') + out.slice(e.end);
    return out;
}

function expandToLine(src, start, end) {
    const le = lineEndOf(src, end);
    const atLineStart = startsLine(src, start);
    if (!atLineStart || !/^[ \t]*$/.test(src.slice(end, le))) {
        let e = end;
        if (atLineStart || src[start - 1] === ' ' || src[start - 1] === '\t') {
            while (e < le && (src[e] === ' ' || src[e] === '\t')) e++;
        }
        return { start, end: e };
    }
    let after = le;
    if (src[after] === '\r') after++;
    if (src[after] === '\n') after++;
    return { start: lineStartOf(src, start), end: after };
}

function trailingCommentEnd(tokens, src, pos) {
    const le = lineEndOf(src, pos);
    let end = pos;
    for (const t of tokens) {
        if (t.start < end) continue;
        if (t.type !== 'comment' || t.start >= le || !/^[ \t]*$/.test(src.slice(end, t.start))) break;
        if (t.end > le) break;
        end = t.end;
    }
    return end;
}

function applyRemove(src, found) {
    const { parent, entry } = found;
    const i = entry.index;
    const edits = [];
    if (entry.comma >= 0) {
        edits.push(expandToLine(src, entry.start, entry.comma + 1));
    } else {
        if (i > 0) {
            const prev = parent.children[i - 1];
            edits.push({ start: prev.comma, end: prev.comma + 1 });
        }
        edits.push(expandToLine(src, entry.start, entry.end));
    }
    return splice(src, edits);
}

function applyInsert(src, container, index, key, value) {
    const eol = detectEol(src);
    const unit = detectIndentUnit(src);
    const tokens = scan(src);
    const kids = container.children;
    const isObject = container.type === 'object';
    let sep = ': ';
    if (isObject && kids.length) {
        const k0 = kids[0];
        sep = src.slice(k0.keyEnd, k0.value.start).replace(/\r?\n[\s\S]*/, '') || ': ';
        if (!sep.includes(':')) sep = ': ';
    }
    const lineMode = kids.length ? startsLine(src, kids[0].start) : true;
    const childIndent = kids.length && lineMode ? indentAt(src, kids[0].start) : indentAt(src, container.start) + unit;
    const item = (isObject ? JSON.stringify(String(key)) + sep : '') + serialize(value, childIndent, unit, eol);

    if (!kids.length) {
        const inner = src.slice(container.start + 1, container.closeStart);
        if (/^\s*$/.test(inner)) {
            return splice(src, [{
                start: container.start + 1,
                end: container.closeStart,
                text: eol + childIndent + item + eol + indentAt(src, container.start),
            }]);
        }
        return splice(src, [{ start: container.start + 1, end: container.start + 1, text: eol + childIndent + item }]);
    }

    const trailingStyle = kids[kids.length - 1].comma >= 0;
    if (index >= kids.length) {
        const last = kids[kids.length - 1];
        const anchor = last.comma >= 0 ? last.comma + 1 : last.end;
        const edits = [];
        if (last.comma < 0) edits.push({ start: last.end, end: last.end, text: ',' });
        if (lineMode) {
            const pos = trailingCommentEnd(tokens, src, anchor);
            edits.push({ start: pos, end: pos, text: eol + childIndent + item + (trailingStyle ? ',' : '') });
        } else {
            edits.push({ start: anchor, end: anchor, text: ' ' + item + (trailingStyle ? ',' : '') });
        }
        return splice(src, edits);
    }

    const target = kids[Math.max(0, index)];
    if (!lineMode) {
        return splice(src, [{ start: target.start, end: target.start, text: item + ', ' }]);
    }
    const prevEnd = index > 0 ? kids[index - 1].comma + 1 : container.start + 1;
    const afterPrev = lineEndOf(src, trailingCommentEnd(tokens, src, prevEnd));
    let pos = afterPrev;
    if (src[pos] === '\r') pos++;
    if (src[pos] === '\n') pos++;
    const targetLine = lineStartOf(src, target.start);
    if (pos > targetLine || pos <= prevEnd) pos = targetLine;
    return splice(src, [{ start: pos, end: pos, text: childIndent + item + ',' + eol }]);
}

function applyReorder(src, container, order) {
    const kids = container.children;
    const n = kids.length;
    if (container.type !== 'array' || !Array.isArray(order) || order.length !== n) return null;
    if (new Set(order).size !== n || order.some((i) => !Number.isInteger(i) || i < 0 || i >= n)) return null;
    if (order.every((v, i) => v === i)) return src;

    const tokens = scan(src);
    const comments = tokens.filter((t) => t.type === 'comment');
    const sameLine = (a, b) => !/[\r\n]/.test(src.slice(a, b));
    const bodies = [];
    const tails = [];
    let prevEnd = container.start + 1;
    for (let i = 0; i < n; i++) {
        const kid = kids[i];
        let start = kid.start;
        for (const c of comments) {
            if (c.start < prevEnd || c.end > kid.start) continue;
            if (i > 0 && sameLine(prevEnd, c.start)) continue;
            start = c.start;
            break;
        }
        let end = kid.end;
        if (kid.comma >= 0) {
            for (const c of comments) {
                if (c.start >= kid.end && c.end <= kid.comma) end = Math.max(end, c.end);
            }
        }
        const tailStart = kid.comma >= 0 ? kid.comma + 1 : end;
        const tailEnd = trailingCommentEnd(tokens, src, tailStart);
        bodies.push({ start, end });
        tails.push({ start: tailStart, end: tailEnd });
        prevEnd = kid.comma >= 0 ? kid.comma + 1 : kid.end;
    }

    const edits = [];
    for (let i = 0; i < n; i++) {
        const from = order[i];
        edits.push({ start: bodies[i].start, end: bodies[i].end, text: src.slice(bodies[from].start, bodies[from].end) });
        edits.push({ start: tails[i].start, end: tails[i].end, text: src.slice(tails[from].start, tails[from].end) });
    }
    return splice(src, edits);
}

export function applyEdit(text, edit) {
    const src = String(text ?? '');
    if (!edit || !Array.isArray(edit.path)) return null;
    const root = parseTree(src);
    if (!root) return null;
    const found = findNode(root, edit.path);
    if (!found) return null;

    switch (edit.op) {
        case 'set': {
            const { node } = found;
            const eol = detectEol(src);
            const unit = detectIndentUnit(src);
            return splice(src, [{ start: node.start, end: node.end, text: serialize(edit.value, indentAt(src, node.start), unit, eol) }]);
        }
        case 'rename': {
            const { entry, parent } = found;
            if (!entry || !parent || parent.type !== 'object') return null;
            return splice(src, [{ start: entry.keyStart, end: entry.keyEnd, text: JSON.stringify(String(edit.newKey)) }]);
        }
        case 'remove': {
            if (!found.entry) return null;
            return applyRemove(src, found);
        }
        case 'insert': {
            const container = found.node;
            if (!container || container.type === 'value') return null;
            if (container.type === 'object' && (edit.key === undefined || edit.key === null)) return null;
            const index = Number.isInteger(edit.index) ? edit.index : container.children.length;
            return applyInsert(src, container, index, edit.key, edit.value);
        }
        case 'reorder': {
            if (!found.node || found.node.type !== 'array') return null;
            return applyReorder(src, found.node, edit.order);
        }
        default:
            return null;
    }
}

export function format(text, indent = 2) {
    const src = String(text ?? '');
    const tokens = scan(src);
    const eol = detectEol(src);
    const unit = typeof indent === 'number' ? ' '.repeat(indent) : String(indent);
    let out = '';
    let depth = 0;
    let breakPending = false;
    let prevEnd = -1;
    let blank = false;

    const newline = () => {
        out = out.replace(/[ \t]+$/, '');
        out += (blank ? eol : '') + eol + unit.repeat(depth);
    };

    for (let k = 0; k < tokens.length; k++) {
        const t = tokens[k];
        const s = src.slice(t.start, t.end);
        const gap = prevEnd >= 0 ? src.slice(prevEnd, t.start) : '';
        const sameLine = prevEnd >= 0 && !/[\r\n]/.test(gap);
        blank = /\n[ \t]*\r?\n/.test(gap);

        if (t.type === 'comment') {
            const inline = out !== '' && sameLine && breakPending !== 'force';
            if (inline) out += (out.endsWith(' ') ? '' : ' ') + s;
            else if (out === '') out += s;
            else {
                newline();
                const oldIndent = indentAt(src, t.start);
                const newIndent = unit.repeat(depth);
                out += !t.block || !startsLine(src, t.start)
                    ? s
                    : s.split(/\r?\n/).map((line, i) => (i > 0 && line.startsWith(oldIndent) ? newIndent + line.slice(oldIndent.length) : line)).join(eol);
            }
            if (!t.block) breakPending = 'force';
            else if (!inline) breakPending = true;
            else out += ' ';
            prevEnd = t.end;
            continue;
        }

        const c = s[0];
        if (t.type === 'punct' && (c === '}' || c === ']')) {
            depth = Math.max(0, depth - 1);
            const last = out.slice(-1);
            if (!((last === '{' || last === '[') && breakPending !== 'force')) newline();
            out += c;
            breakPending = false;
        } else if (t.type === 'punct' && c === ',') {
            if (breakPending === 'force') newline();
            out += ',';
            breakPending = true;
        } else if (t.type === 'punct' && c === ':') {
            if (breakPending === 'force') newline();
            out += ': ';
            breakPending = false;
        } else {
            if (breakPending && out !== '') newline();
            out += s;
            if (t.type === 'punct') depth++;
            breakPending = t.type === 'punct';
        }
        prevEnd = t.end;
    }
    return out.replace(/[ \t]+$/, '') + (/\r?\n$/.test(src) ? eol : '');
}
