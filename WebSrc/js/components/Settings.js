import {
    KEYBIND_DEFINITIONS,
    DEFAULT_KEYBINDS,
    getKeybinds,
    saveKeybindsLocal,
    formatChordFromEvent,
    findKeybindConflict,
    normalizeKeybinds
} from '../utils/keybinds.js';
import { escapeHtml, escapeAttr } from '../utils/htmlSafe.js';
import { DEFAULT_UI_THEME } from '../themes/registry.js';
import { applyUiTheme, getThemeOptionsForSettings } from '../utils/themeManager.js';

export class Settings {
    constructor() {
        this.activeTab = 'preferences';
        this.backups = [];
        this.backupListFilter = null;
        this.backupSearchQuery = '';
        this.backupPreview = null;
        this.backupPreviewId = null;
        this.backupPreviewLoading = false;
        this._keybindDraft = null;
        this._keybindCaptureAbort = null;
    }
    static PATHS = [
        { id: 'game-path-input', labelKey: 'game_path', placeholderKey: 'game_path_placeholder', target: 'game', key: 'gamePath' },
        { id: 'docs-path-input', labelKey: 'documents_path', placeholderKey: 'docs_path_placeholder', target: 'docs', key: 'documentsPath' },
        { id: 'local-appdata-path-input', labelKey: 'local_appdata_path', placeholderKey: 'local_appdata_path_placeholder', target: 'localAppData', key: 'localAppDataPath' },
        { id: 'strings-path-input', labelKey: 'strings_path', placeholderKey: 'strings_path_placeholder', target: 'strings', key: 'stringsPath' },
        { id: 'seven-zip-path-input', labelKey: 'seven_zip_path', placeholderKey: 'seven_zip_path_placeholder', target: 'sevenZipExe', key: 'sevenZipPath', picker: 'file' },
        { id: 'rar-tool-path-input', labelKey: 'rar_tool_path', placeholderKey: 'rar_tool_path_placeholder', target: 'rarToolExe', key: 'rarExtractorPath', picker: 'file' },
    ];

    static TOGGLES = [
        { id: 'tray-toggle', labelKey: 'minimize_to_tray', key: 'minimizeToTray' },
        { id: 'animations-toggle', labelKey: 'ui_animations', key: 'uiAnimations' },
        { id: 'platform-glow-toggle', labelKey: 'platform_badge_glow', key: 'platformBadgeGlow' },
        { id: 'sync-platforms-toggle', labelKey: 'sync_platforms', key: 'syncPlatforms' },
        { id: 'auto-force-toggle', labelKey: 'auto_conflict_override', key: 'autoForceDeploy' },
        { id: 'virtual-mod-mode-toggle', labelKey: 'virtual_mod_mode', key: 'virtualModMode' },
        { id: 'config-spellcheck-toggle', labelKey: 'config_editor_spellcheck', key: 'configEditorSpellCheck' },
        { id: 'confirm-delete-mod-toggle', labelKey: 'confirm_before_delete_mod', key: 'confirmBeforeDeleteMod' },
        { id: 'confirm-remove-old-mod-toggle', labelKey: 'confirm_before_remove_old_mod_on_update', key: 'confirmBeforeRemoveOldModOnUpdate' },
    ];

    render(data) {
        const ms = data ? (data.managerSettings || {}) : {};
        const hasNexus = !!ms.nexusLoggedIn;

        const t = (k) => window._t(k);

        const pathsHtml = Settings.PATHS.map(p => `
            <div class="tweak-item" style="flex-direction: column; align-items: flex-start; gap: 8px;">
                <div class="tweak-label">${t(p.labelKey)}</div>
                <div class="path-input-group" style="width: 100%;">
                    <input type="text" value="${ms[p.key] || ''}" id="${p.id}" placeholder="${t(p.placeholderKey)}">
                    <button class="icon-btn-sm browse-btn" data-target="${p.target}" data-picker="${p.picker || 'folder'}" title="Browse">
                        <i data-lucide="${p.picker === 'file' ? 'file-search' : 'folder-open'}"></i>
                    </button>
                </div>
            </div>
        `).join('');

        const toggleDefaults = {
            minimizeToTray: false,
            uiAnimations: true,
            platformBadgeGlow: true,
            syncPlatforms: true,
            autoForceDeploy: false,
            virtualModMode: false,
            configEditorSpellCheck: false,
            confirmBeforeDeleteMod: false,
            confirmBeforeRemoveOldModOnUpdate: false
        };
        const togglesHtml = Settings.TOGGLES.map(p => {
            const val = ms[p.key];
            const checked = val !== undefined ? val : toggleDefaults[p.key];
            const descKey = `${p.labelKey}_desc`;
            const desc = window._t(descKey);
            const descHtml = (desc && desc !== descKey) ? desc : '';
            const labelHtml = p.key === 'virtualModMode'
                ? `${t(p.labelKey)}<span class="text-muted">(Beta)</span>`
                : t(p.labelKey);
            return `
            <div class="tweak-item">
                <div class="tweak-info">
                    <div class="tweak-label">${labelHtml}</div>
                    <div class="tweak-desc">${descHtml}</div>
                </div>
                <label class="switch">
                    <input type="checkbox" id="${p.id}" ${checked ? 'checked' : ''}>
                    <span class="slider"></span>
                </label>
            </div>
        `; }).join('');

        const renderPathsPanel = () => `
            <div class="settings-card-inner">
                <div class="paths-grid">
                    ${pathsHtml}
                </div>
            </div>`;

        const renderPreferencesPanel = () => `
            <div class="settings-card-inner">
                <div class="tweak-item settings-nexus-wrap" style="flex-direction: column; align-items: flex-start; gap: 6px; border-bottom: 1px solid #1a1a1a; padding-bottom: 10px; margin-bottom: 4px;">
                    <div class="tweak-label">${t('nexus_connection')}</div>
                    <div class="path-input-group" style="width: 100%;">
                        ${hasNexus ?
                            `<div class="nexus-connected">
                                <span>
                                    <i data-lucide="check-circle"></i>
                                    ${t('nexus_connected')}
                                </span>
                                <button class="btn danger btn-compact" id="nexus-logout-btn">
                                    <i data-lucide="log-out"></i>
                                    <span>${t('nexus_logout')}</span>
                                </button>
                            </div>` :
                            `<button class="btn primary full-width nexus-login-btn" id="nexus-login-btn">
                                <i data-lucide="log-in"></i>
                                <span>${t('nexus_login')}</span>
                            </button>`
                        }
                    </div>
                    <div class="tweak-desc">
                        ${hasNexus ? t('nexus_connected_desc') : t('nexus_login_sso_hint')}
                    </div>
                </div>

                <div class="preferences-row">
                    ${togglesHtml}
                </div>

                <div class="tweak-item" style="border-top: 1px solid #1a1a1a; padding-top: 10px; margin-top: 4px;">
                    <div class="tweak-info">
                        <div class="tweak-label">${t('archive_key_label')}</div>
                        <div class="tweak-desc">${t('archive_key_desc')}</div>
                    </div>
                    <select class="tweak-select" id="archive-key-select">
                        <option value="auto" ${(ms.archiveKeyName || 'auto') === 'auto' ? 'selected' : ''}>${t('archive_key_auto')}</option>
                        <option value="sResourceArchive2List" ${ms.archiveKeyName === 'sResourceArchive2List' ? 'selected' : ''}>sResourceArchive2List</option>
                        <option value="sResourceIndexFileList" ${ms.archiveKeyName === 'sResourceIndexFileList' ? 'selected' : ''}>sResourceIndexFileList</option>
                    </select>
                </div>
            </div>`;

        const renderAdvancedPanel = () => `
            <div class="settings-card-inner settings-advanced-layout" id="settings-advanced-panel">
                <div class="backups-panel backups-panel-scroll">
                    <div class="backups-panel-sticky">
                        <div class="backups-panel-head">
                            <div>
                                <div class="tweak-label">${t('backups_panel_title')}</div>
                                <div class="tweak-desc">${t('backups_panel_desc')}</div>
                            </div>
                            <div class="backups-panel-head-actions">
                                <button type="button" class="btn secondary btn-slim" id="btn-refresh-backups">
                                    <i data-lucide="refresh-cw"></i>
                                    <span>${t('refresh')}</span>
                                </button>
                                <div class="mods-actions-menu-wrap advanced-maintenance-menu-wrap">
                                    <button type="button" class="btn secondary btn-slim advanced-maintenance-actions-trigger" aria-haspopup="true" aria-expanded="false" title="${escapeAttr(t('actions'))}">
                                        <i data-lucide="more-horizontal"></i>
                                    </button>
                                    <div class="mods-actions-dropdown advanced-maintenance-dropdown" hidden>
                                        <button type="button" class="mods-actions-item" data-action="backup-ini">
                                            <i data-lucide="copy"></i><span>${t('backup_inis')}</span>
                                        </button>
                                        <button type="button" class="mods-actions-item" data-action="backup-mods">
                                            <i data-lucide="archive"></i><span>${t('backup_mods')}</span>
                                        </button>
                                        <button type="button" class="mods-actions-item" data-action="backup-configs">
                                            <i data-lucide="file-text"></i><span>${t('backup_configs')}</span>
                                        </button>
                                        <button type="button" class="mods-actions-item" data-action="transfer-to-other">
                                            <i data-lucide="arrow-right-left"></i><span>${t('transfer_mods_to_other')}</span>
                                        </button>
                                        <button type="button" class="mods-actions-item" data-action="transfer-from-other">
                                            <i data-lucide="arrow-right-left"></i><span>${t('transfer_mods_from_other')}</span>
                                        </button>
                                        <button type="button" class="mods-actions-item" data-action="reset-config">
                                            <i data-lucide="history"></i><span>${t('reset_config')}</span>
                                        </button>
                                        <button type="button" class="mods-actions-item danger" data-action="delete-all-backups">
                                            <i data-lucide="trash-2"></i><span>${t('delete_all_backups')}</span>
                                        </button>
                                        <button type="button" class="mods-actions-item danger" data-action="clear-cache">
                                            <i data-lucide="trash-2"></i><span>${t('clear_cache')}</span>
                                        </button>
                                    </div>
                                </div>
                            </div>
                        </div>
                        <div id="backups-toolbar">${this.renderBackupsToolbar()}</div>
                    </div>
                    <div id="backups-list" class="backups-table-wrap">${this.renderBackupsTable()}</div>
                </div>
            </div>`;

        const renderKeybindsPanel = () => {
            const bindings = this._keybindDraft || getKeybinds(ms);
            const rows = KEYBIND_DEFINITIONS.map(def => `
                <li class="keybind-row">
                    <span>${t(def.labelKey)}</span>
                    <button type="button" class="keybind-assign-btn" data-keybind-id="${def.id}" title="${t('keybind_click_to_change')}">
                        <kbd class="keybind-chord-display">${bindings[def.id]}</kbd>
                    </button>
                </li>
            `).join('');
            return `
            <div class="settings-card-inner settings-keybinds-block">
                <ul class="keybind-list">${rows}</ul>
                <div class="keybind-actions-footer">
                    <button type="button" class="btn secondary btn-slim" id="btn-reset-keybinds">
                        <i data-lucide="rotate-ccw"></i>
                        <span>${t('keybind_reset_defaults')}</span>
                    </button>
                </div>
            </div>`;
        };

        const activePanel = this.activeTab === 'paths'
            ? renderPathsPanel()
            : this.activeTab === 'preferences'
                ? renderPreferencesPanel()
                : this.activeTab === 'keybinds'
                    ? renderKeybindsPanel()
                    : renderAdvancedPanel();

        const contentTitles = {
            paths: t('install_paths'),
            preferences: t('manager_preferences'),
            keybinds: t('shortcuts_hint_title'),
            advanced: t('advanced_maintenance')
        };
        const contentDescs = {
            paths: t('settings_paths_desc'),
            preferences: t('manager_settings_desc'),
            keybinds: t('shortcuts_settings_desc'),
            advanced: t('settings_advanced_desc')
        };
        const contentTitle = contentTitles[this.activeTab] || '';
        const contentDesc = contentDescs[this.activeTab] || '';
        const appVersion = data && data.appVersion ? data.appVersion : null;

        return `
            <div class="settings-page animate-fade">
                <div class="settings-header-bar">
                    <div class="settings-lang-wrap">
                        <label for="language-select">${t('ui_language_label')}</label>
                        <select class="settings-select" id="language-select">
                            <option value="en-US" ${ms.language === 'en-US' ? 'selected' : ''}>English</option>
                            <option value="fr-FR" ${ms.language === 'fr-FR' ? 'selected' : ''}>Français</option>
                            <option value="de-DE" ${ms.language === 'de-DE' ? 'selected' : ''}>Deutsch</option>
                            <option value="es-ES" ${ms.language === 'es-ES' ? 'selected' : ''}>Español</option>
                            <option value="it-IT" ${ms.language === 'it-IT' ? 'selected' : ''}>Italiano</option>
                            <option value="pl-PL" ${ms.language === 'pl-PL' ? 'selected' : ''}>Polski</option>
                            <option value="ru-RU" ${ms.language === 'ru-RU' ? 'selected' : ''}>Русский</option>
                            <option value="zh-CN" ${ms.language === 'zh-CN' ? 'selected' : ''}>简体中文</option>
                            <option value="zh-TW" ${ms.language === 'zh-TW' ? 'selected' : ''}>繁體中文</option>
                            <option value="ja-JP" ${ms.language === 'ja-JP' ? 'selected' : ''}>日本語</option>
                            <option value="ko-KR" ${ms.language === 'ko-KR' ? 'selected' : ''}>한국어</option>
                            <option value="pt-BR" ${ms.language === 'pt-BR' ? 'selected' : ''}>Português (Brasil)</option>
                        </select>
                    </div>
                    <div class="settings-lang-wrap settings-theme-picker">
                        <label for="ui-theme-select">${t('ui_theme_label')}</label>
                        <select class="settings-select" id="ui-theme-select">
                            ${getThemeOptionsForSettings(ms).map((th) => {
                                const label = th.isUser
                                    ? escapeHtml(th.displayName || th.id)
                                    : escapeHtml(t(th.labelKey));
                                return `<option value="${escapeAttr(th.id)}" ${th.selected ? 'selected' : ''}>${label}</option>`;
                            }).join('')}
                        </select>
                    </div>
                    <div class="mods-actions-menu-wrap settings-theme-actions-wrap">
                        <button type="button" class="btn-secondary mods-actions-trigger" id="btn-settings-theme-actions" aria-haspopup="true" aria-expanded="false" title="${escapeAttr(t('actions'))}">
                            <i data-lucide="more-horizontal"></i>
                        </button>
                        <div class="mods-actions-dropdown" id="settings-theme-actions-dropdown" hidden>
                            <button type="button" class="mods-actions-item" id="btn-import-theme" title="${escapeAttr(t('theme_import_hint'))}">
                                <i data-lucide="download"></i>
                                <span>${escapeHtml(t('theme_import'))}</span>
                            </button>
                            <button type="button" class="mods-actions-item" id="btn-open-themes-folder" title="${escapeAttr(t('theme_open_folder_hint'))}">
                                <i data-lucide="folder-open"></i>
                                <span>${escapeHtml(t('theme_open_folder'))}</span>
                            </button>
                            <button type="button" class="mods-actions-item" id="btn-reload-themes" title="${escapeAttr(t('theme_reload_hint'))}">
                                <i data-lucide="refresh-cw"></i>
                                <span>${escapeHtml(t('theme_reload'))}</span>
                            </button>
                        </div>
                    </div>
                </div>

                <div class="settings-card-wide">
                    <div class="settings-inner">
                        <nav class="settings-sidebar">
                            <button class="settings-tab-btn ${this.activeTab === 'preferences' ? 'active' : ''}" data-settings-tab="preferences">
                                <i data-lucide="sliders-horizontal"></i>
                                <span>${t('manager_preferences')}</span>
                            </button>
                            <button class="settings-tab-btn ${this.activeTab === 'paths' ? 'active' : ''}" data-settings-tab="paths">
                                <i data-lucide="map-pin"></i>
                                <span>${t('install_paths')}</span>
                            </button>
                            <button class="settings-tab-btn ${this.activeTab === 'keybinds' ? 'active' : ''}" data-settings-tab="keybinds">
                                <i data-lucide="keyboard"></i>
                                <span>${t('shortcuts_hint_title')}</span>
                            </button>
                            <button class="settings-tab-btn ${this.activeTab === 'advanced' ? 'active' : ''}" data-settings-tab="advanced">
                                <i data-lucide="settings"></i>
                                <span>${t('advanced_maintenance')}</span>
                            </button>
                        </nav>
                        <div class="settings-inner-content${this.activeTab === 'advanced' ? ' settings-tab-advanced' : ''}">
                            <header class="settings-content-header">
                                <h2 class="settings-content-title">${contentTitle}</h2>
                                <p class="settings-content-desc text-muted">${contentDesc}</p>
                            </header>
                            <div class="settings-panel-card">
                                ${activePanel}
                            </div>
                        </div>
                    </div>
                </div>

                <div class="settings-footer">
                    <div class="app-info">
                        ${appVersion ? `<span class="version-text">v${appVersion}</span>` : ''}
                        <span class="author-text">${t('created_for_wastelanders')}</span>
                    </div>
                    <button class="btn primary" id="save-settings" style="padding: 8px 24px;">
                        <i data-lucide="save"></i>
                        <span>${t('save_changes')}</span>
                    </button>
                </div>
            </div>
        `;
    }

    updateValues(data) {
        if (window.app?.currentSection !== 'settings') return;
        if (typeof window.app.replaceCurrentSectionContent === 'function') {
            window.app.replaceCurrentSectionContent();
        }
    }

    onMount() {
        document.querySelectorAll('[data-settings-tab]').forEach(btn => {
            btn.addEventListener('click', (e) => {
                const target = e.currentTarget;
                const tab = target && target.getAttribute('data-settings-tab');
                if (!tab) return;
                e.preventDefault();
                e.stopPropagation();
                if (tab !== 'advanced') this.closeBackupPreviewModal(true);
                this.activeTab = tab;
                if (window.app && typeof window.app.replaceCurrentSectionContent === 'function') {
                    window.app.replaceCurrentSectionContent();
                }
            });
        });

        document.querySelectorAll('.browse-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const target = btn.getAttribute('data-target') || '';
                const picker = btn.getAttribute('data-picker') || 'folder';
                const path = this.getCurrentPathForTarget(target);
                if (picker === 'file') {
                    window.chrome.webview.postMessage({ type: 'BROWSE_EXECUTABLE', target, path });
                } else {
                    window.chrome.webview.postMessage({ type: 'BROWSE_FOLDER', target, path });
                }
            });
        });

        document.getElementById('ui-theme-select')?.addEventListener('change', (e) => {
            applyUiTheme(e.target?.value);
        });

        this._mountSettingsThemeActionsMenu();

        document.getElementById('save-settings')?.addEventListener('click', () => {
            const settings = {
                language: document.getElementById('language-select')?.value ?? 'en-US',
                uiTheme: document.getElementById('ui-theme-select')?.value ?? DEFAULT_UI_THEME
            };
            Settings.PATHS.forEach(p => { const el = document.getElementById(p.id); if (el) settings[p.key] = el.value; });
            Settings.TOGGLES.forEach(p => { const el = document.getElementById(p.id); if (el) settings[p.key] = el.checked; });
            settings.archiveKeyName = document.getElementById('archive-key-select')?.value ?? 'auto';
            settings.keybinds = this._keybindDraft || getKeybinds(window.app?.realData?.managerSettings);
            saveKeybindsLocal(settings.keybinds);
            window.chrome.webview.postMessage({ type: 'SAVE_MANAGER_SETTINGS', settings });
        });

        this.mountKeybindEditors();

        if (this.activeTab === 'advanced') {
            this.mountAdvancedPanelHandlers();
        }

        document.getElementById('nexus-login-btn')?.addEventListener('click', () => {
            window.chrome.webview.postMessage({ type: 'NEXUS_LOGIN' });
        });

        document.getElementById('nexus-logout-btn')?.addEventListener('click', () => {
            localStorage.removeItem('nexusBannerDismissed');
            window.chrome.webview.postMessage({ type: 'NEXUS_LOGOUT' });
        });

    }

    mountKeybindEditors() {
        if (this._keybindCaptureAbort) {
            this._keybindCaptureAbort.abort();
            this._keybindCaptureAbort = null;
        }
        window.__keybindCapturing = false;

        const panel = document.querySelector('.settings-keybinds-block');
        if (!panel) return;

        if (!this._keybindDraft) {
            this._keybindDraft = getKeybinds(window.app?.realData?.managerSettings);
        }

        this._keybindCaptureAbort = new AbortController();
        const { signal } = this._keybindCaptureAbort;

        const stopCapture = () => {
            window.__keybindCapturing = false;
            panel.querySelectorAll('.keybind-assign-btn').forEach(btn => {
                btn.classList.remove('listening');
                const id = btn.getAttribute('data-keybind-id');
                const chord = this._keybindDraft?.[id];
                const kbd = btn.querySelector('.keybind-chord-display');
                if (kbd && chord) kbd.textContent = chord;
            });
        };

        panel.querySelectorAll('.keybind-assign-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                window.__keybindCapturing = true;
                panel.querySelectorAll('.keybind-assign-btn').forEach(b => b.classList.remove('listening'));
                btn.classList.add('listening');
                const kbd = btn.querySelector('.keybind-chord-display');
                if (kbd) kbd.textContent = window._t('keybind_press_keys');
            }, { signal });
        });

        const onCaptureKeydown = (e) => {
            if (!window.__keybindCapturing) return;
            e.preventDefault();
            e.stopPropagation();

            if (e.key === 'Escape') {
                stopCapture();
                return;
            }

            const chord = formatChordFromEvent(e);
            if (!chord) return;

            const activeBtn = panel.querySelector('.keybind-assign-btn.listening');
            if (!activeBtn) return;
            const id = activeBtn.getAttribute('data-keybind-id');
            if (!id) return;

            const conflict = findKeybindConflict(this._keybindDraft, id, chord);
            if (conflict) {
                window.app?.showBanner?.({
                    type: 'warning',
                    text: window._t('keybind_conflict', chord, window._t(KEYBIND_DEFINITIONS.find(d => d.id === conflict)?.labelKey || conflict))
                });
                return;
            }

            this._keybindDraft = { ...this._keybindDraft, [id]: chord };
            saveKeybindsLocal(this._keybindDraft);
            stopCapture();
        };

        document.addEventListener('keydown', onCaptureKeydown, { signal, capture: true });

        document.getElementById('btn-reset-keybinds')?.addEventListener('click', () => {
            this._keybindDraft = { ...DEFAULT_KEYBINDS };
            saveKeybindsLocal(this._keybindDraft);
            if (window.app?.replaceCurrentSectionContent) {
                window.app.replaceCurrentSectionContent();
            }
        }, { signal });
    }

    getCurrentPathForTarget(target) {
        const pathInfo = Settings.PATHS.find(p => p.target === target);
        if (!pathInfo) return '';
        const input = document.getElementById(pathInfo.id);
        return input ? input.value : '';
    }

    postToHost(type, extra = {}) {
        if (!window.chrome?.webview?.postMessage) {
            window.app?.showBanner?.({ type: 'error', text: window._t('action_unavailable') });
            return false;
        }
        window.chrome.webview.postMessage({ type, ...extra });
        return true;
    }

    handleTransferMods(direction) {
        return window.transferModsAcrossPlatforms?.(direction);
    }

    mountAdvancedPanelHandlers() {
        const panel = document.getElementById('settings-advanced-panel');
        if (!panel) return;

        panel.addEventListener('click', (e) => {
            const actionEl = e.target.closest('[data-action]');
            if (actionEl) {
                this.closeAdvancedMaintenanceMenu();
                const action = actionEl.getAttribute('data-action');
                switch (action) {
                    case 'backup-ini':
                        this.postToHost('BACKUP_INI');
                        break;
                    case 'backup-mods':
                        this.postToHost('BACKUP_MODS');
                        break;
                    case 'backup-configs':
                        this.postToHost('BACKUP_CONFIGS');
                        break;
                    case 'delete-all-backups':
                        void this.handleDeleteAllBackupsClick();
                        break;
                    case 'clear-cache':
                        this.postToHost('CLEAR_CACHE');
                        break;
                    case 'transfer-to-other':
                        void this.handleTransferMods('to-other');
                        break;
                    case 'transfer-from-other':
                        void this.handleTransferMods('from-other');
                        break;
                    default:
                        break;
                }
                return;
            }

            if (e.target.closest('#btn-refresh-backups')) {
                this.requestBackupsList();
                return;
            }

            if (e.target.closest('[data-action="reset-config"]')) {
                this.postToHost('RESET_CONFIG');
            }
        });

        this.mountAdvancedMaintenanceMenu();
        this.mountBackupHandlers();
        this.requestBackupsList();
    }

    getFilteredBackups() {
        const all = this.backups || [];
        let list = all;
        if (this.backupListFilter) {
            list = list.filter(b => b.type === this.backupListFilter);
        }
        const q = (this.backupSearchQuery || '').trim().toLowerCase();
        if (q) {
            list = list.filter(b => {
                const hay = `${b.label || ''} ${b.type || ''} ${b.created || ''}`.toLowerCase();
                return hay.includes(q);
            });
        }
        return list;
    }

    backupTypeLabel(type) {
        const key = `backup_type_${type}`;
        const translated = window._t(key);
        return translated !== key ? translated : type;
    }

    formatBackupDate(created) {
        if (!created) return '—';
        const d = new Date(created);
        if (Number.isNaN(d.getTime())) return created.replace('Z', '').slice(0, 19);
        return d.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' });
    }

    formatBackupLabel(b) {
        const label = b.label || '';
        if (b.type === 'mods') {
            return label.replace(/^ModsBackup_/i, '').replace(/\.zip$/i, '');
        }
        if (label.includes('/')) return label.split('/').pop();
        return label;
    }

    renderBackupsToolbar() {
        const total = (this.backups || []).length;
        const shown = this.getFilteredBackups().length;
        const countText = total === 0
            ? ''
            : window._t('backups_showing_count', shown, total);
        const filters = [
            { id: null, key: 'backups_filter_all' },
            { id: 'mods', key: 'backup_type_mods' },
            { id: 'configs', key: 'backup_type_configs' },
            { id: 'ini', key: 'backup_type_ini' }
        ];
        const chips = filters.map(f => {
            const active = this.backupListFilter === f.id;
            return `<button type="button" class="backups-filter-chip${active ? ' is-active' : ''}" data-filter="${f.id ?? ''}">${window._t(f.key)}</button>`;
        }).join('');
        return `
            <div class="backups-toolbar">
                <input type="search" id="backups-search" class="backups-search" placeholder="${escapeAttr(window._t('backups_search_placeholder'))}" value="${escapeAttr(this.backupSearchQuery)}" autocomplete="off" />
                <div class="backups-filter-chips" role="group" aria-label="${escapeAttr(window._t('backups_filter_label'))}">${chips}</div>
                <span class="backups-count text-muted">${escapeHtml(countText)}</span>
            </div>
        `;
    }

    renderBackupsTable() {
        const filtered = this.getFilteredBackups();
        if (!this.backups || this.backups.length === 0) {
            return `<div class="backups-empty text-muted">${window._t('backups_empty')}</div>`;
        }
        if (filtered.length === 0) {
            const emptyKey = this.backupListFilter === 'mods' ? 'no_mods_backups_found' : 'backups_no_matches';
            return `<div class="backups-empty text-muted">${window._t(emptyKey)}</div>`;
        }
        const rows = filtered.map(b => {
            const selected = this.backupPreviewId === b.id;
            return `
                <tr class="backup-row${selected ? ' is-selected' : ''}" data-backup-id="${escapeAttr(b.id)}" data-backup-type="${escapeAttr(b.type)}">
                    <td><span class="backup-type-badge">${escapeHtml(this.backupTypeLabel(b.type))}</span></td>
                    <td class="backup-name-cell" title="${escapeAttr(b.label)}">${escapeHtml(this.formatBackupLabel(b))}</td>
                    <td class="backup-date-cell text-muted">${escapeHtml(this.formatBackupDate(b.created))}</td>
                    <td class="backup-files-cell text-muted">${b.fileCount}</td>
                    <td class="backup-actions-cell">
                        <button type="button" class="btn-icon btn-restore-backup" data-id="${escapeAttr(b.id)}" data-backup-type="${escapeAttr(b.type)}" title="${escapeAttr(window._t('restore'))}">
                            <i data-lucide="rotate-ccw"></i>
                        </button>
                        <div class="mods-actions-menu-wrap backup-actions-menu-wrap">
                            <button type="button" class="btn-icon backup-actions-trigger" aria-haspopup="true" title="${escapeAttr(window._t('actions'))}">
                                <i data-lucide="more-horizontal"></i>
                            </button>
                            <div class="mods-actions-dropdown" hidden>
                                <button type="button" class="mods-actions-item btn-preview-backup" data-id="${escapeAttr(b.id)}">
                                    <i data-lucide="eye"></i><span>${window._t('preview')}</span>
                                </button>
                                <button type="button" class="mods-actions-item danger btn-delete-backup" data-id="${escapeAttr(b.id)}">
                                    <i data-lucide="trash-2"></i><span>${window._t('delete')}</span>
                                </button>
                            </div>
                        </div>
                    </td>
                </tr>
            `;
        }).join('');
        return `
            <table class="backups-table">
                <thead>
                    <tr>
                        <th>${window._t('backup_col_type')}</th>
                        <th>${window._t('backup_col_name')}</th>
                        <th>${window._t('backup_col_date')}</th>
                        <th>${window._t('backup_col_files')}</th>
                        <th class="backup-actions-col"></th>
                    </tr>
                </thead>
                <tbody>${rows}</tbody>
            </table>
        `;
    }

    renderBackupPreviewModalBody() {
        if (!this.backupPreviewId) return '';
        const backup = (this.backups || []).find(b => b.id === this.backupPreviewId);
        const title = backup ? this.formatBackupLabel(backup) : this.backupPreviewId;
        const typeLabel = backup ? this.backupTypeLabel(backup.type) : '';
        if (this.backupPreviewLoading) {
            return `<p class="text-muted backup-preview-modal-loading">${window._t('backup_detail_loading')}</p>`;
        }
        const files = Array.isArray(this.backupPreview) ? this.backupPreview : [];
        return `
            <p class="text-muted backup-preview-modal-meta">${escapeHtml(typeLabel)} · ${files.length} ${window._t('backup_col_files').toLowerCase()}</p>
            <div class="backup-preview-files backup-preview-modal-files">
                ${files.length === 0
                    ? `<span class="text-muted">${window._t('backup_detail_no_files')}</span>`
                    : files.map(f => `<div>${escapeHtml(f)}</div>`).join('')}
            </div>
        `;
    }

    renderBackupPreviewModalShell() {
        const backup = (this.backups || []).find(b => b.id === this.backupPreviewId);
        const title = backup ? this.formatBackupLabel(backup) : (this.backupPreviewId || window._t('backup_preview_title'));
        return `
            <div class="custom-modal polished backup-preview-modal" role="dialog" aria-labelledby="backup-preview-modal-title">
                <div class="modal-header backup-preview-modal-header">
                    <div>
                        <h3 id="backup-preview-modal-title">${escapeHtml(title)}</h3>
                        <p class="text-muted backup-preview-modal-sub">${window._t('backup_preview_title')}</p>
                    </div>
                    <button type="button" class="close-status backup-preview-modal-close" aria-label="${escapeAttr(window._t('backup_detail_close'))}">&times;</button>
                </div>
                <div class="modal-body backup-preview-modal-body" id="backup-preview-modal-body">
                    ${this.renderBackupPreviewModalBody()}
                </div>
            </div>
        `;
    }

    openBackupPreviewModal() {
        this.closeBackupPreviewModal(false);
        const overlay = document.createElement('div');
        overlay.id = 'backup-preview-overlay';
        overlay.className = 'modal-overlay active backup-preview-overlay';
        overlay.innerHTML = this.renderBackupPreviewModalShell();
        document.body.appendChild(overlay);
        this._mountBackupPreviewModalHandlers(overlay);
        if (window.lucide) {
            try {
                window.lucide.createIcons({ nodes: [overlay] });
            } catch {
                window.lucide.createIcons();
            }
        }
    }

    refreshBackupPreviewModal() {
        const body = document.getElementById('backup-preview-modal-body');
        const overlay = document.getElementById('backup-preview-overlay');
        if (!overlay || !this.backupPreviewId) return;
        if (body) body.innerHTML = this.renderBackupPreviewModalBody();
        const titleEl = document.getElementById('backup-preview-modal-title');
        const backup = (this.backups || []).find(b => b.id === this.backupPreviewId);
        if (titleEl && backup) titleEl.textContent = this.formatBackupLabel(backup);
        if (window.lucide && body) {
            try {
                window.lucide.createIcons({ nodes: [overlay] });
            } catch {
                window.lucide.createIcons();
            }
        }
    }

    closeBackupPreviewModal(clearState = true) {
        document.getElementById('backup-preview-overlay')?.remove();
        if (clearState) {
            this.backupPreviewId = null;
            this.backupPreview = null;
            this.backupPreviewLoading = false;
            document.querySelectorAll('.backup-row.is-selected').forEach(r => r.classList.remove('is-selected'));
        }
    }

    _mountBackupPreviewModalHandlers(overlay) {
        const close = () => this.closeBackupPreviewModal(true);
        overlay.querySelector('.backup-preview-modal-close')?.addEventListener('click', close);
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) close();
        });
        if (!this._backupPreviewEscapeBound) {
            this._backupPreviewEscapeBound = true;
            document.addEventListener('keydown', (e) => {
                if (e.key === 'Escape' && document.getElementById('backup-preview-overlay')) {
                    this.closeBackupPreviewModal(true);
                }
            });
        }
    }

    _closeSettingsThemeActionsMenu() {
        const dropdown = document.getElementById('settings-theme-actions-dropdown');
        const trigger = document.getElementById('btn-settings-theme-actions');
        if (dropdown) dropdown.hidden = true;
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
    }

    _mountSettingsThemeActionsMenu() {
        const trigger = document.getElementById('btn-settings-theme-actions');
        const dropdown = document.getElementById('settings-theme-actions-dropdown');
        if (!trigger || !dropdown) return;

        if (this._settingsThemeActionsMenuAbort) {
            this._settingsThemeActionsMenuAbort.abort();
        }
        this._settingsThemeActionsMenuAbort = new AbortController();
        const { signal } = this._settingsThemeActionsMenuAbort;

        dropdown.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');

        trigger.addEventListener('click', (e) => {
            e.stopPropagation();
            const open = dropdown.hidden;
            dropdown.hidden = !open;
            trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
            if (open && window.lucide) window.lucide.createIcons();
        }, { signal });

        document.addEventListener('click', (e) => {
            if (!document.getElementById('btn-settings-theme-actions')) return;
            if (!e.target.closest('.settings-theme-actions-wrap')) {
                this._closeSettingsThemeActionsMenu();
            }
        }, { signal });

        document.getElementById('btn-import-theme')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeSettingsThemeActionsMenu();
            window.chrome?.webview?.postMessage({ type: 'IMPORT_USER_THEME' });
        }, { signal });

        document.getElementById('btn-open-themes-folder')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeSettingsThemeActionsMenu();
            window.chrome?.webview?.postMessage({ type: 'OPEN_THEMES_FOLDER' });
        }, { signal });

        document.getElementById('btn-reload-themes')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeSettingsThemeActionsMenu();
            window.chrome?.webview?.postMessage({ type: 'RELOAD_USER_THEMES' });
        }, { signal });
    }

    mountAdvancedMaintenanceMenu() {
        const trigger = document.querySelector('.advanced-maintenance-actions-trigger');
        const dropdown = document.querySelector('.advanced-maintenance-dropdown');
        if (!trigger || !dropdown) return;

        trigger.onclick = (e) => {
            e.stopPropagation();
            const open = dropdown.hidden;
            this.closeAdvancedMaintenanceMenu();
            this.closeBackupActionMenus();
            dropdown.hidden = !open;
            trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
            if (open && window.lucide) window.lucide.createIcons();
        };

        if (!this._advancedMenuCloseBound) {
            this._advancedMenuCloseBound = true;
            document.addEventListener('click', (e) => {
                if (!e.target.closest('.advanced-maintenance-menu-wrap')) {
                    this.closeAdvancedMaintenanceMenu();
                }
            });
        }
    }

    closeAdvancedMaintenanceMenu() {
        const dropdown = document.querySelector('.advanced-maintenance-dropdown');
        const trigger = document.querySelector('.advanced-maintenance-actions-trigger');
        if (dropdown) dropdown.hidden = true;
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
    }

    updateBackupsListUI() {
        const toolbar = document.getElementById('backups-toolbar');
        const list = document.getElementById('backups-list');
        if (toolbar) toolbar.innerHTML = this.renderBackupsToolbar();
        if (list) list.innerHTML = this.renderBackupsTable();
        if (this.backupPreviewId && document.getElementById('backup-preview-overlay')) {
            this.refreshBackupPreviewModal();
        }
        this.mountBackupHandlers();
        this.mountAdvancedMaintenanceMenu();
        if (window.lucide) window.lucide.createIcons();
    }

    toggleBackupPreview(id) {
        if (!id) return;
        if (this.backupPreviewId === id && document.getElementById('backup-preview-overlay')) {
            this.closeBackupPreviewModal(true);
            return;
        }
        this.selectBackupForPreview(id);
    }

    selectBackupForPreview(id) {
        if (!id) return;
        this.backupPreviewId = id;
        this.backupPreview = null;
        this.backupPreviewLoading = true;
        this.openBackupPreviewModal();
        const list = document.getElementById('backups-list');
        list?.querySelectorAll('.backup-row.is-selected').forEach(r => r.classList.remove('is-selected'));
        list?.querySelector(`[data-backup-id="${CSS.escape(id)}"]`)?.classList.add('is-selected');
        this.postToHost('PREVIEW_BACKUP', { id });
    }

    clearBackupSelection() {
        this.closeBackupPreviewModal(true);
    }

    positionBackupActionsMenu(trigger, menu) {
        if (!trigger || !menu) return;
        menu.classList.add('backup-actions-dropdown--fixed');
        menu.hidden = false;
        const prevVis = menu.style.visibility;
        menu.style.visibility = 'hidden';
        const menuW = menu.offsetWidth || 172;
        const menuH = menu.offsetHeight || 88;
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

    closeBackupActionMenus() {
        document.querySelectorAll('.backups-table-wrap .mods-actions-dropdown').forEach(d => {
            d.hidden = true;
            d.classList.remove('backup-actions-dropdown--fixed');
            d.style.top = '';
            d.style.left = '';
        });
    }

    async handleDeleteAllBackupsClick() {
        const count = (this.backups || []).length;
        if (count === 0) {
            window.app?.showBanner?.({ type: 'warning', text: window._t('delete_all_backups_none') });
            return;
        }
        const ok = await window.appConfirm({
            title: window._t('delete_all_backups'),
            message: window._t('delete_all_backups_confirm', count),
            okText: window._t('delete_all_backups'),
            cancelText: window._t('cancel'),
            danger: true
        });
        if (!ok) return;
        this.closeBackupPreviewModal(true);
        this.postToHost('DELETE_ALL_BACKUPS');
    }

    requestBackupsList() {
        window.chrome?.webview?.postMessage({ type: 'LIST_BACKUPS' });
    }

    handleBackupsList(payload) {
        this.backups = Array.isArray(payload?.backups) ? payload.backups : [];
        this.updateBackupsListUI();
    }

    handleBackupPreview(payload) {
        if (payload?.error) {
            this.backupPreviewLoading = false;
            window.app?.showBanner?.({ type: 'error', text: payload.error });
            this.refreshBackupPreviewModal();
            return;
        }
        this.backupPreviewId = payload.id;
        this.backupPreview = payload.files || [];
        this.backupPreviewLoading = false;
        if (!document.getElementById('backup-preview-overlay')) {
            this.openBackupPreviewModal();
            const list = document.getElementById('backups-list');
            list?.querySelectorAll('.backup-row.is-selected').forEach(r => r.classList.remove('is-selected'));
            list?.querySelector(`[data-backup-id="${CSS.escape(payload.id)}"]`)?.classList.add('is-selected');
        } else {
            this.refreshBackupPreviewModal();
        }
    }

    mountBackupHandlers() {
        const search = document.getElementById('backups-search');
        if (search && !search.dataset.bound) {
            search.dataset.bound = '1';
            search.addEventListener('input', () => {
                this.backupSearchQuery = search.value;
                const list = document.getElementById('backups-list');
                const toolbar = document.getElementById('backups-toolbar');
                if (list) list.innerHTML = this.renderBackupsTable();
                if (toolbar) {
                    const countEl = toolbar.querySelector('.backups-count');
                    if (countEl) {
                        const total = (this.backups || []).length;
                        const shown = this.getFilteredBackups().length;
                        countEl.textContent = total === 0 ? '' : window._t('backups_showing_count', shown, total);
                    }
                }
                this.mountBackupHandlers();
                if (window.lucide) window.lucide.createIcons();
            });
        }

        document.querySelectorAll('.backups-filter-chip').forEach(chip => {
            chip.onclick = () => {
                const raw = chip.getAttribute('data-filter');
                this.backupListFilter = raw ? raw : null;
                this.updateBackupsListUI();
            };
        });

        const listWrap = document.getElementById('backups-list');
        if (listWrap && !listWrap.dataset.rowClickBound) {
            listWrap.dataset.rowClickBound = '1';
            listWrap.addEventListener('scroll', () => this.closeBackupActionMenus(), { passive: true });
            listWrap.addEventListener('click', (e) => {
                if (e.target.closest('button, a, .mods-actions-menu-wrap')) return;
                const row = e.target.closest('tr.backup-row');
                if (!row) return;
                const id = row.getAttribute('data-backup-id');
                if (id) this.toggleBackupPreview(id);
            });
        }

        document.querySelectorAll('.backup-actions-trigger').forEach(btn => {
            btn.onclick = (e) => {
                e.stopPropagation();
                const wrap = btn.closest('.backup-actions-menu-wrap');
                const menu = wrap?.querySelector('.mods-actions-dropdown');
                if (!menu) return;
                const willOpen = menu.hidden;
                this.closeBackupActionMenus();
                if (willOpen) this.positionBackupActionsMenu(btn, menu);
            };
        });

        if (!this._backupMenuCloseBound) {
            this._backupMenuCloseBound = true;
            document.addEventListener('click', (e) => {
                if (!e.target.closest('.backup-actions-menu-wrap')) {
                    this.closeBackupActionMenus();
                }
            });
        }

        document.querySelectorAll('.btn-preview-backup').forEach(btn => {
            btn.onclick = (e) => {
                e.stopPropagation();
                this.closeBackupActionMenus();
                const id = btn.getAttribute('data-id');
                if (id) this.toggleBackupPreview(id);
            };
        });
        document.querySelectorAll('.btn-delete-backup').forEach(btn => {
            btn.onclick = async (e) => {
                e.stopPropagation();
                this.closeBackupActionMenus();
                const id = btn.getAttribute('data-id');
                if (!id) return;
                const ok = await window.appConfirm({
                    title: window._t('delete_backup'),
                    message: window._t('delete_backup_confirm'),
                    okText: window._t('delete'),
                    cancelText: window._t('cancel'),
                    danger: true
                });
                if (!ok) return;
                if (this.backupPreviewId === id) this.clearBackupSelection();
                this.postToHost('DELETE_BACKUP', { id });
            };
        });
        document.querySelectorAll('.btn-restore-backup').forEach(btn => {
            btn.onclick = async (e) => {
                e.stopPropagation();
                const id = btn.getAttribute('data-id');
                if (!id) return;
                const backupType = btn.getAttribute('data-backup-type');
                const isMods = backupType === 'mods' || id.startsWith('mods:');
                const ok = await window.appConfirm({
                    title: isMods ? window._t('restore_mods') : window._t('restore'),
                    message: isMods ? window._t('backup_restore_mods_confirm') : window._t('backup_restore_confirm'),
                    okText: window._t('restore'),
                    cancelText: window._t('cancel'),
                    danger: true
                });
                if (ok) this.postToHost('RESTORE_BACKUP', { id });
            };
        });
    }
}
