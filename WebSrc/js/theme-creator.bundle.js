(() => {
  // ../../WebSrc/js/Translations.js
  var Translator = class {
    constructor() {
      this.currentLanguage = "en-US";
      this.loadedLanguages = {};
      this.fallbackLanguage = "en-US";
      this.pendingRequests = {};
    }
    async init() {
      try {
        await this.loadLanguage(this.fallbackLanguage);
      } catch (e) {
        console.error("[Translator] Critical: Failed to load fallback language.", e);
      }
      const boot2 = typeof window.__F76_BOOT_LANGUAGE === "string" ? window.__F76_BOOT_LANGUAGE : "";
      const detected = boot2 && boot2 !== "auto" ? boot2 : this.detectLanguage();
      if (detected !== this.fallbackLanguage) {
        try {
          await this.loadLanguage(detected);
          this.currentLanguage = detected;
        } catch (e) {
          console.warn("[Translator] Failed to load detected language, sticking to fallback.", e);
        }
      }
      this.applyDocumentLanguage();
    }
    applyDocumentLanguage() {
      if (typeof document === "undefined" || !document.documentElement) return;
      const lang = String(this.currentLanguage || this.fallbackLanguage);
      document.documentElement.lang = lang === "template-LANG" ? this.fallbackLanguage : lang;
    }
    async setLanguage(lang) {
      if (!lang) return;
      if (lang === "auto") {
        lang = this.detectLanguage();
      }
      if (this.currentLanguage === lang && this.loadedLanguages[lang]) return;
      console.log(`[Translator] Switching to ${lang}...`);
      try {
        await this.loadLanguage(lang);
        this.currentLanguage = lang;
        console.log(`[Translator] Language set to ${lang}`);
      } catch (e) {
        console.error(`[Translator] Failed to load ${lang}, falling back to ${this.fallbackLanguage}`);
        this.currentLanguage = this.fallbackLanguage;
      }
      this.applyDocumentLanguage();
    }
    loadLanguage(lang) {
      if (this.loadedLanguages[lang]) return Promise.resolve(this.loadedLanguages[lang]);
      return new Promise((resolve, reject) => {
        this.pendingRequests[lang] = { resolve, reject };
        if (window.chrome && window.chrome.webview) {
          console.log(`[Translator] Requesting ${lang} from backend...`);
          let attempts = 0;
          const maxAttempts = 10;
          window.chrome.webview.postMessage({ type: "LOAD_LANGUAGE", lang });
          const interval = setInterval(() => {
            if (!this.pendingRequests[lang]) {
              clearInterval(interval);
              return;
            }
            attempts++;
            if (attempts >= maxAttempts) {
              clearInterval(interval);
              if (this.pendingRequests[lang]) {
                console.warn(`[Translator] Timeout loading ${lang} after ${maxAttempts} attempts`);
                delete this.pendingRequests[lang];
                reject(new Error("Timeout"));
              }
            } else {
              console.log(`[Translator] Retry ${attempts}/${maxAttempts} for ${lang}...`);
              window.chrome.webview.postMessage({ type: "LOAD_LANGUAGE", lang });
            }
          }, 500);
        } else {
          console.warn("[Translator] No backend detected, trying fetch fallback...");
          fetch(`locales/${lang}.json`).then((r) => r.json()).then((json) => {
            this.loadedLanguages[lang] = json;
            delete this.pendingRequests[lang];
            resolve(json);
          }).catch(reject);
        }
      });
    }
    receiveLanguageContent(lang, content, error) {
      const req = this.pendingRequests[lang];
      if (!req) return;
      if (error) {
        console.error(`[Translator] Backend reported error for ${lang}:`, error);
        req.reject(new Error(error));
      } else {
        this.loadedLanguages[lang] = content;
        req.resolve(content);
      }
      delete this.pendingRequests[lang];
    }
    detectLanguage() {
      const navLang = navigator.language || navigator.userLanguage || "en-US";
      if (navLang.startsWith("en")) return "en-US";
      if (navLang.startsWith("ja")) return "ja-JP";
      if (navLang.startsWith("fr")) return "fr-FR";
      if (navLang.startsWith("de")) return "de-DE";
      if (navLang.startsWith("es")) return "es-ES";
      if (navLang.startsWith("it")) return "it-IT";
      if (navLang.startsWith("pl")) return "pl-PL";
      if (navLang.startsWith("ru")) return "ru-RU";
      if (navLang.startsWith("pt")) return "pt-BR";
      if (navLang.startsWith("zh")) return navLang.toLowerCase().includes("tw") || navLang.toLowerCase().includes("hk") ? "zh-TW" : "zh-CN";
      if (navLang.startsWith("ko")) return "ko-KR";
      return "en-US";
    }
    t(key, ...args) {
      let text = null;
      if (this.loadedLanguages[this.currentLanguage]) {
        text = this.loadedLanguages[this.currentLanguage][key];
      }
      if (!text && this.loadedLanguages[this.fallbackLanguage]) {
        text = this.loadedLanguages[this.fallbackLanguage][key];
      }
      if (!text) text = key;
      if (args.length > 0) {
        args.forEach((arg, i) => {
          text = text.split(`{${i}}`).join(String(arg));
        });
      }
      return text;
    }
  };
  var translator = new Translator();

  // ../../WebSrc/js/utils/appConfirm.js
  var _confirmPendingResolve = null;
  var _confirmCleanup = null;
  function _escapeConfirmHtml(value) {
    return String(value ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  }
  function _formatConfirmMessageHtml(message) {
    const raw = String(message ?? "");
    const re = /["'«「]([^"'»」]+)["'»」]/g;
    let last = 0;
    let match;
    const parts = [];
    while ((match = re.exec(raw)) !== null) {
      if (match.index > last) {
        parts.push(_escapeConfirmHtml(raw.slice(last, match.index)));
      }
      parts.push(`<code>${_escapeConfirmHtml(match[1])}</code>`);
      last = match.index + match[0].length;
    }
    if (last < raw.length) {
      parts.push(_escapeConfirmHtml(raw.slice(last)));
    }
    return parts.join("").replace(/\n/g, "<br>");
  }
  function dismissAppConfirm(result = false) {
    const pending = _confirmPendingResolve;
    const cleanup = _confirmCleanup;
    _confirmPendingResolve = null;
    _confirmCleanup = null;
    try {
      cleanup?.();
    } catch (_) {
    }
    const overlay = document.getElementById("confirm-modal");
    if (overlay) overlay.classList.remove("active", "confirm-modal--compact");
    const altBtn = document.getElementById("confirm-alt");
    if (altBtn) altBtn.hidden = true;
    const promptClear = document.getElementById("confirm-prompt-clear");
    if (promptClear) promptClear.hidden = true;
    if (pending) {
      try {
        pending(result);
      } catch (_) {
      }
    }
  }
  function appConfirm(options = {}) {
    return new Promise((resolve) => {
      const overlay = document.getElementById("confirm-modal");
      const titleEl = document.getElementById("confirm-title");
      const msgEl = document.getElementById("confirm-message");
      const iconEl = document.getElementById("confirm-icon");
      const okBtn = document.getElementById("confirm-ok");
      const cancelBtn = document.getElementById("confirm-cancel");
      const altBtn = document.getElementById("confirm-alt");
      const closeTopBtn = document.getElementById("confirm-close-top");
      const promptWrap = document.getElementById("confirm-prompt-wrap");
      const promptInput = document.getElementById("confirm-prompt-input");
      const promptClear = document.getElementById("confirm-prompt-clear");
      const promptLabel = document.getElementById("confirm-prompt-label");
      if (!overlay || !titleEl || !msgEl || !okBtn || !cancelBtn) {
        console.warn("[appConfirm] #confirm-modal elements missing");
        resolve(false);
        return;
      }
      dismissAppConfirm(false);
      const t2 = window._t;
      const isDanger = !!options.danger;
      const isPrompt = !!options.prompt;
      overlay.classList.toggle("confirm-modal--compact", !!options.compact);
      titleEl.textContent = options.title ?? "Confirmation";
      msgEl.innerHTML = _formatConfirmMessageHtml(options.message ?? "");
      msgEl.style.whiteSpace = "normal";
      msgEl.hidden = isPrompt && !options.message;
      const syncPromptClear = () => {
        if (promptClear) promptClear.hidden = !isPrompt || !promptInput?.value;
      };
      if (promptWrap && promptInput) {
        if (isPrompt) {
          promptWrap.hidden = false;
          if (promptLabel) {
            promptLabel.textContent = options.promptLabel || "";
            promptLabel.hidden = !options.promptLabel;
          }
          promptInput.value = options.defaultValue ?? "";
          promptInput.placeholder = options.placeholder ?? "";
          promptInput.maxLength = options.maxLength || 120;
        } else {
          promptWrap.hidden = true;
          promptInput.value = "";
        }
        syncPromptClear();
      }
      okBtn.textContent = options.okText ?? options.confirmLabel ?? "OK";
      cancelBtn.textContent = options.cancelText ?? options.cancelLabel ?? (typeof t2 === "function" ? t2("cancel") : "Cancel");
      if (closeTopBtn) {
        closeTopBtn.setAttribute("aria-label", cancelBtn.textContent);
      }
      const hasAlt = !!(altBtn && options.altText && !isPrompt);
      if (altBtn) {
        altBtn.hidden = !hasAlt;
        altBtn.textContent = hasAlt ? options.altText : "";
      }
      if (iconEl) {
        iconEl.setAttribute("data-lucide", isDanger ? "trash-2" : isPrompt ? "pencil" : "help-circle");
      }
      const previousFocus = document.activeElement;
      const cleanupListeners = () => {
        okBtn.removeEventListener("click", onOk);
        cancelBtn.removeEventListener("click", onCancel);
        if (altBtn) altBtn.removeEventListener("click", onAlt);
        if (closeTopBtn) closeTopBtn.removeEventListener("click", onCancel);
        overlay.removeEventListener("click", onOverlayClick);
        document.removeEventListener("keydown", onKeydown);
        if (promptInput) {
          promptInput.removeEventListener("keydown", onPromptKeydown);
          promptInput.removeEventListener("input", onPromptInput);
        }
        if (promptClear) promptClear.removeEventListener("click", onClear);
      };
      const finish = (value) => {
        if (_confirmPendingResolve !== resolve) return;
        _confirmPendingResolve = null;
        _confirmCleanup = null;
        overlay.classList.remove("active", "confirm-modal--compact");
        if (promptWrap) promptWrap.hidden = true;
        if (msgEl) msgEl.hidden = false;
        if (altBtn) altBtn.hidden = true;
        if (promptClear) promptClear.hidden = true;
        cleanupListeners();
        resolve(value);
        try {
          if (previousFocus && typeof previousFocus.focus === "function") {
            previousFocus.focus();
          }
        } catch (_) {
        }
      };
      const onOk = () => {
        if (isPrompt) {
          const raw = (promptInput?.value ?? "").trim();
          if (!raw) {
            try {
              promptInput?.focus();
            } catch (_) {
            }
            return;
          }
          finish(raw);
          return;
        }
        finish(true);
      };
      const onCancel = () => finish(isPrompt ? null : false);
      const onAlt = () => finish("alt");
      const onOverlayClick = (e) => {
        if (e.target === overlay) finish(isPrompt ? null : false);
      };
      const onKeydown = (e) => {
        if (e.key === "Escape") {
          e.preventDefault();
          finish(isPrompt ? null : false);
        }
      };
      const onPromptKeydown = (e) => {
        if (e.key === "Enter") {
          e.preventDefault();
          onOk();
        }
      };
      const onPromptInput = () => syncPromptClear();
      const onClear = (e) => {
        e.preventDefault();
        if (!promptInput) return;
        promptInput.value = "";
        syncPromptClear();
        try {
          promptInput.focus();
        } catch (_) {
        }
      };
      _confirmPendingResolve = resolve;
      _confirmCleanup = cleanupListeners;
      okBtn.addEventListener("click", onOk);
      cancelBtn.addEventListener("click", onCancel);
      if (hasAlt) altBtn.addEventListener("click", onAlt);
      if (closeTopBtn) closeTopBtn.addEventListener("click", onCancel);
      overlay.addEventListener("click", onOverlayClick);
      document.addEventListener("keydown", onKeydown);
      if (isPrompt && promptInput) {
        promptInput.addEventListener("keydown", onPromptKeydown);
        promptInput.addEventListener("input", onPromptInput);
      }
      if (isPrompt && promptClear) promptClear.addEventListener("click", onClear);
      overlay.classList.add("active");
      if (window.lucide) {
        try {
          if (iconEl) {
            iconEl.parentElement?.querySelectorAll("svg").forEach((s) => s.remove());
          }
          window.lucide.createIcons({ nodes: [overlay] });
        } catch (_) {
        }
      }
      try {
        if (isPrompt && promptInput) {
          promptInput.focus();
          promptInput.select();
        } else {
          okBtn.focus();
        }
      } catch (_) {
      }
    });
  }
  function appPrompt(options = {}) {
    return appConfirm({
      ...options,
      prompt: true,
      title: options.title ?? (typeof window._t === "function" ? window._t("new_preset") : "Name"),
      message: options.message ?? "",
      okText: options.okText ?? (typeof window._t === "function" ? window._t("ok") || "OK" : "OK"),
      cancelText: options.cancelText ?? (typeof window._t === "function" ? window._t("cancel") : "Cancel")
    });
  }
  function installAppConfirmGlobals() {
    window.appConfirm = appConfirm;
    window.appPrompt = appPrompt;
    window.dismissAppConfirm = () => dismissAppConfirm(false);
  }

  // ../../WebSrc/js/themes/registry.js
  var UI_THEMES = [
    { id: "fallout", labelKey: "theme_fallout", logo: "assets/themes/fallout.png", logoLayout: { "width": 44, "height": 0, "scale": 1, "offsetX": 0, "offsetY": 0, "objectFit": "cover", "objectPosition": "center bottom", "opacity": 1, "shadowY": 4, "shadowBlur": 12, "shadowOpacity": 0.5, "collapsedScale": 1, "collapsedOffsetX": 0 } },
    { id: "vault-tec", labelKey: "theme_vault_tec", logo: "assets/themes/vault-tec.png", logoLayout: { "width": 44, "height": 0, "scale": 1, "offsetX": 0, "offsetY": 0, "objectFit": "cover", "objectPosition": "center bottom", "opacity": 1, "shadowY": 4, "shadowBlur": 12, "shadowOpacity": 0.5, "collapsedScale": 1, "collapsedOffsetX": 0 } },
    { id: "red-black", labelKey: "theme_red_black", logo: "assets/themes/red-black.png", logoLayout: { "width": 44, "height": 0, "scale": 1, "offsetX": 0, "offsetY": 0, "objectFit": "contain", "objectPosition": "center center", "opacity": 1, "shadowY": 4, "shadowBlur": 12, "shadowOpacity": 0.5, "collapsedScale": 1, "collapsedOffsetX": 0 } },
    { id: "black-white", labelKey: "theme_black_white", logo: "assets/themes/black-white.webp", logoLayout: { "width": 48, "height": 48, "scale": 1, "offsetX": 0, "offsetY": 2, "objectFit": "fill", "objectPosition": "center center", "opacity": 1, "shadowY": 4, "shadowBlur": 12, "shadowOpacity": 0.5, "collapsedScale": 1, "collapsedOffsetX": 0 } }
  ];
  var DEFAULT_UI_THEME = "fallout";
  var THEME_IDS = new Set(UI_THEMES.map((t2) => t2.id));
  function isBuiltInUiTheme(id) {
    return typeof id === "string" && THEME_IDS.has(id);
  }

  // ../../WebSrc/js/utils/logoLayout.js
  var DEFAULT_LOGO_LAYOUT = {
    width: 44,
    height: 0,
    scale: 1,
    offsetX: 0,
    offsetY: 0,
    objectFit: "cover",
    objectPosition: "center bottom",
    opacity: 1,
    shadowY: 4,
    shadowBlur: 12,
    shadowOpacity: 0.5,
    collapsedScale: 1,
    collapsedOffsetX: 0
  };
  var OBJECT_FITS = /* @__PURE__ */ new Set(["cover", "contain", "fill", "none", "scale-down"]);
  function normalizeLogoLayout(raw) {
    const base = { ...DEFAULT_LOGO_LAYOUT };
    if (!raw || typeof raw !== "object") return base;
    const n = (key, min, max, fallback) => {
      const v = Number(raw[key]);
      if (!Number.isFinite(v)) return fallback;
      return Math.min(max, Math.max(min, v));
    };
    base.width = n("width", 16, 120, base.width);
    base.height = n("height", 0, 120, base.height);
    base.scale = n("scale", 0.25, 3, base.scale);
    base.offsetX = n("offsetX", -40, 40, base.offsetX);
    base.offsetY = n("offsetY", -40, 40, base.offsetY);
    base.opacity = n("opacity", 0, 1, base.opacity);
    base.shadowY = n("shadowY", 0, 24, base.shadowY);
    base.shadowBlur = n("shadowBlur", 0, 48, base.shadowBlur);
    base.shadowOpacity = n("shadowOpacity", 0, 1, base.shadowOpacity);
    base.collapsedScale = n("collapsedScale", 0.25, 3, base.collapsedScale);
    base.collapsedOffsetX = n("collapsedOffsetX", -40, 40, base.collapsedOffsetX);
    const fit = String(raw.objectFit || base.objectFit).toLowerCase();
    base.objectFit = OBJECT_FITS.has(fit) ? fit : base.objectFit;
    const pos = String(raw.objectPosition || base.objectPosition).trim();
    base.objectPosition = pos || base.objectPosition;
    return base;
  }
  function applyLogoLayout(layout) {
    const L = normalizeLogoLayout(layout);
    const root = document.documentElement;
    root.style.setProperty("--logo-width", `${L.width}px`);
    root.style.setProperty("--logo-height", L.height > 0 ? `${L.height}px` : "var(--topbar-height)");
    root.style.setProperty("--logo-scale", String(L.scale));
    root.style.setProperty("--logo-offset-x", `${L.offsetX}px`);
    root.style.setProperty("--logo-offset-y", `${L.offsetY}px`);
    root.style.setProperty("--logo-object-fit", L.objectFit);
    root.style.setProperty("--logo-object-position", L.objectPosition);
    root.style.setProperty("--logo-opacity", String(L.opacity));
    root.style.setProperty("--logo-shadow-y", `${L.shadowY}px`);
    root.style.setProperty("--logo-shadow-blur", `${L.shadowBlur}px`);
    root.style.setProperty("--logo-shadow-opacity", String(L.shadowOpacity));
    root.style.setProperty("--logo-collapsed-scale", String(L.collapsedScale));
    root.style.setProperty("--logo-collapsed-offset-x", `${L.collapsedOffsetX}px`);
    root.style.setProperty("--logo-collapsed-margin", `${-L.width / 2}px`);
    return L;
  }
  function getLogoLayoutForTheme(themeId) {
    const entry = UI_THEMES.find((t2) => t2.id === themeId);
    return normalizeLogoLayout(entry?.logoLayout);
  }

  // ../../WebSrc/js/utils/logoSharpen.js
  var ALPHA_THRESHOLD = 8;
  var MIN_LONG_SIDE = 320;
  var RENDER_CACHE_LIMIT = 24;
  var SHARPEN_AMOUNT = 0.35;
  var REFRESH_DEBOUNCE_MS = 100;
  var trimCache = /* @__PURE__ */ new Map();
  var prepareCache = /* @__PURE__ */ new Map();
  var renderCache = /* @__PURE__ */ new Map();
  var requestSeq = /* @__PURE__ */ new WeakMap();
  function isAnimatedCandidate(url) {
    const u = String(url || "").toLowerCase();
    if (u.startsWith("data:")) {
      return u.startsWith("data:image/gif") || u.startsWith("data:image/webp");
    }
    const path = u.split(/[?#]/)[0];
    return path.endsWith(".gif") || path.endsWith(".webp");
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
    const c = document.createElement("canvas");
    c.width = Math.max(1, Math.round(w));
    c.height = Math.max(1, Math.round(h));
    return c;
  }
  function canvasToBlobUrl(canvas) {
    return new Promise((resolve) => canvas.toBlob(resolve, "image/png")).then((blob) => blob ? URL.createObjectURL(blob) : null);
  }
  async function decodeAndTrim(url) {
    const img = new Image();
    img.decoding = "async";
    img.src = url;
    await img.decode();
    const srcW = img.naturalWidth;
    const srcH = img.naturalHeight;
    if (!srcW || !srcH) return null;
    const full = makeCanvas(srcW, srcH);
    const fullCtx = full.getContext("2d", { willReadFrequently: true });
    fullCtx.drawImage(img, 0, 0);
    const b = findOpaqueBounds(fullCtx, srcW, srcH);
    if (!b) return null;
    if (b.w === srcW && b.h === srcH) return full;
    const cropped = makeCanvas(b.w, b.h);
    cropped.getContext("2d").drawImage(full, b.x, b.y, b.w, b.h, 0, 0, b.w, b.h);
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
      const ctx = next.getContext("2d");
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = "high";
      ctx.drawImage(cur, 0, 0, next.width, next.height);
      cur = next;
    }
    return cur;
  }
  function parsePositionToken(token) {
    const t2 = token.toLowerCase();
    if (t2 === "left" || t2 === "top") return { frac: 0, px: 0 };
    if (t2 === "right" || t2 === "bottom") return { frac: 1, px: 0 };
    if (t2 === "center") return { frac: 0.5, px: 0 };
    if (t2.endsWith("%")) {
      const n2 = parseFloat(t2);
      return Number.isFinite(n2) ? { frac: n2 / 100, px: 0 } : null;
    }
    if (t2.endsWith("px")) {
      const n2 = parseFloat(t2);
      return Number.isFinite(n2) ? { frac: 0, px: n2 } : null;
    }
    const n = parseFloat(t2);
    return Number.isFinite(n) && n === 0 ? { frac: 0, px: 0 } : null;
  }
  function parseObjectPosition(value) {
    const center = { frac: 0.5, px: 0 };
    const tokens = String(value || "").trim().split(/\s+/).filter(Boolean);
    if (!tokens.length) return { x: center, y: { frac: 1, px: 0 } };
    let x = null;
    let y = null;
    const pending = [];
    for (const tok of tokens.slice(0, 2)) {
      const lower = tok.toLowerCase();
      if (lower === "left" || lower === "right") x = parsePositionToken(tok);
      else if (lower === "top" || lower === "bottom") y = parsePositionToken(tok);
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
      case "fill":
        dw = boxW;
        dh = boxH;
        break;
      case "contain":
        dw = srcW * contain;
        dh = srcH * contain;
        break;
      case "none":
        dw = srcW * dpr;
        dh = srcH * dpr;
        break;
      case "scale-down": {
        const s = Math.min(contain, dpr);
        dw = srcW * s;
        dh = srcH * s;
        break;
      }
      case "cover":
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
    const ctx = canvas.getContext("2d", { willReadFrequently: true });
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
    const ctx = out.getContext("2d", { willReadFrequently: true });
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(source, 0, 0, source.width, source.height, place.dx, place.dy, place.dw, place.dh);
    sharpen(out, SHARPEN_AMOUNT);
    return await canvasToBlobUrl(out) || url;
  }
  function isBlobInUse(blobUrl) {
    for (const img of document.querySelectorAll("img")) {
      if (img.getAttribute("src") === blobUrl) return true;
    }
    return false;
  }
  function evictRenderCache() {
    while (renderCache.size > RENDER_CACHE_LIMIT) {
      const [oldKey, oldJob] = renderCache.entries().next().value;
      renderCache.delete(oldKey);
      oldJob.then((blobUrl) => {
        if (blobUrl && blobUrl.startsWith("blob:") && !isBlobInUse(blobUrl)) {
          URL.revokeObjectURL(blobUrl);
        }
      });
    }
  }
  function renderLogo(url, { w, h, fit = "cover", position = "center bottom", dpr = 1 }) {
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
    return await canvasToBlobUrl(shrunk) || url;
  }
  function prepareLogo(url) {
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
    const t2 = cs.transform;
    if (!t2 || t2 === "none") return 1;
    const m = t2.match(/^matrix\(\s*([-\d.e]+)\s*,\s*([-\d.e]+)/i);
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
    const fit = (cs.getPropertyValue("--logo-object-fit").trim() || "cover").toLowerCase();
    const position = cs.getPropertyValue("--logo-object-position").trim() || "center bottom";
    return { w, h, fit, position, dpr };
  }
  var dprQuery = null;
  function onDprChange() {
    watchDpr();
    refreshSharpLogos();
  }
  function watchDpr() {
    if (typeof window.matchMedia !== "function") return;
    if (dprQuery) dprQuery.removeEventListener("change", onDprChange);
    dprQuery = window.matchMedia(`(resolution: ${window.devicePixelRatio || 1}dppx)`);
    dprQuery.addEventListener("change", onDprChange);
  }
  async function setSharpLogo(img, url) {
    if (!img || !url) return;
    if (!dprQuery) watchDpr();
    const seq = (requestSeq.get(img) || 0) + 1;
    requestSeq.set(img, seq);
    img.dataset.logoRequest = url;
    const isCurrent = () => requestSeq.get(img) === seq && img.dataset.logoRequest === url;
    const apply = (display2, sharp) => {
      if (!isCurrent()) return;
      if (img.getAttribute("src") !== display2) img.src = display2;
      if (sharp) img.dataset.sharp = "1";
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
      const display2 = await prepareLogo(url);
      apply(display2, false);
      return;
    }
    const display = await renderLogo(url, target);
    apply(display, display !== url);
  }
  var refreshTimer = 0;
  function refreshSharpLogos() {
    if (refreshTimer) clearTimeout(refreshTimer);
    refreshTimer = setTimeout(() => {
      refreshTimer = 0;
      document.querySelectorAll("img[data-logo-request]").forEach((img) => {
        setSharpLogo(img, img.dataset.logoRequest);
      });
    }, REFRESH_DEBOUNCE_MS);
  }

  // ../../WebSrc/js/utils/themeManager.js
  var STORAGE_KEY = "f76_ui_theme";
  var DEFAULT_LOGO = "assets/Icon-nobg.png";
  var VHOST = "https://f76manager.app/";
  var PREVIEW_THEME_ID = "__draft";
  var LOGO_INLINE_VARS = [
    "--logo-width",
    "--logo-height",
    "--logo-scale",
    "--logo-offset-x",
    "--logo-offset-y",
    "--logo-object-fit",
    "--logo-object-position",
    "--logo-opacity",
    "--logo-shadow-y",
    "--logo-shadow-blur",
    "--logo-shadow-opacity",
    "--logo-collapsed-scale",
    "--logo-collapsed-offset-x",
    "--logo-collapsed-margin"
  ];
  function assetVersion() {
    if (typeof window.__F76_ASSET_VERSION === "string" && window.__F76_ASSET_VERSION) {
      return window.__F76_ASSET_VERSION;
    }
    const link = document.querySelector('link[href*="css/style.css"]');
    const href = link?.getAttribute("href") || "";
    const m = href.match(/[?&]v=([^&]+)/);
    return m ? m[1] : String(Date.now());
  }
  function withAssetVersion(url) {
    if (!url || url.startsWith("data:")) return url;
    const sep = url.includes("?") ? "&" : "?";
    return `${url}${sep}v=${encodeURIComponent(assetVersion())}`;
  }
  var userThemesById = /* @__PURE__ */ new Map();
  var previewState = null;
  function isThemePreviewActive() {
    return previewState != null;
  }
  function registerUserThemesFromHost(themes, { authoritative = false } = {}) {
    if (!Array.isArray(themes)) return;
    const previous = new Map(userThemesById);
    userThemesById.clear();
    const bootCss = typeof window !== "undefined" ? window.__F76_USER_THEME_CSS : null;
    if (authoritative) pruneRemovedUserThemes(previous, themes, bootCss);
    for (const t2 of themes) {
      if (!t2?.id) continue;
      const prev = previous.get(t2.id);
      if (t2.css && bootCss) bootCss[t2.id] = t2.css;
      userThemesById.set(t2.id, {
        id: t2.id,
        displayName: t2.displayName || prev?.displayName || t2.id,
        logo: t2.logo || prev?.logo || `user-theme-logo/${t2.id}`,
        css: t2.css || prev?.css || bootCss?.[t2.id] || ""
      });
    }
    if (!authoritative && bootCss) {
      for (const [id, css] of Object.entries(bootCss)) {
        const entry = userThemesById.get(id);
        if (entry && !entry.css) entry.css = css;
        else if (!entry && css) {
          userThemesById.set(id, { id, displayName: id, logo: `user-theme-logo/${id}`, css });
        }
      }
    }
    for (const [id, t2] of userThemesById) {
      if (t2.css) ensureUserThemeStyle(id, t2.css);
    }
    if (authoritative && !previewState) {
      const applied = document.documentElement.dataset.theme;
      if (applied && !isBuiltInUiTheme(applied) && applied !== PREVIEW_THEME_ID && !userThemesById.has(applied)) {
        applyUiTheme(DEFAULT_UI_THEME);
      }
    }
  }
  function pruneRemovedUserThemes(previous, themes, bootCss) {
    const keep = new Set(themes.filter((t2) => t2?.id).map((t2) => t2.id));
    const stale = /* @__PURE__ */ new Set([...previous.keys(), ...Object.keys(bootCss || {})]);
    for (const id of stale) {
      if (keep.has(id)) continue;
      if (bootCss) delete bootCss[id];
      document.getElementById(`user-theme-${id}`)?.remove();
      if (previewState?.previousThemeId === id) previewState.previousThemeId = DEFAULT_UI_THEME;
    }
    if (typeof window !== "undefined" && Array.isArray(window.__F76_USER_THEME_IDS)) {
      window.__F76_USER_THEME_IDS = window.__F76_USER_THEME_IDS.filter((id) => keep.has(id));
    }
  }
  function ensureUserThemeStyle(themeId, css) {
    const styleId = `user-theme-${themeId}`;
    let el = document.getElementById(styleId);
    if (!el) {
      el = document.createElement("style");
      el.id = styleId;
    }
    document.head.appendChild(el);
    el.textContent = css;
  }
  function getAppliedThemeId() {
    const id = previewState ? previewState.previousThemeId : document.documentElement.dataset.theme;
    return id && id !== PREVIEW_THEME_ID && isValidUiTheme(id) ? id : null;
  }
  function getThemeOptionsForSettings(ms, { followApplied = false } = {}) {
    const active = followApplied && getAppliedThemeId() || ms?.uiTheme || DEFAULT_UI_THEME;
    const builtin = UI_THEMES.map((th) => ({
      id: th.id,
      labelKey: th.labelKey,
      isUser: false,
      selected: active === th.id
    }));
    const user = [...userThemesById.values()].map((t2) => ({
      id: t2.id,
      displayName: t2.displayName,
      isUser: true,
      selected: active === t2.id
    }));
    return [...builtin, ...user];
  }
  function toVirtualHostUrl(path) {
    if (!path || path.startsWith("http") || path.startsWith("data:")) return path;
    return `${VHOST}${path.replace(/^\//, "")}`;
  }
  function logoForTheme(themeId) {
    const user = userThemesById.get(themeId);
    if (user) {
      return toVirtualHostUrl(user.logo);
    }
    const entry = UI_THEMES.find((t2) => t2.id === themeId);
    return entry?.logo ? toVirtualHostUrl(entry.logo) : toVirtualHostUrl(DEFAULT_LOGO);
  }
  function clearInlineLogoVars() {
    const root = document.documentElement;
    for (const name of LOGO_INLINE_VARS) {
      root.style.removeProperty(name);
    }
  }
  function applyThemeLogo(themeId) {
    const el = document.getElementById("app-logo");
    if (!el) return;
    setSharpLogo(el, withAssetVersion(logoForTheme(themeId)));
    if (userThemesById.has(themeId)) {
      clearInlineLogoVars();
    } else {
      applyLogoLayout(getLogoLayoutForTheme(themeId));
    }
    refreshSharpLogos();
  }
  function getUiTheme() {
    if (previewState) return PREVIEW_THEME_ID;
    const fromDom = document.documentElement.dataset.theme;
    if (fromDom && fromDom !== PREVIEW_THEME_ID && isValidUiTheme(fromDom)) return fromDom;
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      if (stored && isValidUiTheme(stored)) return stored;
    } catch {
    }
    return DEFAULT_UI_THEME;
  }
  function applyUiTheme(id) {
    if (previewState) {
      if (id && id !== PREVIEW_THEME_ID && isValidUiTheme(id)) previewState.previousThemeId = id;
      return previewState.previousThemeId || DEFAULT_UI_THEME;
    }
    const themeId = isValidUiTheme(id) ? id : DEFAULT_UI_THEME;
    document.documentElement.dataset.theme = themeId;
    const user = userThemesById.get(themeId);
    if (user?.css) ensureUserThemeStyle(themeId, user.css);
    applyThemeLogo(themeId);
    try {
      localStorage.setItem(STORAGE_KEY, themeId);
    } catch {
    }
    postWindowChromeColors();
    return themeId;
  }
  function cssColorToHex(value) {
    if (!value) return null;
    const v = String(value).trim();
    if (v.startsWith("#")) {
      if (v.length === 4) {
        return `#${v[1]}${v[1]}${v[2]}${v[2]}${v[3]}${v[3]}`.toLowerCase();
      }
      return v.slice(0, 7).toLowerCase();
    }
    const m = v.match(/rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/i);
    if (!m) return null;
    const hex = (n) => Number(n).toString(16).padStart(2, "0");
    return `#${hex(m[1])}${hex(m[2])}${hex(m[3])}`;
  }
  function postWindowChromeColors() {
    try {
      if (!window.chrome?.webview?.postMessage) return;
      const styles = getComputedStyle(document.documentElement);
      const caption = cssColorToHex(styles.getPropertyValue("--bg-dark")) || "#121212";
      const text = cssColorToHex(styles.getPropertyValue("--text-main")) || "#e0e0e0";
      const border = cssColorToHex(styles.getPropertyValue("--border-color")) || "#333333";
      const themeId = document.documentElement.dataset.theme || getUiTheme();
      const styleEl = document.getElementById(`user-theme-${themeId}`);
      const css = styleEl?.textContent || "";
      window.chrome.webview.postMessage({
        type: "SET_WINDOW_CHROME",
        caption,
        text,
        border,
        themeId,
        css
      });
    } catch {
    }
  }
  function isValidUiTheme(id) {
    if (typeof id !== "string" || !id) return false;
    if (id === PREVIEW_THEME_ID) return true;
    if (isBuiltInUiTheme(id)) return true;
    return userThemesById.has(id);
  }
  function buildDraftThemeCss(themeId, tokens, logoLayout) {
    const L = normalizeLogoLayout(logoLayout);
    const lines = [`:root[data-theme="${themeId}"] {`];
    for (const [key, val] of Object.entries(tokens || {})) {
      if (val == null || val === "") continue;
      lines.push(`  --${key}: ${val};`);
    }
    if (tokens?.["primary-rgb"]) {
      lines.push(`  --primary-green-dim: rgba(${tokens["primary-rgb"]}, 0.7);`);
    }
    const width = L.width;
    lines.push(`  --logo-width: ${width}px;`);
    lines.push(`  --logo-height: ${L.height > 0 ? `${L.height}px` : "var(--topbar-height)"};`);
    lines.push(`  --logo-scale: ${L.scale};`);
    lines.push(`  --logo-offset-x: ${L.offsetX}px;`);
    lines.push(`  --logo-offset-y: ${L.offsetY}px;`);
    lines.push(`  --logo-object-fit: ${L.objectFit};`);
    lines.push(`  --logo-object-position: ${L.objectPosition};`);
    lines.push(`  --logo-opacity: ${L.opacity};`);
    lines.push(`  --logo-shadow-y: ${L.shadowY}px;`);
    lines.push(`  --logo-shadow-blur: ${L.shadowBlur}px;`);
    lines.push(`  --logo-shadow-opacity: ${L.shadowOpacity};`);
    lines.push(`  --logo-collapsed-scale: ${L.collapsedScale};`);
    lines.push(`  --logo-collapsed-offset-x: ${L.collapsedOffsetX}px;`);
    lines.push(`  --logo-collapsed-margin: ${-width / 2}px;`);
    lines.push("}");
    return lines.join("\n");
  }
  function beginThemePreview(css, logoUrl, layout) {
    if (!previewState) {
      const logoEl = document.getElementById("app-logo");
      previewState = {
        previousThemeId: document.documentElement.dataset.theme || getUiTheme(),
        previousLogoSrc: logoEl?.getAttribute("src") || null
      };
      if (previewState.previousThemeId === PREVIEW_THEME_ID) {
        previewState.previousThemeId = DEFAULT_UI_THEME;
      }
    }
    updateThemePreview(css, logoUrl, layout);
  }
  function updateThemePreview(css, logoUrl, layout) {
    if (!previewState) {
      beginThemePreview(css, logoUrl, layout);
      return;
    }
    ensureUserThemeStyle(PREVIEW_THEME_ID, css || "");
    document.documentElement.dataset.theme = PREVIEW_THEME_ID;
    clearInlineLogoVars();
    if (layout) applyLogoLayout(layout);
    refreshSharpLogos();
    const logoEl = document.getElementById("app-logo");
    if (logoEl && logoUrl) {
      setSharpLogo(logoEl, withAssetVersion(logoUrl));
    }
    postWindowChromeColors();
  }
  function readLiveThemeTokens(themeId) {
    const prev = document.documentElement.dataset.theme;
    const keys = [
      "bg-dark",
      "bg-surface",
      "bg-surface-light",
      "bg-elevated",
      "bg-inset",
      "primary-green",
      "primary-rgb",
      "primary-hover",
      "accent-amber",
      "text-main",
      "text-muted",
      "border-color",
      "danger-red",
      "danger-red-soft",
      "success-green",
      "warning-yellow",
      "on-primary",
      "slider-track"
    ];
    try {
      if (themeId && themeId !== PREVIEW_THEME_ID) {
        document.documentElement.dataset.theme = themeId;
        const user = userThemesById.get(themeId);
        if (user?.css) ensureUserThemeStyle(themeId, user.css);
      }
      void document.documentElement.offsetHeight;
      const styles = getComputedStyle(document.documentElement);
      const tokens = {};
      for (const key of keys) {
        const raw = styles.getPropertyValue(`--${key}`).trim();
        tokens[key] = raw || "";
      }
      return tokens;
    } finally {
      document.documentElement.dataset.theme = prev;
    }
  }
  function getLogoUrlForTheme(themeId) {
    return withAssetVersion(logoForTheme(themeId));
  }
  function getBuiltInLogoOptions() {
    return [
      ...UI_THEMES.map((t2) => ({
        id: t2.id,
        labelKey: t2.labelKey,
        url: withAssetVersion(toVirtualHostUrl(t2.logo))
      })),
      {
        id: "app-icon",
        labelKey: "theme_creator_logo_app_icon",
        url: withAssetVersion(toVirtualHostUrl(DEFAULT_LOGO))
      }
    ];
  }
  if (typeof window !== "undefined") {
    const ids = window.__F76_USER_THEME_IDS;
    if (Array.isArray(ids) && ids.length) {
      registerUserThemesFromHost(ids.map((id) => ({ id })));
    }
  }

  // ../../WebSrc/js/utils/htmlSafe.js
  function escapeHtml(value) {
    const str = String(value ?? "");
    return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }
  function escapeAttr(value) {
    return escapeHtml(value).replace(/`/g, "&#96;");
  }

  // ../../WebSrc/js/components/ThemeCreator.js
  var DRAFT_ID = "__draft";
  var MAX_LOGO_DIM = 512;
  var MAX_LOGO_BYTES = 2 * 1024 * 1024;
  var COLOR_TOKENS = [
    { key: "bg-dark", labelKey: "theme_token_bg_dark", derived: false },
    { key: "bg-surface", labelKey: "theme_token_bg_surface", derived: false },
    { key: "bg-surface-light", labelKey: "theme_token_bg_surface_light", derived: false },
    { key: "bg-elevated", labelKey: "theme_token_bg_elevated", derived: true },
    { key: "bg-inset", labelKey: "theme_token_bg_inset", derived: true },
    { key: "primary-green", labelKey: "theme_token_primary", derived: false },
    { key: "primary-hover", labelKey: "theme_token_primary_hover", derived: true },
    { key: "accent-amber", labelKey: "theme_token_accent", derived: false },
    { key: "text-main", labelKey: "theme_token_text_main", derived: false },
    { key: "text-muted", labelKey: "theme_token_text_muted", derived: false },
    { key: "border-color", labelKey: "theme_token_border", derived: false },
    { key: "danger-red", labelKey: "theme_token_danger", derived: false },
    { key: "danger-red-soft", labelKey: "theme_token_danger_soft", derived: true },
    { key: "success-green", labelKey: "theme_token_success", derived: false },
    { key: "warning-yellow", labelKey: "theme_token_warning", derived: false },
    { key: "on-primary", labelKey: "theme_token_on_primary", derived: false },
    { key: "slider-track", labelKey: "theme_token_slider_track", derived: true }
  ];
  var DEFAULT_AUTO_KEYS = ["bg-elevated", "bg-inset", "primary-hover", "danger-red-soft", "slider-track"];
  var OBJECT_FITS2 = ["cover", "contain", "fill", "none", "scale-down"];
  var RANDOM_MODES = [
    { id: "harmonious", labelKey: "theme_creator_random_harmonious", fallback: "Harmonious" },
    { id: "vibrant", labelKey: "theme_creator_random_vibrant", fallback: "Vibrant" },
    { id: "chaos", labelKey: "theme_creator_random_chaos", fallback: "Chaos" }
  ];
  function t(key, ...args) {
    return window._t?.(key, ...args) || key;
  }
  function tOr(key, fallback) {
    const v = t(key);
    return v && v !== key ? v : fallback;
  }
  function slugify(name) {
    return String(name || "").toLowerCase().normalize("NFKD").replace(/[\u0300-\u036f]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").replace(/-+/g, "-").slice(0, 48);
  }
  function isValidThemeId(id) {
    return /^[a-z][a-z0-9]*(-[a-z0-9]+)*$/.test(id) && id !== "my-theme" && id !== "default";
  }
  function parseColor(value) {
    const v = String(value || "").trim();
    let m = v.match(/^#([0-9a-f]{6})$/i);
    if (m) {
      return {
        r: parseInt(m[1].slice(0, 2), 16),
        g: parseInt(m[1].slice(2, 4), 16),
        b: parseInt(m[1].slice(4, 6), 16),
        a: 1
      };
    }
    m = v.match(/^#([0-9a-f]{8})$/i);
    if (m) {
      return {
        r: parseInt(m[1].slice(0, 2), 16),
        g: parseInt(m[1].slice(2, 4), 16),
        b: parseInt(m[1].slice(4, 6), 16),
        a: parseInt(m[1].slice(6, 8), 16) / 255
      };
    }
    m = v.match(/^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*(0|1|0?\.\d+|1\.0+))?\s*\)$/i);
    if (m) {
      return {
        r: Math.min(255, +m[1]),
        g: Math.min(255, +m[2]),
        b: Math.min(255, +m[3]),
        a: m[4] != null ? Math.min(1, Math.max(0, +m[4])) : 1
      };
    }
    return null;
  }
  function toHex6({ r, g, b }) {
    const h = (n) => Math.max(0, Math.min(255, Math.round(n))).toString(16).padStart(2, "0");
    return `#${h(r)}${h(g)}${h(b)}`;
  }
  function toCssColor({ r, g, b, a }) {
    if (a == null || a >= 0.999) return toHex6({ r, g, b });
    const aa = Math.round(a * 1e3) / 1e3;
    return `rgba(${Math.round(r)}, ${Math.round(g)}, ${Math.round(b)}, ${aa})`;
  }
  function lighten(color, amount) {
    const c = parseColor(color);
    if (!c) return color;
    return toHex6({
      r: c.r + (255 - c.r) * amount,
      g: c.g + (255 - c.g) * amount,
      b: c.b + (255 - c.b) * amount
    });
  }
  function darken(color, amount) {
    const c = parseColor(color);
    if (!c) return color;
    return toHex6({
      r: c.r * (1 - amount),
      g: c.g * (1 - amount),
      b: c.b * (1 - amount)
    });
  }
  function mix(a, b, tFrac) {
    const ca = parseColor(a);
    const cb = parseColor(b);
    if (!ca || !cb) return a;
    return toHex6({
      r: ca.r + (cb.r - ca.r) * tFrac,
      g: ca.g + (cb.g - ca.g) * tFrac,
      b: ca.b + (cb.b - ca.b) * tFrac
    });
  }
  function hslToHex(h, s, l) {
    const hh = (h % 360 + 360) % 360;
    const ss = Math.max(0, Math.min(100, s)) / 100;
    const ll = Math.max(0, Math.min(100, l)) / 100;
    const k = (n) => (n + hh / 30) % 12;
    const a = ss * Math.min(ll, 1 - ll);
    const f = (n) => ll - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
    return toHex6({ r: f(0) * 255, g: f(8) * 255, b: f(4) * 255 });
  }
  function relativeLuminance(color) {
    const c = parseColor(color);
    if (!c) return 0;
    const ch = (v) => {
      const x = v / 255;
      return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * ch(c.r) + 0.7152 * ch(c.g) + 0.0722 * ch(c.b);
  }
  function contrastRatio(a, b) {
    const la = relativeLuminance(a);
    const lb = relativeLuminance(b);
    return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
  }
  function rand(min, max) {
    return min + Math.random() * (max - min);
  }
  function pick(arr) {
    return arr[Math.floor(Math.random() * arr.length)];
  }
  function hslWithContrast(h, s, l, bg, minRatio, towardLight) {
    let light = l;
    let color = hslToHex(h, s, light);
    for (let i = 0; i < 50 && contrastRatio(color, bg) < minRatio; i++) {
      light += towardLight ? 2 : -2;
      if (light < 0 || light > 100) break;
      color = hslToHex(h, s, light);
    }
    return color;
  }
  function randomHex() {
    return toHex6({ r: rand(0, 256), g: rand(0, 256), b: rand(0, 256) });
  }
  function generateRandomPalette(mode) {
    if (mode === "chaos") {
      const out = {};
      for (const { key } of COLOR_TOKENS) out[key] = randomHex();
      return out;
    }
    const vibrant = mode === "vibrant";
    const dark = vibrant || Math.random() < 0.7;
    const hue = rand(0, 360);
    const bgSat = vibrant ? rand(35, 60) : rand(8, 22);
    const base = dark ? rand(6, 9) : rand(92, 96);
    const bgL = dark ? [base, base + 4, base + 10] : [base, base - 5, base - 11];
    const bgDark = hslToHex(hue, bgSat, bgL[0]);
    const primaryHue = hue + pick([0, 30, -30, 150, 180, 210]);
    const primarySat = vibrant ? rand(85, 100) : rand(35, 60);
    const primaryL = dark ? vibrant ? rand(55, 65) : rand(60, 72) : rand(32, 42);
    const primary = hslWithContrast(primaryHue, primarySat, primaryL, bgDark, 3, dark);
    const accentHue = primaryHue + pick([60, -60, 120, -120, 180]);
    const accent = hslWithContrast(accentHue, primarySat, primaryL, bgDark, 3, dark);
    const statusSat = vibrant ? rand(80, 100) : rand(55, 80);
    const statusL = dark ? rand(52, 62) : rand(38, 46);
    const status = (h) => hslWithContrast(h, statusSat, statusL, bgDark, 3, dark);
    const textHue = pick([hue, primaryHue, accentHue]);
    const onPrimary = contrastRatio(primary, "#121212") >= contrastRatio(primary, "#ffffff") ? "#121212" : "#ffffff";
    return {
      "bg-dark": bgDark,
      "bg-surface": hslToHex(hue, bgSat, bgL[1]),
      "bg-surface-light": hslToHex(hue, bgSat, bgL[2]),
      "primary-green": primary,
      "accent-amber": accent,
      "text-main": hslWithContrast(textHue, vibrant ? rand(30, 45) : rand(18, 30), dark ? rand(86, 92) : rand(10, 18), bgDark, 7, dark),
      "text-muted": hslWithContrast(textHue, vibrant ? rand(32, 50) : rand(20, 34), dark ? rand(64, 72) : rand(36, 44), bgDark, 4.5, dark),
      "border-color": hslToHex(hue, bgSat, dark ? bgL[2] + 6 : bgL[2] - 6),
      "danger-red": status(rand(-5, 10)),
      "success-green": status(rand(115, 145)),
      "warning-yellow": status(rand(42, 55)),
      "on-primary": onPrimary
    };
  }
  function deriveToken(key, tokens) {
    switch (key) {
      case "primary-hover":
        return lighten(tokens["primary-green"] || "#b8c5a4", 0.12);
      case "bg-elevated":
        return mix(tokens["bg-surface"] || "#1e1e1e", tokens["bg-surface-light"] || "#2d2d2d", 0.5);
      case "bg-inset":
        return darken(tokens["bg-dark"] || "#121212", 0.04);
      case "slider-track":
        return mix(tokens["bg-surface-light"] || "#2d2d2d", tokens["primary-green"] || "#b8c5a4", 0.35);
      case "danger-red-soft": {
        const c = parseColor(tokens["danger-red"] || "#e0564c") || { r: 224, g: 86, b: 76, a: 1 };
        return `rgba(${c.r}, ${c.g}, ${c.b}, 0.18)`;
      }
      default:
        return tokens[key] || "";
    }
  }
  function syncRangeFill(input) {
    const min = Number(input.min) || 0;
    const max = Number(input.max) || 100;
    const pct = max > min ? (Number(input.value) - min) / (max - min) * 100 : 0;
    input.style.setProperty("--value", `${Math.min(100, Math.max(0, pct))}%`);
  }
  function rgbTripletFromPrimary(primary) {
    const c = parseColor(primary);
    if (!c) return "184, 197, 164";
    return `${Math.round(c.r)}, ${Math.round(c.g)}, ${Math.round(c.b)}`;
  }
  function readFileAsDataUrl(blob) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result);
      reader.onerror = () => reject(new Error("read failed"));
      reader.readAsDataURL(blob);
    });
  }
  function isAnimatedFriendlyMime(file) {
    const type = (file.type || "").toLowerCase();
    const name = (file.name || "").toLowerCase();
    return type === "image/gif" || type === "image/webp" || name.endsWith(".gif") || name.endsWith(".webp");
  }
  async function resizeLogoFile(file) {
    if (file.size > MAX_LOGO_BYTES) {
      throw new Error(t("theme_creator_logo_too_large"));
    }
    if (isAnimatedFriendlyMime(file)) {
      const dataUrl2 = await readFileAsDataUrl(file);
      return { dataUrl: dataUrl2, mime: file.type || "image/gif" };
    }
    const bitmap = await createImageBitmap(file);
    const scale = Math.min(1, MAX_LOGO_DIM / Math.max(bitmap.width, bitmap.height));
    const w = Math.max(1, Math.round(bitmap.width * scale));
    const h = Math.max(1, Math.round(bitmap.height * scale));
    const canvas = document.createElement("canvas");
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext("2d");
    ctx.drawImage(bitmap, 0, 0, w, h);
    bitmap.close?.();
    const toBlob = (type, quality) => new Promise((resolve) => canvas.toBlob(resolve, type, quality));
    let blob = await toBlob("image/png");
    if (blob && blob.size > MAX_LOGO_BYTES) blob = await toBlob("image/jpeg", 0.88);
    if (blob && blob.size > MAX_LOGO_BYTES) blob = await toBlob("image/jpeg", 0.72);
    if (!blob || blob.size > MAX_LOGO_BYTES) {
      throw new Error(t("theme_creator_logo_too_large"));
    }
    const dataUrl = await readFileAsDataUrl(blob);
    return { dataUrl, mime: blob.type };
  }
  var ThemeCreator = class {
    constructor(root) {
      this.root = root;
      this.activeTab = "general";
      this.dirty = false;
      this.idManual = false;
      this.nameTouched = false;
      this.idTouched = false;
      this.autoKeys = new Set(DEFAULT_AUTO_KEYS);
      this.tokens = {};
      this.logoLayout = { ...DEFAULT_LOGO_LAYOUT };
      this.logoDataUrl = "";
      this._pendingLogoFetch = "";
      this.displayName = "";
      this.themeId = "";
      this.startFrom = DEFAULT_UI_THEME;
      this.editMode = false;
      this._abort = null;
      this._logoDrag = null;
      this._closing = false;
      this._status = { type: "", text: "" };
      this.randomMode = "harmonious";
    }
    mount() {
      this._seedFromTheme(window.__F76_BOOT_UI_THEME || DEFAULT_UI_THEME, { keepIdentity: false });
      this._render();
      this._bind();
      this._pushPreview();
      if (window.lucide) window.lucide.createIcons();
    }
    loadForEdit(data) {
      if (!data?.id) return;
      this.editMode = true;
      this.startFrom = data.id;
      this.themeId = data.id;
      this.displayName = data.displayName || data.id;
      this.idManual = true;
      this.nameTouched = true;
      this.idTouched = true;
      const derivedKeys = COLOR_TOKENS.filter((c) => c.derived).map((c) => c.key);
      this.autoKeys = /* @__PURE__ */ new Set();
      this.tokens = {};
      const incoming = data.tokens || {};
      for (const { key } of COLOR_TOKENS) {
        if (incoming[key] != null && incoming[key] !== "") {
          this.tokens[key] = incoming[key];
        } else if (derivedKeys.includes(key)) {
          this.autoKeys.add(key);
        }
      }
      for (const [key, val] of Object.entries(incoming)) {
        if (val != null && val !== "" && this.tokens[key] == null) this.tokens[key] = val;
      }
      for (const key of [...this.autoKeys]) {
        this.tokens[key] = deriveToken(key, this.tokens);
      }
      this.tokens["primary-rgb"] = rgbTripletFromPrimary(this.tokens["primary-green"] || incoming["primary-green"] || "#00ff9d");
      this.logoLayout = normalizeLogoLayout(data.logoLayout || DEFAULT_LOGO_LAYOUT);
      this.logoDataUrl = data.logoDataUrl || "";
      this._pendingLogoFetch = this.logoDataUrl ? "" : getLogoUrlForTheme(data.id);
      this.dirty = false;
      this._status = { type: "", text: "" };
      this._render();
      this._bind();
      this._pushPreview();
      if (window.lucide) window.lucide.createIcons();
    }
    _seedFromTheme(themeId, opts = {}) {
      const keepIdentity = !!opts.keepIdentity;
      this.startFrom = themeId || DEFAULT_UI_THEME;
      const tokens = readLiveThemeTokens(this.startFrom);
      for (const { key } of COLOR_TOKENS) {
        let val = tokens[key];
        if (!val || !parseColor(val)) val = deriveToken(key, tokens);
        this.tokens[key] = val;
      }
      this.tokens["primary-rgb"] = rgbTripletFromPrimary(this.tokens["primary-green"]);
      this.autoKeys = new Set(DEFAULT_AUTO_KEYS);
      for (const key of [...this.autoKeys]) {
        this.tokens[key] = deriveToken(key, this.tokens);
      }
      this.logoLayout = isBuiltInUiTheme(this.startFrom) ? { ...getLogoLayoutForTheme(this.startFrom) } : { ...DEFAULT_LOGO_LAYOUT };
      this.logoDataUrl = "";
      this._pendingLogoFetch = getLogoUrlForTheme(this.startFrom);
      if (!keepIdentity) {
        this.displayName = "";
        this.themeId = "";
        this.idManual = false;
        this.nameTouched = false;
        this.idTouched = false;
        this.dirty = false;
        this.editMode = false;
      }
    }
    _recomputeDerived() {
      for (const key of this.autoKeys) {
        this.tokens[key] = deriveToken(key, this.tokens);
      }
      this.tokens["primary-rgb"] = rgbTripletFromPrimary(this.tokens["primary-green"]);
    }
    _currentLogoUrl() {
      return this.logoDataUrl || this._pendingLogoFetch || getLogoUrlForTheme(this.startFrom);
    }
    _buildCss() {
      this._recomputeDerived();
      return buildDraftThemeCss(DRAFT_ID, {
        ...this.tokens,
        "primary-rgb": this.tokens["primary-rgb"]
      }, this.logoLayout);
    }
    _pushPreview() {
      const css = this._buildCss();
      const logo = this._currentLogoUrl();
      const layout = normalizeLogoLayout(this.logoLayout);
      if (isThemePreviewActive()) {
        updateThemePreview(css, logo, layout);
      } else {
        beginThemePreview(css, logo, layout);
      }
      try {
        window.chrome?.webview?.postMessage({
          type: "THEME_PREVIEW_UPDATE",
          css,
          logoUrl: logo,
          layout
        });
      } catch {
      }
      this._updateReplicaLogos();
    }
    _contentTitle() {
      if (this.activeTab === "colors") return t("theme_creator_tab_colors");
      if (this.activeTab === "logo") return t("theme_creator_tab_logo");
      return t("theme_creator_tab_general");
    }
    _contentDesc() {
      return t("theme_creator_popout_hint");
    }
    _isFormValid() {
      const nameOk = !!this.displayName.trim();
      const idOk = isValidThemeId(this.themeId) && !isBuiltInUiTheme(this.themeId);
      return nameOk && idOk;
    }
    _nameError() {
      if (!this.nameTouched) return "";
      if (!this.displayName.trim()) return t("theme_creator_name_required");
      return "";
    }
    _idError() {
      if (!this.themeId) {
        return this.idTouched || this.nameTouched ? t("theme_creator_id_required") : "";
      }
      if (!isValidThemeId(this.themeId)) return t("theme_creator_id_invalid");
      if (isBuiltInUiTheme(this.themeId)) return t("theme_creator_id_reserved");
      return "";
    }
    _startFromOptionsHtml() {
      return getThemeOptionsForSettings({ uiTheme: this.startFrom }).map((th) => {
        const label = th.isUser ? escapeHtml(th.displayName || th.id) : escapeHtml(t(th.labelKey));
        return `<option value="${escapeAttr(th.id)}" ${th.id === this.startFrom ? "selected" : ""}>${label}</option>`;
      }).join("");
    }
    _renderGeneral() {
      const nameErr = this._nameError();
      const idErr = this._idError();
      return `
            <div class="settings-card-inner theme-creator-general">
                <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 8px;">
                    <div class="tweak-label">${escapeHtml(t("theme_creator_name"))} <span class="theme-creator-required" aria-hidden="true">*</span></div>
                    <div class="path-input-group" style="width: 100%;">
                        <input type="text" id="theme-creator-name" maxlength="80"
                            class="${nameErr ? "theme-creator-input-error" : ""}"
                            placeholder="${escapeAttr(t("theme_creator_name_placeholder"))}"
                            value="${escapeAttr(this.displayName)}">
                    </div>
                    <div class="theme-creator-field-hint ${nameErr ? "theme-creator-field-hint--error" : ""}" id="theme-creator-name-hint">${escapeHtml(nameErr)}</div>
                </div>
                <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 8px;">
                    <div class="tweak-label">${escapeHtml(t("theme_creator_id"))} <span class="theme-creator-required" aria-hidden="true">*</span></div>
                    <div class="path-input-group" style="width: 100%;">
                        <input type="text" id="theme-creator-id" maxlength="48"
                            class="${idErr ? "theme-creator-input-error" : ""}"
                            placeholder="${escapeAttr(t("theme_creator_id_placeholder"))}"
                            value="${escapeAttr(this.themeId)}"
                            ${this.editMode ? "readonly" : ""}>
                    </div>
                    <div class="theme-creator-field-hint ${idErr ? "theme-creator-field-hint--error" : this.editMode ? "theme-creator-field-hint--muted" : ""}" id="theme-creator-id-hint">${escapeHtml(idErr || (this.editMode ? t("theme_creator_id_locked") : t("theme_creator_id_invalid")))}</div>
                </div>
            </div>
        `;
    }
    _renderColors() {
      const rows = COLOR_TOKENS.map(({ key, labelKey, derived }) => {
        const parsed = parseColor(this.tokens[key]) || { r: 128, g: 128, b: 128, a: 1 };
        const hex = toHex6(parsed);
        const isAuto = derived && this.autoKeys.has(key);
        const showOpacity = parsed.a < 0.999 || key === "text-muted" || key === "border-color" || key === "danger-red-soft";
        const value = this.tokens[key] || "";
        return `
                <div class="theme-creator-color-cell" data-token="${escapeAttr(key)}">
                    <div class="theme-creator-color-info">
                        <div class="tweak-label">${escapeHtml(t(labelKey))}</div>
                        ${derived ? `
                            <label class="theme-creator-auto" title="${escapeAttr(t("theme_creator_auto_hint"))}">
                                <span class="switch">
                                    <input type="checkbox" class="theme-creator-auto-toggle" data-token="${escapeAttr(key)}" ${isAuto ? "checked" : ""}>
                                    <span class="slider"></span>
                                </span>
                                <span class="tweak-desc">${escapeHtml(t("theme_creator_auto"))}</span>
                            </label>
                        ` : ""}
                    </div>
                    <input type="color" class="theme-creator-color-swatch" data-token="${escapeAttr(key)}" value="${escapeAttr(hex)}" ${isAuto ? "disabled" : ""}>
                    <input type="text" class="theme-creator-color-text" data-token="${escapeAttr(key)}" value="${escapeAttr(value)}" title="${escapeAttr(value)}" spellcheck="false" ${isAuto ? "readonly" : ""}>
                    ${showOpacity ? `
                        <div class="theme-creator-opacity-row">
                            <span class="tweak-desc">${escapeHtml(t("theme_creator_opacity"))}</span>
                            <input type="range" min="0" max="100" step="1" class="tweak-range theme-creator-opacity" data-token="${escapeAttr(key)}" value="${Math.round(parsed.a * 100)}" ${isAuto ? "disabled" : ""}>
                            <span class="theme-creator-opacity-val" data-token="${escapeAttr(key)}">${Math.round(parsed.a * 100)}%</span>
                        </div>
                    ` : ""}
                </div>
            `;
      }).join("");
      const modeOptions = RANDOM_MODES.map(
        ({ id, labelKey, fallback }) => `<option value="${id}" ${this.randomMode === id ? "selected" : ""}>${escapeHtml(tOr(labelKey, fallback))}</option>`
      ).join("");
      return `
            <div class="theme-creator-colors-toolbar">
                <label class="tweak-desc" for="tc-random-mode">${escapeHtml(tOr("theme_creator_random_mode", "Style"))}</label>
                <select id="tc-random-mode" class="settings-select">${modeOptions}</select>
                <button type="button" class="btn-secondary" id="tc-randomize">
                    <i data-lucide="dices"></i>
                    <span>${escapeHtml(tOr("theme_creator_randomize", "Randomize"))}</span>
                </button>
            </div>
            <div class="theme-creator-colors-grid">${rows}</div>
        `;
    }
    _renderLogo() {
      const L = this.logoLayout;
      const logoSrc = escapeAttr(this._currentLogoUrl());
      return `
            <div class="theme-creator-logo-section">
                <div class="theme-creator-logo-actions">
                    <button type="button" class="btn-secondary" id="theme-creator-pick-logo">
                        <i data-lucide="image"></i>
                        <span>${escapeHtml(t("theme_creator_pick_logo"))}</span>
                    </button>
                    <input type="file" id="theme-creator-logo-file" accept="image/png,image/jpeg,image/webp,image/gif,.png,.jpg,.jpeg,.webp,.gif" hidden>
                    <p class="tweak-desc">${escapeHtml(t("theme_creator_logo_drag_hint"))}</p>
                    <div class="tweak-label">${escapeHtml(tOr("theme_creator_builtin_logos", "Built-in logos"))}</div>
                    <div class="theme-creator-builtin-logos">${this._builtInLogosHtml()}</div>
                </div>

                <div class="theme-creator-sidebar-previews">
                    <div class="theme-creator-sidebar-preview-card">
                        <div class="theme-creator-preview-label">${escapeHtml(t("theme_creator_preview_expanded"))}</div>
                        <div class="sidebar theme-creator-sidebar-replica" data-collapsed="0">
                            <div class="sidebar-header">
                                <div class="logo-container">
                                    <img class="logo theme-studio-logo-editable theme-creator-replica-logo" src="${logoSrc}" alt="" draggable="false">
                                    <span class="app-name">F76 Manager</span>
                                </div>
                            </div>
                        </div>
                    </div>
                    <div class="theme-creator-sidebar-preview-card">
                        <div class="theme-creator-preview-label">${escapeHtml(t("theme_creator_preview_collapsed"))}</div>
                        <div class="sidebar collapsed theme-creator-sidebar-replica" data-collapsed="1">
                            <div class="sidebar-header">
                                <div class="logo-container">
                                    <img class="logo theme-studio-logo-editable theme-creator-replica-logo" src="${logoSrc}" alt="" draggable="false">
                                </div>
                            </div>
                        </div>
                    </div>
                </div>

                <div class="theme-creator-sliders">
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t("theme_creator_logo_width"))} <em id="tc-w-val">${L.width}</em></div>
                        <input type="range" class="tweak-range" id="tc-width" min="16" max="120" value="${L.width}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t("theme_creator_logo_scale"))} <em id="tc-s-val">${L.scale.toFixed(2)}</em></div>
                        <input type="range" class="tweak-range" id="tc-scale" min="25" max="300" value="${Math.round(L.scale * 100)}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t("theme_creator_logo_offset_x"))} <em id="tc-ox-val">${L.offsetX}</em></div>
                        <input type="range" class="tweak-range" id="tc-offset-x" min="-40" max="40" value="${L.offsetX}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t("theme_creator_logo_offset_y"))} <em id="tc-oy-val">${L.offsetY}</em></div>
                        <input type="range" class="tweak-range" id="tc-offset-y" min="-40" max="40" value="${L.offsetY}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t("theme_creator_logo_collapsed_scale"))} <em id="tc-cs-val">${L.collapsedScale.toFixed(2)}</em></div>
                        <input type="range" class="tweak-range" id="tc-collapsed-scale" min="25" max="300" value="${Math.round(L.collapsedScale * 100)}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t("theme_creator_logo_collapsed_x"))} <em id="tc-cox-val">${L.collapsedOffsetX}</em></div>
                        <input type="range" class="tweak-range" id="tc-collapsed-x" min="-40" max="40" value="${L.collapsedOffsetX}">
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 8px;">
                        <div class="tweak-label">${escapeHtml(t("theme_creator_logo_fit"))}</div>
                        <select id="tc-object-fit" class="settings-select" style="width: 100%;">
                            ${OBJECT_FITS2.map((f) => `<option value="${f}" ${L.objectFit === f ? "selected" : ""}>${f}</option>`).join("")}
                        </select>
                    </div>
                    <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 4px;">
                        <div class="tweak-label">${escapeHtml(t("theme_creator_logo_opacity"))} <em id="tc-op-val">${Math.round(L.opacity * 100)}%</em></div>
                        <input type="range" class="tweak-range" id="tc-opacity" min="0" max="100" value="${Math.round(L.opacity * 100)}">
                    </div>
                </div>
            </div>
        `;
    }
    _builtInLogosHtml() {
      const fallbacks = {
        fallout: "Fallout",
        "vault-tec": "Vault-Tec",
        "red-black": "Red & Black",
        "black-white": "Black & White",
        "app-icon": "App icon"
      };
      const active = this.logoDataUrl ? "" : this._currentLogoUrl();
      return getBuiltInLogoOptions().map(({ id, labelKey, url }) => {
        const label = tOr(labelKey, fallbacks[id] || id);
        return `
                <button type="button" class="theme-creator-builtin-logo ${url === active ? "active" : ""}"
                    data-logo-url="${escapeAttr(url)}" data-logo-theme="${escapeAttr(id)}"
                    title="${escapeAttr(label)}" aria-label="${escapeAttr(label)}">
                    <img src="${escapeAttr(url)}" alt="" draggable="false">
                </button>
            `;
      }).join("");
    }
    _syncBuiltInLogoActive() {
      const active = this.logoDataUrl ? "" : this._currentLogoUrl();
      this.root?.querySelectorAll(".theme-creator-builtin-logo").forEach((btn) => {
        btn.classList.toggle("active", btn.dataset.logoUrl === active);
      });
    }
    _useBuiltInLogo(url, themeId) {
      if (!url) return;
      this.logoDataUrl = "";
      this._pendingLogoFetch = url;
      if (themeId && isBuiltInUiTheme(themeId)) {
        const builtIn = getLogoLayoutForTheme(themeId);
        this.logoLayout = normalizeLogoLayout({
          ...this.logoLayout,
          objectFit: builtIn.objectFit,
          objectPosition: builtIn.objectPosition
        });
      }
      this._setStatus("", "");
      this._syncBuiltInLogoActive();
      this._syncSlidersFromLayout();
      const fitSelect = this.root?.querySelector("#tc-object-fit");
      if (fitSelect) fitSelect.value = this.logoLayout.objectFit;
      this._markDirty();
      this._pushPreview();
    }
    _activePanelHtml() {
      if (this.activeTab === "colors") return this._renderColors();
      if (this.activeTab === "logo") return this._renderLogo();
      return this._renderGeneral();
    }
    _render() {
      if (!this.root) return;
      const canSave = this._isFormValid();
      const status = this._status;
      this.root.innerHTML = `
            <div class="settings-page theme-creator-page animate-fade">
                <div class="settings-header-bar theme-creator-header-bar">
                    <div class="theme-creator-header-title">
                        <i data-lucide="palette"></i>
                        <h1 class="settings-title">${escapeHtml(this.editMode ? t("theme_creator_edit_title") : t("theme_creator"))}</h1>
                    </div>
                    <div class="settings-lang-wrap">
                        <label for="theme-creator-start">${escapeHtml(t("theme_creator_start_from"))}</label>
                        <select class="settings-select" id="theme-creator-start">${this._startFromOptionsHtml()}</select>
                    </div>
                </div>

                <div class="settings-card-wide theme-creator-shell">
                    <div class="settings-inner">
                        <nav class="settings-sidebar">
                            <button type="button" class="settings-tab-btn ${this.activeTab === "general" ? "active" : ""}" data-tc-tab="general">
                                <i data-lucide="sliders-horizontal"></i>
                                <span>${escapeHtml(t("theme_creator_tab_general"))}</span>
                            </button>
                            <button type="button" class="settings-tab-btn ${this.activeTab === "colors" ? "active" : ""}" data-tc-tab="colors">
                                <i data-lucide="palette"></i>
                                <span>${escapeHtml(t("theme_creator_tab_colors"))}</span>
                            </button>
                            <button type="button" class="settings-tab-btn ${this.activeTab === "logo" ? "active" : ""}" data-tc-tab="logo">
                                <i data-lucide="image"></i>
                                <span>${escapeHtml(t("theme_creator_tab_logo"))}</span>
                            </button>
                        </nav>
                        <div class="settings-inner-content">
                            <header class="settings-content-header">
                                <h2 class="settings-content-title">${escapeHtml(this._contentTitle())}</h2>
                                <p class="settings-content-desc text-muted">${escapeHtml(this._contentDesc())}</p>
                            </header>
                            <div class="settings-panel-card theme-creator-panel-card">
                                ${this._activePanelHtml()}
                            </div>
                        </div>
                    </div>
                </div>

                <div class="settings-footer theme-creator-footer">
                    <div class="app-info">
                        <div class="theme-creator-status ${status.text ? `theme-creator-status--${status.type || "info"}` : ""}" id="theme-creator-status" ${status.text ? "" : "hidden"}>${escapeHtml(status.text || "")}</div>
                    </div>
                    <div class="theme-creator-actions">
                        <button type="button" class="btn-secondary" id="theme-creator-cancel">${escapeHtml(t("cancel"))}</button>
                        <button type="button" class="btn-secondary" id="theme-creator-export" ${canSave ? "" : "disabled"}>${escapeHtml(t("theme_creator_export"))}</button>
                        <button type="button" class="btn primary" id="theme-creator-save" ${canSave ? "" : "disabled"}>
                            <i data-lucide="save"></i>
                            <span>${escapeHtml(t("theme_creator_save"))}</span>
                        </button>
                    </div>
                </div>
            </div>
        `;
      if (this.activeTab === "general" && !this._idError() && !this.editMode) {
        const hint = this.root.querySelector("#theme-creator-id-hint");
        if (hint) {
          hint.textContent = t("theme_creator_id_invalid");
          hint.className = "theme-creator-field-hint theme-creator-field-hint--muted";
        }
      }
    }
    _setStatus(type, text) {
      this._status = { type: type || "", text: text || "" };
      const el = this.root?.querySelector("#theme-creator-status");
      if (!el) return;
      if (!text) {
        el.hidden = true;
        el.textContent = "";
        el.className = "theme-creator-status";
        return;
      }
      el.hidden = false;
      el.className = `theme-creator-status theme-creator-status--${type || "info"}`;
      el.textContent = text;
    }
    _updateSaveButtons() {
      const canSave = this._isFormValid();
      const save = this.root?.querySelector("#theme-creator-save");
      const exp = this.root?.querySelector("#theme-creator-export");
      if (save) save.disabled = !canSave;
      if (exp) exp.disabled = !canSave;
    }
    _refreshIdentityHints() {
      const nameHint = this.root?.querySelector("#theme-creator-name-hint");
      const idHint = this.root?.querySelector("#theme-creator-id-hint");
      const nameInput = this.root?.querySelector("#theme-creator-name");
      const idInput = this.root?.querySelector("#theme-creator-id");
      const nameErr = this._nameError();
      const idErr = this._idError();
      if (nameHint) {
        nameHint.textContent = nameErr;
        nameHint.className = `theme-creator-field-hint${nameErr ? " theme-creator-field-hint--error" : ""}`;
      }
      if (nameInput) nameInput.classList.toggle("theme-creator-input-error", !!nameErr);
      if (idHint) {
        if (idErr) {
          idHint.textContent = idErr;
          idHint.className = "theme-creator-field-hint theme-creator-field-hint--error";
        } else {
          idHint.textContent = t("theme_creator_id_invalid");
          idHint.className = "theme-creator-field-hint theme-creator-field-hint--muted";
        }
      }
      if (idInput) idInput.classList.toggle("theme-creator-input-error", !!idErr);
      this._updateSaveButtons();
    }
    _markDirty() {
      this.dirty = true;
    }
    _syncSliderLabels() {
      const L = this.logoLayout;
      const set = (id, text) => {
        const el = this.root?.querySelector(`#${id}`);
        if (el) el.textContent = text;
      };
      set("tc-w-val", String(L.width));
      set("tc-s-val", L.scale.toFixed(2));
      set("tc-ox-val", String(L.offsetX));
      set("tc-oy-val", String(L.offsetY));
      set("tc-cs-val", L.collapsedScale.toFixed(2));
      set("tc-cox-val", String(L.collapsedOffsetX));
      set("tc-op-val", `${Math.round(L.opacity * 100)}%`);
    }
    _syncSlidersFromLayout() {
      const L = this.logoLayout;
      const setVal = (id, v) => {
        const el = this.root?.querySelector(`#${id}`);
        if (el) {
          el.value = String(v);
          syncRangeFill(el);
        }
      };
      setVal("tc-width", L.width);
      setVal("tc-scale", Math.round(L.scale * 100));
      setVal("tc-offset-x", L.offsetX);
      setVal("tc-offset-y", L.offsetY);
      setVal("tc-collapsed-scale", Math.round(L.collapsedScale * 100));
      setVal("tc-collapsed-x", L.collapsedOffsetX);
      setVal("tc-opacity", Math.round(L.opacity * 100));
      this._syncSliderLabels();
    }
    _refreshColorRow(key) {
      const cell = this.root?.querySelector(`.theme-creator-color-cell[data-token="${key}"]`);
      if (!cell) return;
      const parsed = parseColor(this.tokens[key]) || { r: 128, g: 128, b: 128, a: 1 };
      const swatch = cell.querySelector(".theme-creator-color-swatch");
      const text = cell.querySelector(".theme-creator-color-text");
      const opacity = cell.querySelector(".theme-creator-opacity");
      const autoToggle = cell.querySelector(".theme-creator-auto-toggle");
      const isAuto = this.autoKeys.has(key);
      if (swatch) {
        swatch.value = toHex6(parsed);
        swatch.disabled = isAuto;
      }
      if (text) {
        text.value = this.tokens[key] || "";
        text.title = text.value;
        text.readOnly = isAuto;
      }
      if (opacity) {
        opacity.value = String(Math.round(parsed.a * 100));
        opacity.disabled = isAuto;
        syncRangeFill(opacity);
      }
      const opacityVal = cell.querySelector(".theme-creator-opacity-val");
      if (opacityVal) opacityVal.textContent = `${Math.round(parsed.a * 100)}%`;
      if (autoToggle) autoToggle.checked = isAuto;
    }
    _randomizeColors() {
      const palette = generateRandomPalette(this.randomMode);
      Object.assign(this.tokens, palette);
      this.autoKeys = this.randomMode === "chaos" ? /* @__PURE__ */ new Set() : new Set(COLOR_TOKENS.filter((c) => c.derived).map((c) => c.key));
      this._recomputeDerived();
      for (const { key } of COLOR_TOKENS) this._refreshColorRow(key);
      this._markDirty();
      this._pushPreview();
    }
    _updateReplicaLogos() {
      const src = this._currentLogoUrl();
      this.root?.querySelectorAll(".theme-creator-replica-logo").forEach((img) => {
        setSharpLogo(img, src);
      });
    }
    _switchTab(tab) {
      if (this.activeTab === tab) return;
      this.activeTab = tab;
      this._render();
      this._bind();
      if (window.lucide) window.lucide.createIcons();
      this._pushPreview();
    }
    _bind() {
      if (this._abort) this._abort.abort();
      this._abort = new AbortController();
      const { signal } = this._abort;
      const root = this.root;
      if (!root) return;
      root.querySelectorAll(".tweak-range").forEach(syncRangeFill);
      root.addEventListener("input", (e) => {
        if (e.target?.classList?.contains("tweak-range")) syncRangeFill(e.target);
      }, { signal });
      root.querySelectorAll("[data-tc-tab]").forEach((btn) => {
        btn.addEventListener("click", () => this._switchTab(btn.getAttribute("data-tc-tab")), { signal });
      });
      root.querySelector("#theme-creator-cancel")?.addEventListener("click", () => this.close(), { signal });
      root.querySelector("#theme-creator-save")?.addEventListener("click", () => this._save("install"), { signal });
      root.querySelector("#theme-creator-export")?.addEventListener("click", () => this._save("export"), { signal });
      root.querySelector("#theme-creator-start")?.addEventListener("change", async (e) => {
        if (this.dirty) {
          const ok = await appConfirm({
            title: t("theme_creator"),
            message: t("theme_creator_discard"),
            confirmLabel: t("ok"),
            cancelLabel: t("cancel")
          });
          if (!ok) {
            e.target.value = this.startFrom;
            return;
          }
        }
        this._seedFromTheme(e.target.value, { keepIdentity: true });
        this.dirty = true;
        this._render();
        this._bind();
        this._pushPreview();
        if (window.lucide) window.lucide.createIcons();
      }, { signal });
      root.querySelector("#theme-creator-name")?.addEventListener("input", (e) => {
        this.nameTouched = true;
        this.displayName = e.target.value;
        if (!this.idManual) {
          this.themeId = slugify(this.displayName);
          const idEl = root.querySelector("#theme-creator-id");
          if (idEl) idEl.value = this.themeId;
        }
        this._refreshIdentityHints();
        this._markDirty();
      }, { signal });
      root.querySelector("#theme-creator-name")?.addEventListener("blur", () => {
        this.nameTouched = true;
        this._refreshIdentityHints();
      }, { signal });
      root.querySelector("#theme-creator-id")?.addEventListener("input", (e) => {
        if (this.editMode) {
          e.target.value = this.themeId;
          return;
        }
        this.idManual = true;
        this.idTouched = true;
        this.themeId = e.target.value.trim().toLowerCase();
        this._refreshIdentityHints();
        this._markDirty();
      }, { signal });
      root.querySelector("#theme-creator-id")?.addEventListener("blur", () => {
        this.idTouched = true;
        this._refreshIdentityHints();
      }, { signal });
      root.querySelectorAll(".theme-creator-color-swatch").forEach((el) => {
        el.addEventListener("input", (e) => {
          const key = e.target.dataset.token;
          if (!key || this.autoKeys.has(key)) return;
          const prev = parseColor(this.tokens[key]) || { a: 1 };
          const next = parseColor(e.target.value);
          if (!next) return;
          next.a = prev.a;
          this.tokens[key] = toCssColor(next);
          if (key === "primary-green") {
            this.tokens["primary-rgb"] = rgbTripletFromPrimary(this.tokens[key]);
          }
          this._recomputeDerived();
          for (const k of this.autoKeys) this._refreshColorRow(k);
          const text = root.querySelector(`.theme-creator-color-text[data-token="${key}"]`);
          if (text) text.value = this.tokens[key];
          this._markDirty();
          this._pushPreview();
        }, { signal });
      });
      root.querySelectorAll(".theme-creator-color-text").forEach((el) => {
        el.addEventListener("change", (e) => {
          const key = e.target.dataset.token;
          if (!key || this.autoKeys.has(key)) return;
          const parsed = parseColor(e.target.value);
          if (!parsed) {
            e.target.value = this.tokens[key] || "";
            return;
          }
          this.tokens[key] = toCssColor(parsed);
          e.target.value = this.tokens[key];
          if (key === "primary-green") {
            this.tokens["primary-rgb"] = rgbTripletFromPrimary(this.tokens[key]);
          }
          this.autoKeys.delete(key);
          this._recomputeDerived();
          this._refreshColorRow(key);
          for (const k of this.autoKeys) this._refreshColorRow(k);
          this._markDirty();
          this._pushPreview();
        }, { signal });
      });
      root.querySelectorAll(".theme-creator-opacity").forEach((el) => {
        el.addEventListener("input", (e) => {
          const key = e.target.dataset.token;
          if (!key || this.autoKeys.has(key)) return;
          const parsed = parseColor(this.tokens[key]) || { r: 128, g: 128, b: 128, a: 1 };
          parsed.a = Number(e.target.value) / 100;
          this.tokens[key] = toCssColor(parsed);
          const text = root.querySelector(`.theme-creator-color-text[data-token="${key}"]`);
          if (text) {
            text.value = this.tokens[key];
            text.title = text.value;
          }
          const val = root.querySelector(`.theme-creator-opacity-val[data-token="${key}"]`);
          if (val) val.textContent = `${e.target.value}%`;
          this._markDirty();
          this._pushPreview();
        }, { signal });
      });
      root.querySelector("#tc-random-mode")?.addEventListener("change", (e) => {
        this.randomMode = e.target.value;
      }, { signal });
      root.querySelector("#tc-randomize")?.addEventListener("click", () => this._randomizeColors(), { signal });
      root.querySelectorAll(".theme-creator-auto-toggle").forEach((el) => {
        el.addEventListener("change", (e) => {
          const key = e.currentTarget.dataset.token;
          if (!key) return;
          if (e.currentTarget.checked) {
            this.autoKeys.add(key);
            this.tokens[key] = deriveToken(key, this.tokens);
          } else {
            this.autoKeys.delete(key);
          }
          this._refreshColorRow(key);
          this._markDirty();
          this._pushPreview();
        }, { signal });
      });
      const pickBtn = root.querySelector("#theme-creator-pick-logo");
      const fileInput = root.querySelector("#theme-creator-logo-file");
      pickBtn?.addEventListener("click", () => fileInput?.click(), { signal });
      fileInput?.addEventListener("change", async (e) => {
        const file = e.target.files?.[0];
        if (!file) return;
        try {
          const { dataUrl } = await resizeLogoFile(file);
          this.logoDataUrl = dataUrl;
          this._pendingLogoFetch = "";
          this._syncBuiltInLogoActive();
          this._markDirty();
          this._pushPreview();
        } catch (err) {
          this._setStatus("error", err?.message || t("theme_creator_logo_failed"));
        }
      }, { signal });
      root.querySelectorAll(".theme-creator-builtin-logo").forEach((btn) => {
        btn.addEventListener("click", () => {
          this._useBuiltInLogo(btn.dataset.logoUrl, btn.dataset.logoTheme);
        }, { signal });
      });
      const bindRange = (id, apply) => {
        root.querySelector(id)?.addEventListener("input", (e) => {
          apply(Number(e.target.value));
          this.logoLayout = normalizeLogoLayout(this.logoLayout);
          this._syncSliderLabels();
          this._markDirty();
          this._pushPreview();
        }, { signal });
      };
      bindRange("#tc-width", (v) => {
        this.logoLayout.width = v;
      });
      bindRange("#tc-scale", (v) => {
        this.logoLayout.scale = v / 100;
      });
      bindRange("#tc-offset-x", (v) => {
        this.logoLayout.offsetX = v;
      });
      bindRange("#tc-offset-y", (v) => {
        this.logoLayout.offsetY = v;
      });
      bindRange("#tc-collapsed-scale", (v) => {
        this.logoLayout.collapsedScale = v / 100;
      });
      bindRange("#tc-collapsed-x", (v) => {
        this.logoLayout.collapsedOffsetX = v;
      });
      bindRange("#tc-opacity", (v) => {
        this.logoLayout.opacity = v / 100;
      });
      root.querySelector("#tc-object-fit")?.addEventListener("change", (e) => {
        this.logoLayout.objectFit = e.target.value;
        this._markDirty();
        this._pushPreview();
      }, { signal });
      this._bindReplicaLogoEdit(signal);
    }
    _bindReplicaLogoEdit(signal) {
      const logos = this.root?.querySelectorAll(".theme-creator-replica-logo") || [];
      logos.forEach((logo) => {
        const replica = logo.closest(".theme-creator-sidebar-replica");
        const collapsed = replica?.getAttribute("data-collapsed") === "1";
        logo.addEventListener("pointerdown", (e) => {
          if (e.button !== 0) return;
          e.preventDefault();
          e.stopPropagation();
          logo.setPointerCapture?.(e.pointerId);
          this._logoDrag = {
            startX: e.clientX,
            startY: e.clientY,
            baseX: collapsed ? this.logoLayout.collapsedOffsetX : this.logoLayout.offsetX,
            baseY: this.logoLayout.offsetY,
            collapsed
          };
          const onMove = (ev) => {
            if (!this._logoDrag) return;
            const dx = Math.round(ev.clientX - this._logoDrag.startX);
            const dy = Math.round(ev.clientY - this._logoDrag.startY);
            if (this._logoDrag.collapsed) {
              this.logoLayout.collapsedOffsetX = Math.max(-40, Math.min(40, this._logoDrag.baseX + dx));
            } else {
              this.logoLayout.offsetX = Math.max(-40, Math.min(40, this._logoDrag.baseX + dx));
              this.logoLayout.offsetY = Math.max(-40, Math.min(40, this._logoDrag.baseY - dy));
            }
            this.logoLayout = normalizeLogoLayout(this.logoLayout);
            this._syncSlidersFromLayout();
            this._markDirty();
            this._pushPreview();
          };
          const onUp = () => {
            this._logoDrag = null;
            window.removeEventListener("pointermove", onMove);
            window.removeEventListener("pointerup", onUp);
          };
          window.addEventListener("pointermove", onMove);
          window.addEventListener("pointerup", onUp);
        }, { signal });
        logo.addEventListener("wheel", (e) => {
          e.preventDefault();
          e.stopPropagation();
          const delta = e.deltaY > 0 ? -0.05 : 0.05;
          if (collapsed) {
            this.logoLayout.collapsedScale = Math.max(0.25, Math.min(3, this.logoLayout.collapsedScale + delta));
          } else {
            this.logoLayout.scale = Math.max(0.25, Math.min(3, this.logoLayout.scale + delta));
          }
          this.logoLayout = normalizeLogoLayout(this.logoLayout);
          this._syncSlidersFromLayout();
          this._markDirty();
          this._pushPreview();
        }, { signal, passive: false });
      });
    }
    _buildManifest() {
      this._recomputeDerived();
      const tokens = {};
      for (const { key } of COLOR_TOKENS) tokens[key] = this.tokens[key];
      tokens["primary-rgb"] = this.tokens["primary-rgb"] || rgbTripletFromPrimary(tokens["primary-green"]);
      return {
        formatVersion: 2,
        id: this.themeId,
        displayName: this.displayName.trim(),
        tokens,
        logoLayout: normalizeLogoLayout(this.logoLayout)
      };
    }
    async _ensureLogoDataUrl() {
      if (this.logoDataUrl) return this.logoDataUrl;
      const url = this._pendingLogoFetch || getLogoUrlForTheme(this.startFrom);
      const res = await fetch(url);
      if (!res.ok) throw new Error(t("theme_creator_logo_failed"));
      const blob = await res.blob();
      const mime = blob.type || "image/png";
      const ext = mime.includes("gif") ? "gif" : mime.includes("webp") ? "webp" : mime.includes("jpeg") || mime.includes("jpg") ? "jpg" : "png";
      const file = new File([blob], `logo.${ext}`, { type: mime });
      const { dataUrl } = await resizeLogoFile(file);
      this.logoDataUrl = dataUrl;
      return dataUrl;
    }
    async _save(mode) {
      this.nameTouched = true;
      this.idTouched = true;
      this._refreshIdentityHints();
      if (!this._isFormValid()) {
        this._setStatus("error", this._nameError() || this._idError() || t("theme_creator_id_invalid"));
        this._switchTab("general");
        return;
      }
      this._setStatus("info", t("theme_creator_saving"));
      try {
        const logoDataUrl = await this._ensureLogoDataUrl();
        const manifest = this._buildManifest();
        window.chrome?.webview?.postMessage({
          type: "SAVE_THEME_PACKAGE",
          mode,
          manifest,
          logoDataUrl
        });
      } catch (err) {
        this._setStatus("error", err?.message || t("theme_creator_save_failed"));
      }
    }
    handleSaveResult(data) {
      if (!data) return;
      if (data.ok) {
        this.dirty = false;
        if (data.mode === "export") {
          this._setStatus("success", t("theme_creator_export_success"));
        } else {
          this._setStatus("success", t("theme_creator_save_success", data.displayName || data.themeId || ""));
        }
      } else {
        this._setStatus("error", data.error || t("theme_creator_save_failed"));
      }
    }
    async close() {
      if (this._closing) return;
      if (this.dirty) {
        const ok = await appConfirm({
          title: t("theme_creator"),
          message: t("theme_creator_discard"),
          confirmLabel: t("ok"),
          cancelLabel: t("cancel")
        });
        if (!ok) return;
      }
      this._closing = true;
      if (this._abort) {
        this._abort.abort();
        this._abort = null;
      }
      try {
        window.chrome?.webview?.postMessage({ type: "CLOSE_THEME_CREATOR" });
      } catch {
      }
    }
  };
  function mountThemeCreator(root) {
    const creator = new ThemeCreator(root);
    creator.mount();
    return creator;
  }

  // ../../WebSrc/js/utils/themedSelectMenu.js
  var STYLE_ID = "themed-select-menu-style";
  var MENU_CSS = `
.themed-select-menu {
    position: fixed;
    z-index: 100000;
    box-sizing: border-box;
    margin: 0;
    padding: 0;
    overflow-y: auto;
    background: var(--bg-surface-light);
    color: var(--text-main);
    border: 1px solid var(--border-color);
    border-radius: 4px;
    box-shadow: 0 6px 16px rgba(0, 0, 0, 0.4);
    font-family: inherit;
    user-select: none;
}
.themed-select-menu-item {
    padding: 3px 10px;
    white-space: nowrap;
    cursor: default;
}
.themed-select-menu-item.active {
    background: var(--primary-green);
    color: var(--on-primary);
}
.themed-select-menu-item.disabled {
    color: var(--text-muted);
}
`;
  var menu = null;
  var owner = null;
  var activeIndex = -1;
  var installed = false;
  function isThemable(select) {
    return select instanceof HTMLSelectElement && !select.disabled && !select.multiple && select.size <= 1 && select.options.length > 0;
  }
  function items() {
    return menu ? [...menu.querySelectorAll(".themed-select-menu-item")] : [];
  }
  function setActive(index, scroll = false) {
    const rows = items();
    if (!rows.length) return;
    activeIndex = Math.max(0, Math.min(index, rows.length - 1));
    rows.forEach((row, i) => row.classList.toggle("active", i === activeIndex));
    if (scroll) rows[activeIndex].scrollIntoView({ block: "nearest" });
  }
  function moveActive(step) {
    const rows = items();
    let i = activeIndex;
    for (let n = 0; n < rows.length; n++) {
      i += step;
      if (i < 0 || i >= rows.length) return;
      if (!rows[i].classList.contains("disabled")) {
        setActive(i, true);
        return;
      }
    }
  }
  function closeMenu({ refocus = false } = {}) {
    if (!menu) return;
    menu.remove();
    menu = null;
    const select = owner;
    owner = null;
    activeIndex = -1;
    if (refocus && select?.isConnected) select.focus({ preventScroll: true });
  }
  function choose(index) {
    const select = owner;
    const row = items()[index];
    if (!select || !row || row.classList.contains("disabled")) return;
    const optionIndex = Number(row.dataset.optionIndex);
    closeMenu({ refocus: true });
    if (select.selectedIndex === optionIndex) return;
    select.selectedIndex = optionIndex;
    select.dispatchEvent(new Event("input", { bubbles: true }));
    select.dispatchEvent(new Event("change", { bubbles: true }));
  }
  function positionMenu(select) {
    const rect = select.getBoundingClientRect();
    const margin = 4;
    menu.style.minWidth = `${Math.round(rect.width)}px`;
    menu.style.left = `${Math.round(rect.left)}px`;
    const below = window.innerHeight - rect.bottom - margin;
    const above = rect.top - margin;
    const natural = menu.scrollHeight;
    const openUp = natural > below && above > below;
    const maxHeight = Math.max(80, openUp ? above : below);
    menu.style.maxHeight = `${Math.floor(maxHeight)}px`;
    menu.style.top = openUp ? `${Math.round(rect.top - Math.min(natural, maxHeight))}px` : `${Math.round(rect.bottom)}px`;
    const overflowX = menu.getBoundingClientRect().right - (window.innerWidth - margin);
    if (overflowX > 0) menu.style.left = `${Math.max(margin, Math.round(rect.left - overflowX))}px`;
  }
  function openMenu(select) {
    closeMenu();
    owner = select;
    menu = document.createElement("div");
    menu.className = "themed-select-menu";
    menu.setAttribute("role", "listbox");
    const cs = getComputedStyle(select);
    menu.style.fontSize = cs.fontSize;
    menu.style.fontWeight = cs.fontWeight;
    [...select.options].forEach((opt, i) => {
      if (opt.hidden) return;
      const row = document.createElement("div");
      row.className = "themed-select-menu-item";
      if (opt.disabled) row.classList.add("disabled");
      row.setAttribute("role", "option");
      row.dataset.optionIndex = String(i);
      row.textContent = opt.textContent;
      menu.appendChild(row);
    });
    menu.addEventListener("mousemove", (e) => {
      const row = e.target.closest(".themed-select-menu-item");
      if (!row || row.classList.contains("disabled")) return;
      const index = items().indexOf(row);
      if (index !== activeIndex) setActive(index);
    });
    menu.addEventListener("mousedown", (e) => e.preventDefault());
    menu.addEventListener("click", (e) => {
      const row = e.target.closest(".themed-select-menu-item");
      if (row) choose(items().indexOf(row));
    });
    document.body.appendChild(menu);
    positionMenu(select);
    const current = items().findIndex((row) => Number(row.dataset.optionIndex) === select.selectedIndex);
    setActive(current >= 0 ? current : 0, true);
  }
  function onMouseDown(e) {
    if (menu && menu.contains(e.target)) return;
    const select = e.target instanceof Element ? e.target.closest("select") : null;
    if (e.button !== 0 || !isThemable(select)) {
      if (menu) closeMenu();
      return;
    }
    e.preventDefault();
    if (owner === select) {
      closeMenu({ refocus: true });
      return;
    }
    select.focus({ preventScroll: true });
    openMenu(select);
  }
  function onKeyDown(e) {
    if (menu) {
      switch (e.key) {
        case "ArrowDown":
          moveActive(1);
          break;
        case "ArrowUp":
          moveActive(-1);
          break;
        case "Home":
          setActive(0, true);
          break;
        case "End":
          setActive(items().length - 1, true);
          break;
        case "Enter":
        case " ":
          choose(activeIndex);
          break;
        case "Escape":
        case "Tab":
          closeMenu({ refocus: e.key === "Escape" });
          if (e.key === "Tab") return;
          break;
        default:
          return;
      }
      e.preventDefault();
      e.stopPropagation();
      return;
    }
    const select = e.target;
    if (!isThemable(select)) return;
    const opens = e.key === "F4" || e.altKey && (e.key === "ArrowDown" || e.key === "ArrowUp") || e.key === " " || e.key === "Enter";
    if (!opens) return;
    e.preventDefault();
    e.stopPropagation();
    openMenu(select);
  }
  function onScroll(e) {
    if (menu && !menu.contains(e.target)) closeMenu();
  }
  function installThemedSelectMenus() {
    if (installed) return;
    installed = true;
    if (!document.getElementById(STYLE_ID)) {
      const style = document.createElement("style");
      style.id = STYLE_ID;
      style.textContent = MENU_CSS;
      document.head.appendChild(style);
    }
    document.addEventListener("mousedown", onMouseDown, true);
    document.addEventListener("keydown", onKeyDown, true);
    document.addEventListener("scroll", onScroll, true);
    window.addEventListener("resize", () => closeMenu());
    window.addEventListener("blur", () => closeMenu());
  }

  // ../../WebSrc/js/theme-creator.js
  window._t = (key, ...args) => translator.t(key, ...args);
  window.translator = translator;
  installAppConfirmGlobals();
  installThemedSelectMenus();
  function resolvePopoutLanguage() {
    try {
      const params = new URLSearchParams(window.location.search || "");
      let lang = (params.get("lang") || "").trim();
      if (!lang || lang === "auto") {
        lang = translator.detectLanguage();
      }
      return lang || "en-US";
    } catch {
      return "en-US";
    }
  }
  function resolveEditThemeId() {
    try {
      const params = new URLSearchParams(window.location.search || "");
      return (params.get("edit") || "").trim();
    } catch {
      return "";
    }
  }
  async function fetchLocale(lang) {
    const res = await fetch(`https://f76manager.app/locales/${lang}.json`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
  }
  async function loadLocale() {
    try {
      const en = await fetchLocale("en-US");
      translator.loadedLanguages["en-US"] = en;
      translator.currentLanguage = "en-US";
      const lang = resolvePopoutLanguage();
      if (lang !== "en-US") {
        try {
          const json = await fetchLocale(lang);
          translator.loadedLanguages[lang] = json;
          translator.currentLanguage = lang;
        } catch (e) {
          console.warn("[THEME-CREATOR] selected locale load failed, using en-US", e);
        }
      }
      try {
        document.title = window._t("theme_creator_window_title") || "F76 Manager \u2014 Theme Creator";
      } catch {
      }
    } catch (e) {
      console.warn("[THEME-CREATOR] locale load failed", e);
    }
  }
  function registerBootThemes() {
    try {
      const ids = Array.isArray(window.__F76_USER_THEME_IDS) ? window.__F76_USER_THEME_IDS : [];
      const cssMap = window.__F76_USER_THEME_CSS || {};
      registerUserThemesFromHost(ids.map((id) => ({
        id,
        displayName: id,
        logo: `user-theme-logo/${id}`,
        css: cssMap[id] || ""
      })));
    } catch (e) {
      console.warn("[THEME-CREATOR] register themes failed", e);
    }
  }
  function requestEditData(id) {
    if (!id) return;
    window.chrome?.webview?.postMessage({ type: "GET_THEME_EDIT_DATA", id });
  }
  async function boot() {
    await loadLocale();
    registerBootThemes();
    const root = document.getElementById("theme-creator-root");
    const creator = mountThemeCreator(root);
    window.__themeCreator = creator;
    const initialEdit = resolveEditThemeId();
    if (initialEdit) requestEditData(initialEdit);
    window.chrome?.webview?.addEventListener("message", async (event) => {
      const data = event.data;
      if (!data || typeof data !== "object") return;
      if (data.type === "THEME_SAVE_RESULT") {
        creator.handleSaveResult(data);
      } else if (data.type === "THEME_EDIT_DATA") {
        if (data.ok) {
          creator.loadForEdit(data);
        } else {
          creator._setStatus("error", data.error || window._t("theme_creator_save_failed"));
        }
      } else if (data.type === "THEME_CREATOR_LOAD") {
        const id = (data.editThemeId || "").trim();
        if (!id) return;
        if (creator.dirty) {
          const ok = await window.appConfirm?.({
            title: window._t("theme_creator"),
            message: window._t("theme_creator_discard"),
            confirmLabel: window._t("ok"),
            cancelLabel: window._t("cancel")
          });
          if (!ok) return;
        }
        requestEditData(id);
      }
    });
    if (window.lucide) {
      try {
        window.lucide.createIcons();
      } catch {
      }
    }
  }
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
