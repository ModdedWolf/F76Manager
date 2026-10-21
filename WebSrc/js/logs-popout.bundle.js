(() => {
  var __defProp = Object.defineProperty;
  var __defNormalProp = (obj, key, value) => key in obj ? __defProp(obj, key, { enumerable: true, configurable: true, writable: true, value }) : obj[key] = value;
  var __publicField = (obj, key, value) => __defNormalProp(obj, typeof key !== "symbol" ? key + "" : key, value);

  // ../../WebSrc/js/components/Logs.js
  var _Logs = class _Logs {
    constructor(options = {}) {
      this.activeTab = "activity";
      this.showDebugLogs = false;
      this.isPopout = !!options.isPopout;
      this._actionsMenuAbort = null;
      if (!_Logs._delegationBound) {
        _Logs._delegationBound = true;
        document.addEventListener("click", (event) => {
          const tab = event.target.closest(".logs-page .log-tab");
          if (!tab) return;
          const page = tab.closest(".logs-page");
          const logs2 = page?._logsInstance || window.app?.sections?.logs;
          if (!logs2) return;
          const tabName = tab.getAttribute("data-tab");
          if (tabName) logs2.switchTab(tabName);
        });
      }
    }
    render(data) {
      const logs2 = data ? data.logs || { activity: [], errors: [] } : { activity: [], errors: [] };
      const errorBadgeCount = typeof logs2.errorCount === "number" ? logs2.errorCount : (logs2.errors || []).length;
      const currentLogs = this.activeTab === "activity" ? logs2.activity : logs2.errors;
      const shareLabel = this.activeTab === "errors" ? window._t("logs_share_errors") || "Share Error Log" : window._t("logs_share_activity") || "Share Activity Log";
      return `
            <div class="logs-page${this.isPopout ? " logs-page--popout" : ""}">
                <div class="logs-container">
                    <div class="logs-toolbar">
                        <div class="logs-tabs">
                            <button type="button" class="log-tab ${this.activeTab === "activity" ? "active" : ""}" data-tab="activity">
                                <i data-lucide="activity"></i>
                                <span>${window._t("activity_log")}</span>
                            </button>
                            <button type="button" class="log-tab ${this.activeTab === "errors" ? "active" : ""}" data-tab="errors">
                                <i data-lucide="alert-circle"></i>
                                <span>${window._t("error_log")}</span>
                                ${errorBadgeCount > 0 ? `<span class="error-badge">${errorBadgeCount}</span>` : ""}
                            </button>
                        </div>

                        <div class="logs-actions">
                            <button type="button" class="btn secondary logs-refresh-btn" id="refresh-logs" title="${this.escapeHtml(window._t("refresh") || "Refresh")}">
                                <i data-lucide="refresh-cw"></i>
                            </button>
                            <div class="logs-actions-wrap">
                                <button type="button" class="btn-secondary mods-actions-trigger" id="btn-logs-actions" aria-haspopup="true" aria-expanded="false" title="${this.escapeHtml(window._t("actions") || "Actions")}">
                                    <i data-lucide="more-horizontal"></i>
                                </button>
                                <div class="mods-actions-dropdown" id="logs-actions-dropdown" hidden>
                                    <button type="button" class="mods-actions-item" id="btn-logs-share">
                                        <i data-lucide="share-2"></i>
                                        <span id="btn-logs-share-label">${this.escapeHtml(shareLabel)}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-logs-open-folder">
                                        <i data-lucide="folder-open"></i>
                                        <span>${this.escapeHtml(window._t("logs_open_folder") || "Open Logs Folder")}</span>
                                    </button>
                                    ${this.isPopout ? "" : `
                                    <button type="button" class="mods-actions-item" id="btn-logs-popout">
                                        <i data-lucide="panel-top-open"></i>
                                        <span>${this.escapeHtml(window._t("logs_popout") || "Pop Out Window")}</span>
                                    </button>`}
                                    <div class="mods-actions-divider" role="separator"></div>
                                    <button type="button" class="mods-actions-item danger" id="btn-logs-clear-errors">
                                        <i data-lucide="trash-2"></i>
                                        <span>${this.escapeHtml(window._t("clear_error_log") || "Clear Errors")}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item danger" id="btn-logs-clear-activity">
                                        <i data-lucide="eraser"></i>
                                        <span>${this.escapeHtml(window._t("logs_clear_activity") || "Clear Activity Log")}</span>
                                    </button>
                                </div>
                            </div>
                        </div>
                    </div>

                    <div class="terminal-window">
                        <div class="terminal-header-static">
                            <span class="h-ts">TIME</span>
                            <span class="h-tag">TYPE</span>
                            <span class="h-content">LOG MESSAGE</span>
                        </div>
                        <div class="terminal-body" id="log-content">
                            ${currentLogs.length > 0 ? currentLogs.map((line) => this.formatLogLine(line)).join("") : `<div class="empty-terminal"><span>${window._t("no_log_entries")}</span></div>`}
                        </div>
                    </div>
                </div>
            </div>
        `;
    }
    formatLogLine(line) {
      const trimmed = (line || "").trim();
      if (!trimmed) return "";
      if (/^Actual value was/i.test(trimmed)) return "";
      let className = "";
      if (trimmed.includes("[ERROR]") || trimmed.includes("ERROR:")) className = "log-error";
      else if (trimmed.includes("[WARN]") || trimmed.includes("[WARNING]")) className = "log-warn";
      else if (trimmed.includes("[SUCCESS]")) className = "log-success";
      else if (trimmed.includes("[DEBUG]")) className = "log-debug";
      else if (trimmed.includes("[MODS]") || trimmed.includes("[IMPORT]") || trimmed.includes("[NEXUS]") || trimmed.includes("[DEPLOY]") || trimmed.includes("[ORDER]")) className = "log-info";
      else if (trimmed.includes("[DELETE]")) className = "log-warn";
      else if (trimmed.includes("[INI]")) className = "log-info";
      const tsMatch = trimmed.match(/^\[(.*?)\]/);
      let content = trimmed;
      let timestamp = "";
      if (tsMatch) {
        timestamp = tsMatch[0];
        content = line.substring(timestamp.length).trim();
      }
      const tagMatch = content.match(/^\[(.*?)\]/);
      let tag = "";
      if (tagMatch) {
        tag = tagMatch[0];
        content = content.substring(tag.length).trim();
      }
      content = content.replace(/KeyMatch=\w+/, "").replace(/\(Order: \d+\)/, "").replace(/result: NONE/i, "No match found").replace(/DataPath:.*$/, "(Game Folder)").replace(/Lookup Mod:.*$/, "").trim();
      if (!content && !tag) return "";
      const tagTitle = tag ? ` title="${this.escapeHtml(tag)}"` : "";
      return `
            <div class="log-line ${className}">
                <span class="log-ts">${this.escapeHtml(timestamp)}</span>
                <span class="log-tag"${tagTitle}>${this.escapeHtml(tag)}</span>
                <span class="log-content">${this.escapeHtml(content)}</span>
            </div>
        `;
    }
    escapeHtml(text) {
      const div = document.createElement("div");
      div.textContent = text ?? "";
      return div.innerHTML;
    }
    getLogsPayload(data) {
      return data ? data.logs || { activity: [], errors: [] } : { activity: [], errors: [] };
    }
    getCurrentLogLines(data) {
      const logs2 = this.getLogsPayload(data);
      return this.activeTab === "activity" ? logs2.activity || [] : logs2.errors || [];
    }
    renderLogContentHtml(lines) {
      if (!lines.length) {
        return `<div class="empty-terminal"><span>${window._t("no_log_entries")}</span></div>`;
      }
      return lines.map((line) => this.formatLogLine(line)).join("");
    }
    scrollLogContentToEnd(logContent) {
      if (!logContent) return;
      logContent.scrollTop = logContent.scrollHeight;
    }
    updateShareLabel() {
      const label = document.getElementById("btn-logs-share-label");
      if (!label) return;
      label.textContent = this.activeTab === "errors" ? window._t("logs_share_errors") || "Share Error Log" : window._t("logs_share_activity") || "Share Activity Log";
    }
    refreshView(data = window.app?.realData, scrollToEnd = false) {
      const root = this.isPopout ? document.querySelector(".logs-page") : document.querySelector(".logs-page:not(.logs-page--popout)") || document.querySelector(".logs-page");
      if (!root) return false;
      const logs2 = this.getLogsPayload(data);
      const errorBadgeCount = typeof logs2.errorCount === "number" ? logs2.errorCount : (logs2.errors || []).length;
      root.querySelectorAll(".log-tab").forEach((tab) => {
        tab.classList.toggle("active", tab.getAttribute("data-tab") === this.activeTab);
      });
      const errorsTab = root.querySelector('.log-tab[data-tab="errors"]');
      if (errorsTab) {
        const existingBadge = errorsTab.querySelector(".error-badge");
        if (errorBadgeCount > 0) {
          if (existingBadge) {
            existingBadge.textContent = String(errorBadgeCount);
          } else {
            errorsTab.insertAdjacentHTML("beforeend", `<span class="error-badge">${errorBadgeCount}</span>`);
          }
        } else if (existingBadge) {
          existingBadge.remove();
        }
      }
      this.updateShareLabel();
      const logContent = root.querySelector("#log-content");
      if (logContent) {
        logContent.innerHTML = this.renderLogContentHtml(this.getCurrentLogLines(data));
        if (scrollToEnd) this.scrollLogContentToEnd(logContent);
      }
      return true;
    }
    updateValues(data) {
      if (this.isPopout) {
        this.refreshView(data);
        return;
      }
      if (window.app?.currentSection === "logs" && this.refreshView(data)) {
        return;
      }
      if (window.app && typeof window.app.replaceCurrentSectionContent === "function") {
        window.app.replaceCurrentSectionContent();
      }
    }
    switchTab(tabName) {
      if (!tabName || tabName === this.activeTab) return;
      this.activeTab = tabName;
      this.refreshView(this.isPopout ? window.__logsPopoutData : window.app?.realData, true);
    }
    _closeActionsMenu() {
      const dropdown = document.getElementById("logs-actions-dropdown");
      const trigger = document.getElementById("btn-logs-actions");
      if (dropdown) dropdown.hidden = true;
      if (trigger) trigger.setAttribute("aria-expanded", "false");
    }
    _mountActionsMenu(root) {
      const trigger = root.querySelector("#btn-logs-actions");
      const dropdown = root.querySelector("#logs-actions-dropdown");
      if (!trigger || !dropdown) return;
      if (this._actionsMenuAbort) this._actionsMenuAbort.abort();
      this._actionsMenuAbort = new AbortController();
      const { signal } = this._actionsMenuAbort;
      dropdown.hidden = true;
      trigger.setAttribute("aria-expanded", "false");
      trigger.addEventListener("click", (e) => {
        e.stopPropagation();
        const open = dropdown.hidden;
        dropdown.hidden = !open;
        trigger.setAttribute("aria-expanded", open ? "true" : "false");
        if (open && window.lucide) window.lucide.createIcons();
      }, { signal });
      document.addEventListener("click", (e) => {
        if (!root.isConnected) return;
        if (!e.target.closest(".logs-actions-wrap")) this._closeActionsMenu();
      }, { signal });
      root.querySelector("#btn-logs-share")?.addEventListener("click", (e) => {
        e.stopPropagation();
        this._closeActionsMenu();
        window.chrome?.webview?.postMessage({ type: "SHARE_LOGS", tab: this.activeTab });
      }, { signal });
      root.querySelector("#btn-logs-open-folder")?.addEventListener("click", (e) => {
        e.stopPropagation();
        this._closeActionsMenu();
        window.chrome?.webview?.postMessage({ type: "OPEN_LOGS_FOLDER" });
      }, { signal });
      root.querySelector("#btn-logs-popout")?.addEventListener("click", (e) => {
        e.stopPropagation();
        this._closeActionsMenu();
        window.chrome?.webview?.postMessage({ type: "OPEN_LOGS_POPOUT" });
      }, { signal });
      root.querySelector("#btn-logs-clear-errors")?.addEventListener("click", (e) => {
        e.stopPropagation();
        this._closeActionsMenu();
        window.chrome?.webview?.postMessage({ type: "CLEAR_ERROR_LOG" });
      }, { signal });
      root.querySelector("#btn-logs-clear-activity")?.addEventListener("click", async (e) => {
        e.stopPropagation();
        this._closeActionsMenu();
        const title = window._t("logs_clear_activity") || "Clear Activity Log";
        const message = window._t("logs_clear_activity_confirm") || "Clear the activity log? This cannot be undone.";
        const confirmFn = window.appConfirm;
        let ok = true;
        if (typeof confirmFn === "function") {
          ok = await confirmFn({
            title,
            message,
            okText: title,
            cancelText: window._t("cancel") || "Cancel",
            danger: true
          });
        } else {
          ok = window.confirm(message);
        }
        if (!ok) return;
        window.chrome?.webview?.postMessage({ type: "CLEAR_ACTIVITY_LOG" });
      }, { signal });
    }
    onMount() {
      const root = document.querySelector(this.isPopout ? ".logs-page" : ".logs-page:not(.logs-page--popout)") || document.querySelector(".logs-page");
      if (!root) return;
      root._logsInstance = this;
      const refreshBtn = root.querySelector("#refresh-logs");
      if (refreshBtn) {
        refreshBtn.addEventListener("click", () => {
          if (this.isPopout) {
            window.chrome?.webview?.postMessage({ type: "GET_LOGS" });
          } else {
            window.chrome?.webview?.postMessage({ type: "GET_LOGS" });
          }
        });
      }
      this._mountActionsMenu(root);
      this.updateShareLabel();
      const logContent = root.querySelector("#log-content");
      if (logContent) this.scrollLogContentToEnd(logContent);
      if (window.lucide) window.lucide.createIcons();
    }
  };
  __publicField(_Logs, "_delegationBound", false);
  var Logs = _Logs;

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

  // ../../WebSrc/js/logs-popout.js
  window._t = (key, ...args) => translator.t(key, ...args);
  window.translator = translator;
  var logs = new Logs({ isPopout: true });
  window.__logsPopoutData = { logs: { activity: [], errors: [], errorCount: 0 } };
  var _appliedThemeId = null;
  function showToast(status) {
    const el = document.getElementById("logs-popout-toast");
    if (!el || !status) return;
    el.textContent = status.text || "";
    el.className = `logs-popout-toast ${status.type || "info"}`;
    el.hidden = false;
    requestAnimationFrame(() => el.classList.add("visible"));
    clearTimeout(showToast._timer);
    showToast._timer = setTimeout(() => {
      el.classList.remove("visible");
      setTimeout(() => {
        el.hidden = true;
      }, 200);
    }, 3200);
  }
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
          console.warn("[LOGS-POPOUT] selected locale load failed, using en-US", e);
        }
      }
    } catch (e) {
      console.warn("[LOGS-POPOUT] locale load failed", e);
    }
  }
  function applyThemeSync(payload) {
    if (!payload || typeof payload !== "object") return;
    const themeId = (payload.themeId || "").trim() || "fallout";
    const css = typeof payload.css === "string" ? payload.css : "";
    if (typeof payload.uiAnimations === "boolean") {
      document.body.classList.toggle("no-animations", payload.uiAnimations === false);
    }
    if (_appliedThemeId && _appliedThemeId !== themeId) {
      document.getElementById(`user-theme-${_appliedThemeId}`)?.remove();
    }
    document.documentElement.dataset.theme = themeId;
    _appliedThemeId = themeId;
    const styleId = `user-theme-${themeId}`;
    let styleEl = document.getElementById(styleId);
    if (css) {
      if (!styleEl) {
        styleEl = document.createElement("style");
        styleEl.id = styleId;
      }
      document.head.appendChild(styleEl);
      styleEl.textContent = css;
    } else if (styleEl) {
      styleEl.remove();
    }
  }
  function renderShell() {
    const root = document.getElementById("logs-popout-root");
    if (!root) return;
    root.innerHTML = logs.render(window.__logsPopoutData);
    logs.onMount();
  }
  function applyLogsData(payload) {
    window.__logsPopoutData = { logs: payload || { activity: [], errors: [], errorCount: 0 } };
    if (!logs.refreshView(window.__logsPopoutData, true)) {
      renderShell();
    }
    if (window.lucide) window.lucide.createIcons();
  }
  function requestLogs() {
    window.chrome?.webview?.postMessage({ type: "GET_LOGS" });
  }
  function requestThemeSync() {
    window.chrome?.webview?.postMessage({ type: "GET_THEME_SYNC" });
  }
  async function boot() {
    await loadLocale();
    renderShell();
    requestThemeSync();
    requestLogs();
    setInterval(requestLogs, 1500);
    window.chrome?.webview?.addEventListener("message", (event) => {
      const data = event.data;
      if (!data || typeof data !== "object") return;
      if (data.type === "THEME_SYNC") {
        applyThemeSync(data);
      } else if (data.type === "LOGS_DATA") {
        applyLogsData(data.logs);
      } else if (data.type === "STATUS" && data.status) {
        showToast(data.status);
        requestLogs();
      }
    });
  }
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
