import { Dashboard } from './components/Dashboard.js';
import { ModsManager } from './components/ModsManager.js';
import { ConfigManager } from './components/ConfigManager.js';
import { TweaksManager } from './components/TweaksManager.js';
import { PipBoy } from './components/PipBoy.js';
import { Profiles } from './components/Profiles.js';
import { Logs } from './components/Logs.js';
import { Settings } from './components/Settings.js';
import { UpdateManager } from './components/UpdateManager.js';
import { ConflictModal } from './components/ConflictModal.js';
import { EndorsementModal } from './components/EndorsementModal.js';
import { ModPresetsManager } from './components/ModGroupsManager.js';
import { BundleManager } from './components/BundleManager.js';
import { IniEditorModal } from './components/IniEditorModal.js';
import { escapeAttr, escapeHtml } from './utils/htmlSafe.js';
import { applyStylesheet as applyModTypeBadgeStylesheet } from './utils/modTypeBadgeTheme.js';
import {
    getKeybinds,
    eventMatchesChord,
    executeKeybindAction,
    syncKeybindsFromManagerSettings
} from './utils/keybinds.js';

import { translator } from './Translations.js';
import { applyUiTheme, registerUserThemesFromHost, unregisterUserTheme, updateThemePreview, endThemePreview } from './utils/themeManager.js';
import { DEFAULT_UI_THEME } from './themes/registry.js';
import { refreshSharpLogos } from './utils/logoSharpen.js';
import {
    isPreviewPlaceholderSection,
    mountPreviewPlaceholder,
} from './utils/previewPlaceholders.js';
import { appConfirm, appPrompt, dismissAppConfirm, installAppConfirmGlobals } from './utils/appConfirm.js';
import { installThemedSelectMenus } from './utils/themedSelectMenu.js';

window._t = (key, ...args) => translator.t(key, ...args);
window.translator = translator;
installAppConfirmGlobals();
installThemedSelectMenus();

export { appConfirm, appPrompt };

window.transferModsAcrossPlatforms = async (direction, scope = 'mods') => {
    const isConfigs = scope === 'configs';
    const current = window.app?.realData?.platform || 'current platform';
    const other = current === 'Steam' ? 'Xbox' : (current === 'Xbox' ? 'Steam' : 'the other platform');
    const from = direction === 'from-other' ? other : current;
    const to = direction === 'from-other' ? current : other;
    const ok = await appConfirm({
        title: window._t(isConfigs ? 'transfer_configs_title' : 'transfer_mods_title'),
        message: window._t(isConfigs ? 'transfer_configs_confirm' : 'transfer_mods_confirm', from, to),
        okText: window._t(isConfigs ? 'transfer_configs_ok' : 'transfer_mods_ok'),
        cancelText: window._t('cancel')
    });
    if (ok && window.chrome?.webview?.postMessage) {
        window.chrome.webview.postMessage({ type: 'TRANSFER_MODS_ACROSS_PLATFORMS', direction, overwrite: true });
    }
};

export class App {
    constructor() {
        this.sections = {};
        this.currentSection = 'dashboard';
        this.sidebar = null;
        this.contentArea = null;
        this.notificationContainer = null;
        this.realData = null;
        this.modGroups = new ModPresetsManager(this);
        this.conflictModal = new ConflictModal(this);
        this.endorsementModal = new EndorsementModal(this);
        this.iniEditor = new IniEditorModal(this);
        this.hasRenderedInitially = false;
        this._translationsReady = false;
        this._userChoseSection = false;
        this._pendingUpdateData = null;
        this._updateDataFlushHandle = null;
        this._downloadBannerDismissTimer = null;
        this._pendingPersistSection = null;
        this._navigatePersistTimer = null;
    }

    bootMark(label) {
        try {
            const t = Math.round(performance.now());
            if (t > 60000) return;
            const msg = `[BOOT] ${label} t=${t}ms`;
            console.log(msg);
            window.chrome?.webview?.postMessage({ type: 'JS_LOG', message: msg });
        } catch {  }
    }

    async init() {
        console.log("App initializing...");
        this.bootMark('App.init start');
        this.sidebar = document.getElementById('sidebar');
        this.contentArea = document.getElementById('content-area');
        this.notificationContainer = document.getElementById('notification-container');

        document.addEventListener('keydown', (e) => {
            if (
                e.key === 'F12' ||
                (e.ctrlKey && e.shiftKey && (e.key === 'I' || e.key === 'J' || e.key === 'C')) ||
                (e.ctrlKey && e.key === 'U')
            ) {
                e.preventDefault();
                return false;
            }
        });

        this.setupKeyboardShortcuts();

        window.addEventListener('beforeunload', () => {
            if (this._navigatePersistTimer) {
                clearTimeout(this._navigatePersistTimer);
                this._navigatePersistTimer = null;
            }
            if (this._pendingPersistSection && window.chrome?.webview) {
                window.chrome.webview.postMessage({
                    type: 'NAVIGATE_TO',
                    section: this._pendingPersistSection
                });
            }
        });

        if (window.chrome && window.chrome.webview && !window.chrome.webview.shim) {
        } else {
            window.addEventListener('dragover', (e) => {
                if (e.dataTransfer && e.dataTransfer.types && e.dataTransfer.types.includes('Files')) {
                    e.preventDefault();
                    e.dataTransfer.dropEffect = 'copy';
                }
            });

            window.addEventListener('drop', (e) => {
                if (e.dataTransfer && e.dataTransfer.types && e.dataTransfer.types.includes('Files')) {
                    if (this.currentSection !== 'mods') {
                        e.preventDefault();
                        return;
                    }
                    e.preventDefault();
                    const files = Array.from(e.dataTransfer.files).map(f => f.path).filter(p => p);
                    if (files.length > 0) {
                        window.chrome.webview.postMessage({
                            type: 'IMPORT_FILES',
                            files: files
                        });
                    }
                }
            });
        }

        if (!this.sidebar || !this.contentArea) {
            console.error("Critical UI elements not found!");
            return;
        }

        this.restoreSectionChrome();

        if (window.chrome && window.chrome.webview) {
            this._pendingMessages = [];
            this._ready = false;

            window.chrome.webview.addEventListener('message', (event) => {
                const data = event.data;

                if (data.type === 'LANGUAGE_CONTENT') {
                    translator.receiveLanguageContent(data.lang, data.content, data.error);
                    return;
                }

                if (data.type === 'LOGS_DATA') {
                    this._applyLogsData(data.logs);
                    return;
                }

                if (!this._ready) {
                    if (data.type === 'UPDATE_DATA') {
                        this._handleMessage(data);
                        return;
                    }
                    console.log(`[UI] Queuing early message: ${data.type || 'unknown'}`);
                    this._pendingMessages.push(data);
                    return;
                }

                this._handleMessage(data);
            });

            window.chrome.webview.postMessage({ type: 'GET_DATA' });

            this._ready = true;
            if (this._pendingMessages.length > 0) {
                console.log(`[UI] Replaying ${this._pendingMessages.length} queued message(s)...`);
                for (const msg of this._pendingMessages) {
                    this._handleMessage(msg);
                }
            }
            this._pendingMessages = [];

            translator.init().then(() => {
                this._translationsReady = true;
                this.bootMark('translator ready');
                this.translateStaticElements();
                this.finishInitialPaint();
            }).catch(e => {
                console.error("[Init] Translator failed:", e);
                this._translationsReady = true;
                this.finishInitialPaint();
            });

            setTimeout(() => {
                if (!this.realData) {
                    console.warn('[UI] No data received after 3s, retrying GET_DATA...');
                    window.chrome.webview.postMessage({ type: 'GET_DATA' });
                }
            }, 3000);
        } else {
            document.documentElement.classList.remove('app-booting');
            translator.init().then(() => {
                this._translationsReady = true;
                this.translateStaticElements();
                this.finishInitialPaint();
                if (!this.hasRenderedInitially) {
                    this.navigateTo(this.rememberedSection() || 'dashboard', true, false);
                    this.hasRenderedInitially = true;
                }
            }).catch(e => {
                console.error("[Init] Translator failed:", e);
                this._translationsReady = true;
                if (!this.hasRenderedInitially) {
                    this.navigateTo(this.rememberedSection() || 'dashboard', true, false);
                    this.hasRenderedInitially = true;
                }
            });
        }

        const kofiLink = document.getElementById('kofi-link');
        if (kofiLink) {
            kofiLink.addEventListener('click', () => {
                window.chrome?.webview?.postMessage({
                    type: 'OPEN_IN_BROWSER',
                    url: 'https://ko-fi.com/moddedwolf'
                });
            });
        }

        const platformToggle = document.getElementById('platform-toggle');
        if (platformToggle) {
            platformToggle.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'SWITCH_PLATFORM' });
            });
        }

        const profileDropdown = document.getElementById('profile-dropdown');
        if (profileDropdown) {
            profileDropdown.addEventListener('change', (e) => {
                window.chrome.webview.postMessage({
                    type: 'SWITCH_PROFILE',
                    name: e.target.value
                });
            });
        }

        document.addEventListener('app-refresh-ui', () => {
            this.refreshCurrentSection();
        });

        this.setupNavigation();
        this.setupEndorsementClick();

        if (window.__THEME_STUDIO_PREVIEW && !this.realData) {
            this.navigateTo(window.__THEME_STUDIO_ACTIVE_SECTION || this.currentSection || 'dashboard', true, false);
        }
    }

    _handleMessage(data) {
        if (data.type === 'THEME_IMPORT_RESULT') {
            if (data.ok) {
                const name = data.displayName || data.themeId || '';
                this.showBanner({
                    type: 'success',
                    text: window._t('theme_import_success', name),
                });
                if (this.currentSection === 'settings' && this.sections.settings) {
                    this.replaceCurrentSectionContent();
                }
            } else {
                this.showBanner({
                    type: 'error',
                    text: data.error || window._t('theme_import_failed'),
                });
            }
            return;
        }

        if (data.type === 'THEME_PREVIEW') {
            try {
                updateThemePreview(data.css || '', data.logoUrl || '', data.layout || null);
            } catch (e) {
                console.warn('[THEME] preview apply failed', e);
            }
            return;
        }

        if (data.type === 'THEME_PREVIEW_END') {
            try {
                endThemePreview({ restore: data.restore !== false });
            } catch (e) {
                console.warn('[THEME] preview end failed', e);
            }
            return;
        }

        if (data.type === 'THEME_CREATOR_APPLIED') {
            try {
                endThemePreview({ restore: false });
                const themeId = data.themeId;
                if (themeId) {
                    applyUiTheme(themeId);
                    if (this.realData?.managerSettings) this.realData.managerSettings.uiTheme = themeId;
                    this._lastHostUiTheme = themeId;
                }
                this.showBanner({
                    type: 'success',
                    text: window._t('theme_creator_save_success', data.displayName || themeId || ''),
                });
                if (this.currentSection === 'settings' && this.sections.settings) {
                    this.replaceCurrentSectionContent();
                }
            } catch (e) {
                console.warn('[THEME] creator applied failed', e);
            }
            return;
        }

        if (data.type === 'THEME_DELETE_RESULT') {
            if (data.ok) {
                const wasShowing = document.documentElement.dataset.theme === data.themeId;
                unregisterUserTheme(data.themeId);
                if (wasShowing) applyUiTheme(data.activeTheme || DEFAULT_UI_THEME);
                if (this.realData?.managerSettings && data.activeTheme) {
                    this.realData.managerSettings.uiTheme = data.activeTheme;
                }
                this.showBanner({
                    type: 'success',
                    text: window._t('theme_delete_success', data.displayName || data.themeId || ''),
                });
                if (this.currentSection === 'settings' && this.sections.settings) {
                    this.replaceCurrentSectionContent();
                }
            } else {
                this.showBanner({
                    type: 'error',
                    text: data.error || window._t('theme_delete_failed'),
                });
            }
            return;
        }

        if (data.type === 'THEME_RELOAD_RESULT') {
            const loaded = Array.isArray(data.loaded) ? data.loaded : [];
            const rejected = Array.isArray(data.rejected) ? data.rejected : [];
            if (loaded.length && !rejected.length) {
                this.showBanner({
                    type: 'success',
                    text: window._t('theme_reload_success', String(loaded.length)),
                });
            } else if (loaded.length && rejected.length) {
                this.showBanner({
                    type: 'warning',
                    text: window._t('theme_reload_partial', String(loaded.length), String(rejected.length)),
                });
            } else if (rejected.length) {
                const first = rejected[0];
                const detail = first?.error || '';
                const file = first?.fileName || '';
                this.showBanner({
                    type: 'error',
                    text: window._t('theme_rejected_item', file, detail)
                        || window._t('theme_reload_failed'),
                });
            } else {
                this.showBanner({
                    type: 'info',
                    text: window._t('theme_reload_empty', data.themesFolder || ''),
                });
            }
            if (this.currentSection === 'settings' && this.sections.settings) {
                this.replaceCurrentSectionContent();
            }
            return;
        }

        let statusObj = null;
        if (data.type === 'STATUS') {
            statusObj = data.status;
        } else if (data.status && (data.status.type === 'success' || data.status.type === 'error' || data.status.type === 'info' || data.status.type === 'warning')) {
            statusObj = data.status;
        }

        if (statusObj) {
            console.log('[UI] Showing banner:', statusObj);
            this.showBanner(statusObj);
            document.dispatchEvent(new CustomEvent('app-message', { detail: statusObj }));
            if (data.type === 'STATUS') return;
        }

        if (data.type === 'APP_UPDATE_STATUS') {
            this.appUpdate = data;
            this.syncUpdateNavBadge();
            this.syncAppUpdateBanner(data);
            if (this.currentSection === 'update') {
                this.replaceCurrentSectionContent();
            }
            return;
        }

        if (data.type === 'conflicts_found') {
            const isAutoOverride = this.realData?.managerSettings?.autoForceDeploy;
            
            if (isAutoOverride && data.isFromDeploy) {
                const modsToSend = data.requestedMods || [];
                console.log('[UI] Conflicts detected, but autoForceDeploy is enabled. Auto-resolving with', modsToSend.length, 'mods.');
                this.showBanner({ type: 'success', text: 'Conflicts auto-resolved (Override enabled)' });

                window.chrome.webview.postMessage({
                    type: 'DEPLOY_MODS',
                    mods: modsToSend,
                    force: true
                });
                return;
            }

            console.log('[UI] Conflicts detected, showing modal.');
            this.conflictModal.show(data);
            return;
        }

        if (data.type === 'SHOW_ENDORSEMENT') {
            console.log('[UI] Threshold reached, showing endorsement modal.');
            this.showEndorsementModal();
            return;
        }

        if (data.type === 'NEXUS_RESOLVE_MOD_LINK_RESULT') {
            if (window.modsManager && typeof window.modsManager.handleNexusResolveModLinkResult === 'function') {
                window.modsManager.handleNexusResolveModLinkResult(data);
            }
            return;
        }

        if (data.type === 'NEXUS_COLLECTION_STARTED') {
            this.showCollectionProgressBanner(0, 0, data.total ?? 0);
            return;
        }

        if (data.type === 'NEXUS_COLLECTION_DOWNLOAD_PROGRESS') {
            this.showCollectionProgressBanner(
                data.percent ?? 0,
                data.current ?? 0,
                data.total ?? 0
            );
            return;
        }

        if (data.type === 'NEXUS_COLLECTION_COMPLETE') {
            const collectionBanner = this.notificationContainer?.querySelector('[data-progress-id="collection-import"]');
            if (collectionBanner) collectionBanner.remove();

            const label = data.collectionName || 'Collection';
            const failed = data.failed ?? 0;
            this.showBanner({
                type: failed > 0 ? 'warning' : 'success',
                text: failed > 0
                    ? window._t(
                        'nexus_collection_complete_partial',
                        label,
                        data.queued ?? 0,
                        failed
                    )
                    : window._t(
                        'nexus_collection_complete',
                        label,
                        data.queued ?? 0
                    )
            });
            return;
        }

        if (data.type === 'MOD_UPDATES_RESULT') {
            if (window.modsManager && typeof window.modsManager.handleModUpdatesResult === 'function') {
                window.modsManager.handleModUpdatesResult(data);
            }
            return;
        }

        if (data.type === 'BA2_CONTENTS' || data.type === 'GAME_BA2_LIST' || data.type === 'FOLDER_CONTENTS') {
            const bundle = this.sections?.bundle;
            if (this.currentSection === 'bundle' && bundle?.isCreating && typeof bundle.handleInspectorMessage === 'function') {
                bundle.handleInspectorMessage(data);
            }
            return;
        }

        if (data.type === 'REQUEST_MOD_UPDATE_CHECK') {
            if (window.modsManager && typeof window.modsManager.scheduleModUpdateCheck === 'function') {
                window.modsManager.scheduleModUpdateCheck(700);
            }
            return;
        }

        if (data.type === 'CONFIRM_MOD_UPDATE_REPLACE') {
            void appConfirm({
                title: 'Confirmation',
                message: typeof window._t === 'function'
                    ? window._t('mod_update_remove_confirm', data.displayName || data.originalName || '')
                    : `Remove the previous version of "${data.displayName || data.originalName || ''}"? The new update is already installed.`,
                okText: typeof window._t === 'function' ? window._t('delete') : 'Delete',
                cancelText: typeof window._t === 'function' ? window._t('cancel') : 'Cancel',
                danger: true
            }).then((ok) => {
                if (!window.chrome?.webview) return;
                window.chrome.webview.postMessage({
                    type: 'MOD_UPDATE_REPLACE_RESPONSE',
                    confirmed: ok,
                    originalName: data.originalName,
                    importedKeys: Array.isArray(data.importedKeys) ? data.importedKeys : []
                });
            });
            return;
        }

        if (data.type === 'BACKUPS_LIST') {
            this.sections.settings?.handleBackupsList?.(data);
            return;
        }

        if (data.type === 'BACKUP_PREVIEW') {
            this.sections.settings?.handleBackupPreview?.(data);
            return;
        }

        if (data.type === 'BULK_UPDATE_STARTED') {
            window.modsManager?.handleBulkUpdateStarted?.(data);
            return;
        }

        if (data.type === 'BULK_UPDATE_PROGRESS') {
            window.modsManager?.handleBulkUpdateProgress?.(data);
            return;
        }

        if (data.type === 'BULK_UPDATE_COMPLETE') {
            window.modsManager?.handleBulkUpdateComplete?.(data);
            return;
        }

        if (data.type === 'COLLECTION_REVISION_RESULT') {
            return;
        }

        if (data.type === 'INI_CONTENT') {
             this.iniEditor.updateContent(data.iniType, data.content);
             return;
        }

        if (data.type === 'MOD_FILE_CONTENT') {
            if (window.modsManager && typeof window.modsManager.handleModFileContent === 'function') {
                window.modsManager.handleModFileContent(data);
            }
            return;
        }

        if (data.type === 'MOD_FILE_WRITE_RESULT') {
            if (window.modsManager && typeof window.modsManager.handleModFileWriteResult === 'function') {
                window.modsManager.handleModFileWriteResult(data);
            }
            return;
        }

        if (data.type === 'MOD_FILE_ADOPT_RESULT') {
            if (window.modsManager && typeof window.modsManager.handleModFileAdoptResult === 'function') {
                window.modsManager.handleModFileAdoptResult(data);
            }
            return;
        }

        if (data.type === 'MOD_CONTENTS') {
            if (window.modsManager && typeof window.modsManager.handleModContents === 'function') {
                window.modsManager.handleModContents(data);
            }
            return;
        }

        if (data.type === 'MOD_FILES_ADDING' || data.type === 'MOD_FILES_ADDED') {
            if (window.modsManager && typeof window.modsManager.handleModFilesAdded === 'function') {
                window.modsManager.handleModFilesAdded(data);
            }
            return;
        }

        if (data.type === 'LOGS_DATA') {
            this._applyLogsData(data.logs);
            return;
        }

        if (data.type === 'INTERFACE_COLORS') {
             return;
        }

        if (data.type === 'FILE_SELECTED' || data.type === 'FOLDER_SELECTED') {
            return;
        }

        if (data.type === 'UPDATE_DATA') {
            if (data.userThemes) registerUserThemesFromHost(data.userThemes, { authoritative: true });
            if (data.modsDeferred) {
                const source = this._pendingUpdateData && !this._pendingUpdateData.modsDeferred
                    ? this._pendingUpdateData
                    : (this.realData && !this.realData.modsDeferred ? this.realData : null);
                if (source) {
                    data = { ...data, mods: source.mods, stats: source.stats, modsDeferred: false };
                }
            }
            this._pendingUpdateData = data;
            if (this._updateDataFlushHandle) return;

            this._updateDataFlushHandle = setTimeout(() => {
                const latest = this._pendingUpdateData;
                this._pendingUpdateData = null;
                this._updateDataFlushHandle = null;
                if (latest) this._applyUpdateData(latest);
            }, 40);
            return;
        }

        if (!data.type || data.type === 'UPDATE_DATA') {
            this._applyUpdateData(data);
        }
    }

    _applyLogsData(logsPayload) {
        if (!this.realData) return;
        this.realData.logs = logsPayload || { activity: [], errors: [], errorCount: 0 };
        const logsSection = this.sections?.logs;
        if (logsSection && typeof logsSection.refreshView === 'function') {
            logsSection.refreshView(this.realData, true);
        }
    }

    _applyUpdateData(data) {
        const firstData = !this.hasRenderedInitially;
        const tApply = performance.now();
        if (data.userThemes) registerUserThemesFromHost(data.userThemes, { authoritative: true });
        this.realData = data;
        if (data.modPresets || data.modGroups) {
            this.modGroups.setFromHost(data.modPresets || data.modGroups, data.activeModPreset);
        }

        if (this.realData.managerSettings) {
            const ms = this.realData.managerSettings;
            if (ms.keybinds && !localStorage.getItem('f76_manager_keybinds')) {
                syncKeybindsFromManagerSettings(ms);
            }
            this.applyLiveUiSettings(ms);

            if (!window.__THEME_STUDIO_PREVIEW) {
                const hostTheme = ms.uiTheme || DEFAULT_UI_THEME;
                const prevHostTheme = this._lastHostUiTheme;
                this._lastHostUiTheme = hostTheme;
                if (prevHostTheme === undefined || prevHostTheme !== hostTheme) {
                    applyUiTheme(hostTheme);
                }
            }

            const newLang = ms.language || 'en-US';
            const oldLang = translator.currentLanguage;
            
            if (newLang !== oldLang) {
                 translator.setLanguage(newLang).then(() => {
                     if (translator.currentLanguage !== oldLang) {
                         console.log(`[UI] Language changed from ${oldLang} to ${translator.currentLanguage}`);
                         this.translateStaticElements(); 
                         this.refreshCurrentSection();
                     }
                 });
            }
        }

        this.syncProfileDropdown();
        this.syncPlatformUI();
        document.documentElement.classList.remove('app-booting');

        if (window.__THEME_STUDIO_PREVIEW && isPreviewPlaceholderSection(this.currentSection) && !window.__THEME_STUDIO_PREVIEW_LIVE_MODS) {
            this.hasRenderedInitially = true;
            return;
        }

        if (window.__THEME_STUDIO_PREVIEW && window.__THEME_STUDIO_ACTIVE_SECTION) {
            const studioSection = window.__THEME_STUDIO_ACTIVE_SECTION;
            if (studioSection !== this.currentSection) {
                this.navigateTo(studioSection, true, false, true);
            }
            this.hasRenderedInitially = true;
            return;
        }

        if (!this.hasRenderedInitially) {
            const initialSection = this.pickInitialSection();
            if (!this._translationsReady) {
                this.highlightSection(initialSection);
            } else {
                this.navigateTo(initialSection, true, false, true);
                this.hasRenderedInitially = true;
            }
        } else {
            const activeManager = this.sections[this.currentSection];
            if (activeManager && typeof activeManager.updateValues === 'function') {
                activeManager.updateValues(this.realData);
            } else {
                this.replaceCurrentSectionContent();
            }
            this.syncProfileDropdown();
            this.syncPlatformUI();
        }

        if (this.hasRenderedInitially && (firstData || (data.mods?.length ?? 0) > 0)) {
            this.bootMark(`UPDATE_DATA applied (first=${firstData}, mods=${data.mods?.length ?? 0}) render=${Math.round(performance.now() - tApply)}ms`);
        }
    }

    translateStaticElements() {
        const navMappings = {
            'dashboard': 'dashboard',
            'mods': 'mods',
            'config': 'config',
            'bundle': 'bundle',
            'tweaks': 'tweaks',
            'pipboy': 'pip_boy',
            'profiles': 'profiles',
            'logs': 'logs',
            'update': 'update',
            'settings': 'settings'
        };

        Object.entries(navMappings).forEach(([section, key]) => {
            const navItem = document.querySelector(`[data-section="${section}"] .nav-label`);
            if (navItem) {
                navItem.textContent = window._t(key);
            }
        });

        const endorseLabel = document.querySelector('#endorsement-nav-item .nav-label');
        if (endorseLabel) endorseLabel.textContent = window._t('endorse_on_nexus').split(' ')[0] || 'Endorse';
        
        const laterLabel = document.getElementById('endorsement-later');
        if (laterLabel) laterLabel.textContent = window._t('maybe_later');
        this.syncUpdateNavBadge();
    }

    syncTweaksPendingBanner() {
        const tm = this.sections.tweaks;
        if (!this.notificationContainer || !tm) return;
        const hasPending = tm.pendingChanges && Object.keys(tm.pendingChanges).length > 0;
        const selector = '[data-banner-id="tweaks-pending"]';
        const existing = this.notificationContainer.querySelector(selector);
        if (!hasPending) {
            if (existing) existing.remove();
            return;
        }
        if (existing) {
            const span = existing.querySelector('span');
            if (span) span.textContent = window._t('tweak_changes_pending');
            return;
        }
        const el = document.createElement('div');
        el.setAttribute('data-banner-id', 'tweaks-pending');
        el.className = 'status-banner success animate-fade';
        el.innerHTML = `
            <i data-lucide="check-circle"></i>
            <span>${escapeHtml(window._t('tweak_changes_pending'))}</span>
            <button class="close-status">&times;</button>
        `;
        const closeBtn = el.querySelector('.close-status');
        closeBtn.onclick = () => el.remove();
        this.notificationContainer.appendChild(el);
        if (window.lucide) lucide.createIcons();
    }

    clearDownloadBannerDismissTimer() {
        if (this._downloadBannerDismissTimer) {
            clearTimeout(this._downloadBannerDismissTimer);
            this._downloadBannerDismissTimer = null;
        }
    }

    clearBannerDismissTimer(bannerEl) {
        if (!bannerEl) return;
        const id = bannerEl._bannerDismissTimer;
        if (id) {
            clearTimeout(id);
            bannerEl._bannerDismissTimer = null;
        }
    }

    scheduleBannerAutoDismiss(bannerEl, status, displayText) {
        if (!bannerEl || !status) return;
        this.clearBannerDismissTimer(bannerEl);

        const sticky = status.sticky === true;
        const hasActions = Array.isArray(status.actions) && status.actions.length > 0;
        if (sticky || hasActions) return;

        let ms = 0;
        if (status.type === 'success') ms = 5000;
        else if (status.type === 'info') ms = 20000;
        else if (status.type === 'warning') ms = 8000;
        else if (status.type === 'error') ms = 12000;
        if (ms <= 0) return;

        bannerEl._bannerDismissTimer = setTimeout(() => {
            bannerEl._bannerDismissTimer = null;
            if (bannerEl.parentElement) bannerEl.remove();
        }, ms);

        if (status.type === 'info') {
            this.scheduleDownloadInfoDismissIfComplete(bannerEl, displayText);
        }
    }

    scheduleDownloadInfoDismissIfComplete(infoBannerEl, displayText) {
        this.clearDownloadBannerDismissTimer();
        const text = (displayText || '').toString();
        if (!infoBannerEl || !/\bdownloading\b/i.test(text) || !/\b100%\b/.test(text)) return;
        const el = infoBannerEl;
        this._downloadBannerDismissTimer = setTimeout(() => {
            this._downloadBannerDismissTimer = null;
            if (el.parentElement) el.remove();
        }, 2000);
    }

    showCollectionProgressBanner(percent, current, total, done = false) {
        const text = window._t(
            'nexus_collection_download_progress',
            percent ?? 0,
            current ?? 0,
            total ?? 0
        );
        this.showBanner({
            type: 'info',
            text,
            isProgress: true,
            progressId: 'collection-import',
            done
        });
    }

    showBanner(status) {
        console.log('showBanner called with:', status, 'notificationContainer:', this.notificationContainer);
        if (!this.notificationContainer) return;

        let displayText = status.text;
        if (status.key) {
            const args = Array.isArray(status.args) ? status.args : [];
            const translated = window._t(status.key, ...args);
            const found = !!translated && translated !== status.key;
            const hasUnfilledPlaceholder = /\{\d+\}/.test(translated || '');
            if (found && !hasUnfilledPlaceholder) {
                displayText = translated;
            } else if (!status.text) {
                displayText = translated;
            }
            console.log(`[UI] Translated banner: '${status.key}' -> '${displayText}'`);
        }

        if (status.isProgress && status.progressId) {
            if (status.progressId === 'mod-import') {
                this.clearDownloadBannerDismissTimer();
                const dlInfo = this.notificationContainer.querySelector('.status-banner.info:not([data-progress-id])');
                if (dlInfo) dlInfo.remove();
            }

            const isCollection = status.progressId === 'collection-import';
            const isNexusDownload = status.progressId === 'nexus-download';

            if (isNexusDownload && status.done) {
                const ex = this.notificationContainer.querySelector('[data-progress-id="nexus-download"]');
                if (ex) ex.remove();
                return;
            }

            let existing = this.notificationContainer.querySelector(`[data-progress-id="${status.progressId}"]`);
            if (!existing) {
                existing = document.createElement('div');
                existing.className = (isCollection || isNexusDownload)
                    ? 'status-banner info animate-fade'
                    : 'status-banner success animate-fade';
                existing.setAttribute('data-progress-id', status.progressId);
                const iconName = (isCollection || isNexusDownload) ? 'download' : 'check-circle';
                const cancelHtml = (isNexusDownload && status.cancelable)
                    ? `<button type="button" class="status-banner-action cancel-download">${escapeHtml(window._t('nexus_download_cancel', 'Cancel'))}</button>`
                    : '';
                existing.innerHTML = `
                    <i data-lucide="${iconName}"></i>
                    <span>${escapeHtml(displayText)}</span>
                    ${cancelHtml}
                    <button class="close-status">&times;</button>
                `;
                const cancelBtn = existing.querySelector('.cancel-download');
                if (cancelBtn) {
                    cancelBtn.onclick = () => {
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage({ type: 'CANCEL_NEXUS_DOWNLOAD' });
                        }
                        cancelBtn.disabled = true;
                        const span = existing.querySelector('span');
                        if (span) span.textContent = window._t('nexus_download_cancelling', 'Cancelling download…');
                    };
                }
                const closeBtn = existing.querySelector('.close-status');
                closeBtn.onclick = () => existing.remove();
                this.notificationContainer.appendChild(existing);
                if (window.lucide) lucide.createIcons();
            } else {
                const span = existing.querySelector('span');
                if (span) span.textContent = displayText;
            }

            if (status.done) {
                setTimeout(() => {
                    if (existing && existing.parentElement) existing.remove();
                }, 2000);
            }
            return;
        }

        if (status.type === 'info') {
            const existing = this.notificationContainer.querySelector('.status-banner.info:not([data-progress-id])');
            if (existing) {
                const span = existing.querySelector('span');
                if (span) span.textContent = displayText;
                this.scheduleBannerAutoDismiss(existing, status, displayText);
                return;
            }
        }

        const banner = document.createElement('div');
        banner.className = `status-banner ${status.type} animate-fade`;

        let icon = 'info';
        if (status.type === 'success') icon = 'check-circle';
        if (status.type === 'error') icon = 'alert-circle';
        if (status.type === 'warning') icon = 'alert-triangle';

        const actions = Array.isArray(status.actions) ? status.actions : [];
        let actionsHtml = '';
        if (actions.length > 0) {
            actionsHtml = '<div class="status-banner-actions">' +
                actions.map((a) => {
                    const url = (a && a.url) ? String(a.url) : '';
                    const label = (a && a.label) ? String(a.label) : 'Open';
                    return `<button type="button" class="status-banner-action" data-url="${escapeAttr(url)}">${escapeHtml(label)}</button>`;
                }).join('') +
                '</div>';
        }

        banner.innerHTML = `
            <i data-lucide="${icon}"></i>
            <span>${escapeHtml(displayText)}</span>
            ${actionsHtml}
            <button class="close-status">&times;</button>
        `;

        banner.querySelectorAll('.status-banner-action').forEach((btn) => {
            btn.addEventListener('click', () => {
                const u = btn.getAttribute('data-url');
                if (u && window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'OPEN_IN_BROWSER', url: u });
                }
            });
        });

        const closeBtn = banner.querySelector('.close-status');
        closeBtn.onclick = () => banner.remove();

        const tweaksPending = this.notificationContainer.querySelector('[data-banner-id="tweaks-pending"]');
        let progressBanners = Array.from(this.notificationContainer.querySelectorAll('[data-progress-id]'));
        if (!status.isProgress && (status.type === 'success' || status.type === 'error' || status.type === 'warning')) {
            progressBanners = progressBanners.filter(
                (el) => el.getAttribute('data-progress-id') !== 'mod-import'
            );
        }
        if (tweaksPending) tweaksPending.remove();
        this.notificationContainer.innerHTML = '';
        if (tweaksPending) this.notificationContainer.appendChild(tweaksPending);
        progressBanners.forEach(el => this.notificationContainer.appendChild(el));
        this.notificationContainer.appendChild(banner);
        lucide.createIcons();
        this.scheduleBannerAutoDismiss(banner, status, displayText);
    }


    showEndorsementModal() {
        console.log('[UI] Showing endorsement via sidebar.');
        const container = document.getElementById('endorsement-container');
        if (container) {
            container.classList.remove('hidden');
            this.setupEndorsementClick();
        }

    }

    setupEndorsementClick() {
        const container = document.getElementById('endorsement-container');
        const btn = document.getElementById('endorsement-nav-item');
        const laterBtn = document.getElementById('endorsement-later');

        if (btn && !btn.hasAttribute('data-bound')) {
            btn.setAttribute('data-bound', 'true');
            btn.addEventListener('click', (e) => {
                e.preventDefault();
                const url = 'https://www.nexusmods.com/fallout76/mods/3674?tab=files';
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'OPEN_IN_BROWSER', url: url });
                    window.chrome.webview.postMessage({ type: 'ENDORSEMENT_CONFIRMED' });
                }
                if (container) container.classList.add('hidden');
            });
        }

        if (laterBtn && !laterBtn.hasAttribute('data-bound')) {
            laterBtn.setAttribute('data-bound', 'true');
            laterBtn.addEventListener('click', (e) => {
                e.preventDefault();
                e.stopPropagation();
                if (container) container.classList.add('hidden');
            });
        }
    }

    setupNavigation() {
        document.querySelectorAll('[data-section]').forEach(link => {
            link.addEventListener('click', (e) => {
                e.preventDefault();
                const sectionId = link.getAttribute('data-section');
                this.navigateTo(sectionId);
            });
        });

        const toggleBtn = document.getElementById('toggle-sidebar');
        if (toggleBtn) {
            toggleBtn.addEventListener('click', () => {
                this.sidebar.classList.toggle('collapsed');
                refreshSharpLogos();
            });
        }
    }

    formatSectionTitle(sectionId) {
        const id = String(sectionId || '').trim();
        if (!id) return '';

        switch (id) {
            case 'config':
                return 'Configs';
            case 'pipboy':
                return 'Pip-Boy';
            default:
                break;
        }

        return id
            .split('-')
            .map(word => word.charAt(0).toUpperCase() + word.slice(1))
            .join(' ');
    }

    rememberedSection() {
        try {
            const saved = sessionStorage.getItem('f76_ui_section');
            if (saved && this.sections[saved]) return saved;
        } catch {  }
        return '';
    }

    rememberSection(sectionId) {
        if (!sectionId || !this.sections[sectionId]) return;
        try { sessionStorage.setItem('f76_ui_section', sectionId); } catch {  }
    }

    restoreSectionChrome() {
        const id = this.rememberedSection();
        if (!id) return;
        this.highlightSection(id);
    }

    applyLiveUiSettings(ms = this.realData?.managerSettings || {}) {
        const overrides = this._liveSettingOverrides || {};
        const pick = (key) => (key in overrides ? overrides[key] : ms[key]);
        document.body.classList.toggle('no-animations', pick('uiAnimations') === false);
        document.body.classList.toggle('no-platform-glow', pick('platformBadgeGlow') === false);
        const kofiLink = document.getElementById('kofi-link');
        if (kofiLink) kofiLink.hidden = !!pick('hideKofi');
    }

    highlightSection(sectionId) {
        if (!sectionId || !this.sections[sectionId]) return;
        this.currentSection = sectionId;
        document.querySelectorAll('[data-section]').forEach(link => {
            link.classList.toggle('active', link.getAttribute('data-section') === sectionId);
        });
        const titleEl = document.getElementById('section-title');
        if (titleEl) titleEl.textContent = this.formatSectionTitle(sectionId);
        try {
            document.body.dataset.uiSection = sectionId;
            if (this.contentArea) {
                this.contentArea.dataset.activeSection = sectionId;
                this.contentArea.classList.toggle('logs-active', sectionId === 'logs');
            }
        } catch {  }
    }

    pickInitialSection() {
        if (this._userChoseSection && this.currentSection && this.sections[this.currentSection]) {
            return this.currentSection;
        }
        const savedSection = this.realData?.lastSection;
        if (savedSection && this.sections[savedSection]) return savedSection;
        return this.rememberedSection() || 'dashboard';
    }

    finishInitialPaint() {
        if (!this._translationsReady || this.hasRenderedInitially || !this.realData) return;
        this.navigateTo(this.pickInitialSection(), true, false, true);
        this.hasRenderedInitially = true;
    }

    navigateTo(sectionId, force = false, persist = true, keepNotifications = false) {
        if (!force && this.currentSection === sectionId && this.contentArea.innerHTML !== '') {
            if (sectionId === 'update') this.sections.update?.updateValues?.(this.realData);
            return;
        }

        if (!this._translationsReady && !window.__THEME_STUDIO_PREVIEW) {
            if (persist) this._userChoseSection = true;
            this.highlightSection(sectionId);
            this.rememberSection(sectionId);
            return;
        }

        if (this.currentSection === 'settings' && sectionId !== 'settings'
            && this._liveSettingOverrides && Object.keys(this._liveSettingOverrides).length) {
            this._liveSettingOverrides = {};
            this.applyLiveUiSettings();
        }

        this.sections.mods?._closeModsActionsMenu?.();
        this.highlightSection(sectionId);
        this.rememberSection(sectionId);

        if (this.notificationContainer && !keepNotifications) this.notificationContainer.innerHTML = '';

        if (persist) {
            this._userChoseSection = true;
            if (window.chrome && window.chrome.webview && !window.__THEME_STUDIO_PREVIEW) {
                this._pendingPersistSection = sectionId;
                clearTimeout(this._navigatePersistTimer);
                this._navigatePersistTimer = setTimeout(() => {
                    if (!this._pendingPersistSection) return;
                    window.chrome.webview.postMessage({
                        type: 'NAVIGATE_TO',
                        section: this._pendingPersistSection
                    });
                }, 400);
            }
        }

        if (sectionId === 'tweaks' && window.chrome?.webview && !window.__THEME_STUDIO_PREVIEW) {
            window.chrome.webview.postMessage({ type: 'SYNC_TWEAKS_FROM_INI' });
        }

        const section = this.sections[sectionId];
        if (section) {
            const usePlaceholder = window.__THEME_STUDIO_PREVIEW
                && isPreviewPlaceholderSection(sectionId)
                && !window.__THEME_STUDIO_PREVIEW_LIVE_MODS;
            if (usePlaceholder) {
                mountPreviewPlaceholder(this.contentArea, sectionId);
                this.syncTweaksPendingBanner();
                return;
            }

            const payload = this.realData ?? { mods: [], modGroups: {}, managerSettings: {} };
            this._beginSectionEnter(sectionId);
            this.contentArea.classList.add('content-refreshing');
            try {
                this.contentArea.innerHTML = `<div class="section-content">${section.render(payload)}</div>`;
                if (section.onMount) section.onMount(payload);
                applyModTypeBadgeStylesheet();
                lucide.createIcons();
                this.syncTweaksPendingBanner();
            } catch (err) {
                console.error(`[UI] Failed to render section "${sectionId}":`, err);
                this.contentArea.innerHTML = `
                    <div class="section-content">
                        <div class="empty-section" style="padding:48px; text-align:center; opacity:0.7;">
                            <i data-lucide="alert-triangle" style="width:48px; height:48px; margin-bottom:16px; color: var(--danger-red);"></i>
                            <h2>Could not load this section</h2>
                            <p style="color: var(--text-muted);">${String(err?.message || err)}</p>
                        </div>
                    </div>`;
                lucide.createIcons();
            }
            requestAnimationFrame(() => {
                requestAnimationFrame(() => this.contentArea.classList.remove('content-refreshing'));
            });
        } else {
            this.contentArea.classList.add('content-refreshing');
            this.contentArea.innerHTML = `
                <div class="empty-section" style="padding:48px; text-align:center; opacity:0.5;">
                    <i data-lucide="construct" style="width:64px; height:64px; margin-bottom:16px;"></i>
                    <h2>Section "${sectionId}" under development</h2>
                    <p>This module will be available in the next update.</p>
                </div>
            `;
            lucide.createIcons();
            this.syncTweaksPendingBanner();
            requestAnimationFrame(() => {
                requestAnimationFrame(() => this.contentArea.classList.remove('content-refreshing'));
            });
        }
    }

    _beginSectionEnter(sectionId) {
        const isNewSection = this._lastRenderedSection !== sectionId;
        this._lastRenderedSection = sectionId;
        if (!isNewSection || !this.contentArea) return;
        clearTimeout(this._sectionEnterTimer);
        this.contentArea.classList.add('section-entering');
        this._sectionEnterTimer = setTimeout(() => {
            this.contentArea?.classList.remove('section-entering');
        }, 700);
    }

    _cancelSectionEnter() {
        clearTimeout(this._sectionEnterTimer);
        this.contentArea?.classList.remove('section-entering');
    }

    refreshCurrentSection() {
        if (this.replaceCurrentSectionContent()) return;
        this.navigateTo(this.currentSection, true, false);
    }

    replaceCurrentSectionContent() {
        if (window.__THEME_STUDIO_PREVIEW && isPreviewPlaceholderSection(this.currentSection) && !window.__THEME_STUDIO_PREVIEW_LIVE_MODS) {
            mountPreviewPlaceholder(this.contentArea, this.currentSection);
            return true;
        }
        const section = this.sections[this.currentSection];
        if (!section || !this.contentArea || !this.realData) return false;
        this.sections.mods?._closeModsActionsMenu?.();
        this._cancelSectionEnter();
        try {
            document.body.dataset.uiSection = this.currentSection;
            this.contentArea.dataset.activeSection = this.currentSection;
        } catch (_) { }
        this.contentArea.classList.add('content-refreshing');
        this.contentArea.innerHTML = `<div class="section-content">${section.render(this.realData)}</div>`;
        if (section.onMount) section.onMount(this.realData);
        applyModTypeBadgeStylesheet();
        if (window.lucide) lucide.createIcons();
        this.syncTweaksPendingBanner();
        requestAnimationFrame(() => {
            requestAnimationFrame(() => this.contentArea.classList.remove('content-refreshing'));
        });
        return true;
    }

    syncProfileDropdown() {
        const dropdown = document.getElementById('profile-dropdown');
        if (!dropdown || !this.realData || !this.realData.profiles) return;

        const profiles = this.realData.profiles;
        const active = this.realData.activeProfile;
        const nextHtml = profiles.map(p =>
            `<option value="${escapeAttr(p)}" ${p === active ? 'selected' : ''}>${escapeHtml(p)}</option>`
        ).join('');

        if (dropdown.innerHTML !== nextHtml) {
            dropdown.innerHTML = nextHtml;
        }
    }

    syncUpdateNavBadge() {
        const badge = document.getElementById('update-nav-badge');
        if (!badge) return;
        const show = !!this.appUpdate?.updateAvailable;
        badge.hidden = !show;
        if (show) badge.textContent = window._t('update_badge');
    }

    cancelAppUpdate() {
        this._appUpdateCancelRequested = true;
        window.chrome?.webview?.postMessage({ type: 'CANCEL_APP_UPDATE' });
        this.notificationContainer?.querySelector('[data-progress-id="app-update"]')?.remove();
        if (this.currentSection === 'update') this.replaceCurrentSectionContent();
    }

    syncAppUpdateBanner(status) {
        if (!this.notificationContainer) return;
        const phase = status?.phase;
        let el = this.notificationContainer.querySelector('[data-progress-id="app-update"]');
        if (phase !== 'downloading' && phase !== 'preparing') {
            this._appUpdateCancelRequested = false;
            if (el) el.remove();
            return;
        }
        if (this._appUpdateCancelRequested) return;

        const text = phase === 'preparing'
            ? window._t('update_preparing')
            : window._t('update_installing', status.percent ?? 0);

        if (!el) {
            el = document.createElement('div');
            el.className = 'status-banner info animate-fade';
            el.setAttribute('data-progress-id', 'app-update');
            el.innerHTML = `
                <i data-lucide="download"></i>
                <span></span>
                <button type="button" class="status-banner-action cancel-download">${escapeHtml(window._t('update_cancel'))}</button>
                <button class="close-status">&times;</button>
            `;
            el.querySelector('.cancel-download').onclick = () => this.cancelAppUpdate();
            el.querySelector('.close-status').onclick = () => this.cancelAppUpdate();
            this.notificationContainer.appendChild(el);
            if (window.lucide) lucide.createIcons();
        }
        const span = el.querySelector('span');
        if (span) span.textContent = text;
    }

    syncPlatformUI() {
        const toggle = document.getElementById('platform-toggle');
        if (!toggle || !this.realData || !this.realData.platform) return;

        const platform = this.realData.platform;
        const span = toggle.querySelector('span');
        const icon = toggle.querySelector('i');

        const nextClass = `platform-badge ${platform.toLowerCase()}`;
        if (toggle.className !== nextClass) toggle.className = nextClass;
        if (span && span.innerText !== platform) span.innerText = platform;

        if (icon) {
            const nextIcon = platform.toLowerCase() === 'xbox' ? 'box' : 'gamepad-2';
            if (icon.getAttribute('data-lucide') !== nextIcon) {
                icon.setAttribute('data-lucide', nextIcon);
                if (window.lucide) lucide.createIcons();
            }
        }
    }

    registerSection(id, component) {
        this.sections[id] = component;
    }

    _shouldIgnoreShortcut(e) {
        if (window.__keybindCapturing) return true;
        const tag = (e.target?.tagName || '').toLowerCase();
        if (tag === 'input' || tag === 'textarea' || tag === 'select') return true;
        if (e.target?.isContentEditable) return true;
        if (document.querySelector('#confirm-modal.active, #conflict-popup-overlay, #rename-mod-overlay, .modal-overlay.active')) return true;
        return false;
    }

    setupKeyboardShortcuts() {
        if (this._keybindHandler) {
            document.removeEventListener('keydown', this._keybindHandler);
        }
        this._keybindHandler = (e) => {
            if (this._shouldIgnoreShortcut(e)) return;
            const bindings = getKeybinds(this.realData?.managerSettings);
            for (const [id, chord] of Object.entries(bindings)) {
                if (!chord || !eventMatchesChord(e, chord)) continue;
                e.preventDefault();
                executeKeybindAction(id, this);
                return;
            }
        };
        document.addEventListener('keydown', this._keybindHandler);
    }

    cycleProfile() {
        const dropdown = document.getElementById('profile-dropdown');
        if (!dropdown || !this.realData?.profiles?.length) return;
        const profiles = this.realData.profiles;
        const active = this.realData.activeProfile;
        const idx = profiles.indexOf(active);
        const next = profiles[(idx + 1) % profiles.length];
        dropdown.value = next;
        window.chrome?.webview?.postMessage({ type: 'SWITCH_PROFILE', name: next });
    }
}

window.app = new App();
window.app.bootMark('app.bundle.js evaluated');
window.app.registerSection('dashboard', new Dashboard());
window.app.registerSection('mods', new ModsManager(window.app.modGroups));
window.app.registerSection('config', new ConfigManager());
window.app.registerSection('bundle', new BundleManager());
window.app.registerSection('tweaks', new TweaksManager());
window.app.registerSection('pipboy', new PipBoy());
window.app.registerSection('profiles', new Profiles());
window.app.registerSection('logs', new Logs());
window.app.registerSection('update', new UpdateManager());
window.app.registerSection('settings', new Settings());

window.modsManager = window.app.sections?.mods;


document.addEventListener('DOMContentLoaded', () => {
    applyModTypeBadgeStylesheet();
    window.app.init();
});
