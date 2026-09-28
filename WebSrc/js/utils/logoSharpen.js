const ALPHA_THRESHOLD = 8;
const MIN_LONG_SIDE = 320;
const RENDER_CACHE_LIMIT = 24;
const SHARPEN_AMOUNT = 0.35;
const REFRESH_DEBOUNCE_MS = 100;

const trimCache = new Map();
const prepareCache = new Map();
const renderCache = new Map();

const requestSeq = new WeakMap();

function isAnimatedCandidate(url) {
    const u = String(url || '').toLowerCase();
    if (u.startsWith('data:')) {
        return u.startsWith('data:image/gif') || u.startsWith('data:image/webp');
    }
    const path = u.split(/[?#]/)[0];
    return path.endsWith('.gif') || path.endsWith('.webp');
}

function findOpaqueBounds(ctx, width, height) {
    const { data } = ctx.getImageData(0, 0, width, height);
    let minX = width;
    let minY = height;
    let maxX = -1;
    let maxY = -1;
    for (let y = 0; y < height; y++) {
        const row = y * width * 4;
        for (let x = 0; x < width; x++) {
            if (data[row + x * 4 + 3] > ALPHA_THRESHOLD) {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
    }
    if (maxX < 0) return null;
    return { x: minX, y: minY, w: maxX - minX + 1, h: maxY - minY + 1 };
}

function makeCanvas(w, h) {
    const c = document.createElement('canvas');
    c.width = Math.max(1, Math.round(w));
    c.height = Math.max(1, Math.round(h));
    return c;
}

function canvasToBlobUrl(canvas) {
    return new Promise((resolve) => canvas.toBlob(resolve, 'image/png'))
        .then((blob) => (blob ? URL.createObjectURL(blob) : null));
}

async function decodeAndTrim(url) {
    const img = new Image();
    img.decoding = 'async';
    img.src = url;
    await img.decode();

    const srcW = img.naturalWidth;
    const srcH = img.naturalHeight;
    if (!srcW || !srcH) return null;

    const full = makeCanvas(srcW, srcH);
    const fullCtx = full.getContext('2d', { willReadFrequently: true });
    fullCtx.drawImage(img, 0, 0);

    const b = findOpaqueBounds(fullCtx, srcW, srcH);
    if (!b) return null;
    if (b.w === srcW && b.h === srcH) return full;

    const cropped = makeCanvas(b.w, b.h);
    cropped.getContext('2d').drawImage(full, b.x, b.y, b.w, b.h, 0, 0, b.w, b.h);
    return cropped;
}

function loadTrimmed(url) {
    let job = trimCache.get(url);
    if (!job) {
        job = decodeAndTrim(url).catch(() => null);
        trimCache.set(url, job);
    }
    return job;
}

function halveToward(src, targetW, targetH) {
    let cur = src;
    while (cur.width / 2 >= targetW && cur.height / 2 >= targetH && cur.width >= 2 && cur.height >= 2) {
        const next = makeCanvas(cur.width / 2, cur.height / 2);
        const ctx = next.getContext('2d');
        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = 'high';
        ctx.drawImage(cur, 0, 0, next.width, next.height);
        cur = next;
    }
    return cur;
}

function parsePositionToken(token) {
    const t = token.toLowerCase();
    if (t === 'left' || t === 'top') return { frac: 0, px: 0 };
    if (t === 'right' || t === 'bottom') return { frac: 1, px: 0 };
    if (t === 'center') return { frac: 0.5, px: 0 };
    if (t.endsWith('%')) {
        const n = parseFloat(t);
        return Number.isFinite(n) ? { frac: n / 100, px: 0 } : null;
    }
    if (t.endsWith('px')) {
        const n = parseFloat(t);
        return Number.isFinite(n) ? { frac: 0, px: n } : null;
    }
    const n = parseFloat(t);
    return Number.isFinite(n) && n === 0 ? { frac: 0, px: 0 } : null;
}

function parseObjectPosition(value) {
    const center = { frac: 0.5, px: 0 };
    const tokens = String(value || '').trim().split(/\s+/).filter(Boolean);
    if (!tokens.length) return { x: center, y: { frac: 1, px: 0 } };

    let x = null;
    let y = null;
    const pending = [];
    for (const tok of tokens.slice(0, 2)) {
        const lower = tok.toLowerCase();
        if (lower === 'left' || lower === 'right') x = parsePositionToken(tok);
        else if (lower === 'top' || lower === 'bottom') y = parsePositionToken(tok);
        else pending.push(parsePositionToken(tok));
    }
    for (const p of pending) {
        if (!p) continue;
        if (!x) x = p;
        else if (!y) y = p;
    }
    return { x: x || center, y: y || center };
}

function computePlacement(srcW, srcH, boxW, boxH, fit, position, dpr) {
    let dw;
    let dh;
    const contain = Math.min(boxW / srcW, boxH / srcH);
    switch (fit) {
        case 'fill':
            dw = boxW;
            dh = boxH;
            break;
        case 'contain':
            dw = srcW * contain;
            dh = srcH * contain;
            break;
        case 'none':
            dw = srcW * dpr;
            dh = srcH * dpr;
            break;
        case 'scale-down': {
            const s = Math.min(contain, dpr);
            dw = srcW * s;
            dh = srcH * s;
            break;
        }
        case 'cover':
        default: {
            const s = Math.max(boxW / srcW, boxH / srcH);
            dw = srcW * s;
            dh = srcH * s;
            break;
        }
    }
    const pos = parseObjectPosition(position);
    const dx = (boxW - dw) * pos.x.frac + pos.x.px * dpr;
    const dy = (boxH - dh) * pos.y.frac + pos.y.px * dpr;
    return { dx, dy, dw, dh };
}

function sharpen(canvas, amount) {
    const w = canvas.width;
    const h = canvas.height;
    if (w < 3 || h < 3 || amount <= 0) return;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    const image = ctx.getImageData(0, 0, w, h);
    const src = image.data;
    const out = new Uint8ClampedArray(src);

    for (let y = 0; y < h; y++) {
        for (let x = 0; x < w; x++) {
            const i = (y * w + x) * 4;
            if (src[i + 3] === 0) continue;
            let r = 0;
            let g = 0;
            let b = 0;
            let aSum = 0;
            for (let oy = -1; oy <= 1; oy++) {
                const yy = Math.min(h - 1, Math.max(0, y + oy));
                for (let ox = -1; ox <= 1; ox++) {
                    const xx = Math.min(w - 1, Math.max(0, x + ox));
                    const j = (yy * w + xx) * 4;
                    const a = src[j + 3];
                    r += src[j] * a;
                    g += src[j + 1] * a;
                    b += src[j + 2] * a;
                    aSum += a;
                }
            }
            if (aSum === 0) continue;
            out[i] = src[i] + amount * (src[i] - r / aSum);
            out[i + 1] = src[i + 1] + amount * (src[i + 1] - g / aSum);
            out[i + 2] = src[i + 2] + amount * (src[i + 2] - b / aSum);
        }
    }
    image.data.set(out);
    ctx.putImageData(image, 0, 0);
}

async function renderExact(url, w, h, fit, position, dpr) {
    const trimmed = await loadTrimmed(url);
    if (!trimmed) return url;

    const place = computePlacement(trimmed.width, trimmed.height, w, h, fit, position, dpr);
    const source = halveToward(trimmed, place.dw, place.dh);

    const out = makeCanvas(w, h);
    const ctx = out.getContext('2d', { willReadFrequently: true });
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(source, 0, 0, source.width, source.height, place.dx, place.dy, place.dw, place.dh);
    sharpen(out, SHARPEN_AMOUNT);

    return (await canvasToBlobUrl(out)) || url;
}

function isBlobInUse(blobUrl) {
    for (const img of document.querySelectorAll('img')) {
        if (img.getAttribute('src') === blobUrl) return true;
    }
    return false;
}

function evictRenderCache() {
    while (renderCache.size > RENDER_CACHE_LIMIT) {
        const [oldKey, oldJob] = renderCache.entries().next().value;
        renderCache.delete(oldKey);
        oldJob.then((blobUrl) => {
            if (blobUrl && blobUrl.startsWith('blob:') && !isBlobInUse(blobUrl)) {
                URL.revokeObjectURL(blobUrl);
            }
        });
    }
}

export function renderLogo(url, { w, h, fit = 'cover', position = 'center bottom', dpr = 1 }) {
    const key = `${url}|${w}x${h}|${fit}|${position}|${dpr}`;
    let job = renderCache.get(key);
    if (job) {
        renderCache.delete(key);
        renderCache.set(key, job);
        return job;
    }
    job = renderExact(url, w, h, fit, position, dpr).catch(() => url);
    renderCache.set(key, job);
    evictRenderCache();
    return job;
}

async function prepareFallback(url) {
    const trimmed = await loadTrimmed(url);
    if (!trimmed) return url;
    const longSide = Math.max(trimmed.width, trimmed.height);
    const scale = Math.min(1, MIN_LONG_SIDE / longSide);
    const shrunk = halveToward(trimmed, trimmed.width * scale, trimmed.height * scale);
    return (await canvasToBlobUrl(shrunk)) || url;
}

export function prepareLogo(url) {
    if (!url || isAnimatedCandidate(url)) return Promise.resolve(url);
    let job = prepareCache.get(url);
    if (!job) {
        job = prepareFallback(url).catch(() => url);
        prepareCache.set(url, job);
    }
    return job;
}

function nextFrame() {
    return new Promise((resolve) => requestAnimationFrame(() => resolve()));
}

function readTransformScale(cs) {
    const t = cs.transform;
    if (!t || t === 'none') return 1;
    const m = t.match(/^matrix\(\s*([-\d.e]+)\s*,\s*([-\d.e]+)/i);
    if (m) {
        const a = parseFloat(m[1]);
        const b = parseFloat(m[2]);
        const s = Math.hypot(a, b);
        return Number.isFinite(s) && s > 0 ? s : 1;
    }
    return 1;
}

function measureTarget(img) {
    const cssW = img.clientWidth;
    const cssH = img.clientHeight;
    if (!cssW || !cssH) return null;
    const cs = getComputedStyle(img);
    const scale = readTransformScale(cs);
    const dpr = window.devicePixelRatio || 1;
    const w = Math.round(cssW * scale * dpr);
    const h = Math.round(cssH * scale * dpr);
    if (w < 1 || h < 1) return null;
    const fit = (cs.getPropertyValue('--logo-object-fit').trim() || 'cover').toLowerCase();
    const position = cs.getPropertyValue('--logo-object-position').trim() || 'center bottom';
    return { w, h, fit, position, dpr };
}

let dprQuery = null;

function onDprChange() {
    watchDpr();
    refreshSharpLogos();
}

function watchDpr() {
    if (typeof window.matchMedia !== 'function') return;
    if (dprQuery) dprQuery.removeEventListener('change', onDprChange);
    dprQuery = window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
    dprQuery.addEventListener('change', onDprChange);
}

export async function setSharpLogo(img, url) {
    if (!img || !url) return;
    if (!dprQuery) watchDpr();

    const seq = (requestSeq.get(img) || 0) + 1;
    requestSeq.set(img, seq);
    img.dataset.logoRequest = url;

    const isCurrent = () => requestSeq.get(img) === seq && img.dataset.logoRequest === url;
    const apply = (display, sharp) => {
        if (!isCurrent()) return;
        if (img.getAttribute('src') !== display) img.src = display;
        if (sharp) img.dataset.sharp = '1';
        else delete img.dataset.sharp;
    };

    if (isAnimatedCandidate(url)) {
        apply(url, false);
        return;
    }

    await nextFrame();
    if (!isCurrent()) return;

    const target = measureTarget(img);
    if (!target) {
        const display = await prepareLogo(url);
        apply(display, false);
        return;
    }

    const display = await renderLogo(url, target);
    apply(display, display !== url);
}

let refreshTimer = 0;

export function refreshSharpLogos() {
    if (refreshTimer) clearTimeout(refreshTimer);
    refreshTimer = setTimeout(() => {
        refreshTimer = 0;
        document.querySelectorAll('img[data-logo-request]').forEach((img) => {
            setSharpLogo(img, img.dataset.logoRequest);
        });
    }, REFRESH_DEBOUNCE_MS);
}
