import { ModsRenderer, buildModUpdateButtonHtml, buildModUnverifiedButtonHtml } from './ModsRenderer.js';
import { ModsDndHandler } from './ModsDndHandler.js';
import { escapeAttr } from '../utils/htmlSafe.js';
import { applyStylesheet as applyModTypeBadgeStylesheet } from '../utils/modTypeBadgeTheme.js';
import { openTabBadgeColorModal } from '../utils/badgeColorModal.js';

let _modsGlobalEventsAttached = false;

async function deleteModWithConfirmation(name) {
    if (!name) return;
    const confirmEnabled = !!(window.app?.realData?.managerSettings?.confirmBeforeDeleteMod);
    if (confirmEnabled) {
        const ok = await window.appConfirm({
            title: 'Confirmation',
            message: window._t('delete_mod_confirm', name),
            okText: window._t('delete'),
            cancelText: window._t('cancel'),
            danger: true
        });
        if (!ok) return;
    }
    console.log(`[MODS] Deleting mod: ${name}`);
    window.chrome.webview.postMessage({ type: 'DELETE_MOD', name });
}

export class ModsManager {
    constructor(modGroupsManager) {
        this.modGroupsManager = modGroupsManager;
        this._modsActionsMenuAbort = null;
        localStorage.removeItem('mods_sort_field');
        localStorage.removeItem('mods_sort_order');
        this.lastSearchTerm = '';
        this.currentPreset = 'all';
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

    _setupConfigEditorLayout(overlay) {
        this._teardownConfigEditorLayout?.();
        const ta = overlay.querySelector('#mod-config-editor');
        const bd = overlay.querySelector('#mod-config-highlight-backdrop');
        if (!ta || !bd) return;
        const sync = () => this._syncConfigEditorBackdropPad(overlay);
        ta.addEventListener('scroll', sync, { passive: true });
        const ro = new ResizeObserver(() => requestAnimationFrame(sync));
        ro.observe(ta);
        const wrap = ta.closest('.mod-config-editor-wrap');
        if (wrap) ro.observe(wrap);
        this._teardownConfigEditorLayout = () => {
            ta.removeEventListener('scroll', sync);
            ro.disconnect();
            this._teardownConfigEditorLayout = null;
        };
        requestAnimationFrame(sync);
    }

    _parseJsonRelaxed(text) {
        const raw = String(text ?? '');
        try {
            return { ok: true, value: JSON.parse(raw) };
        } catch (_) {
            try {
                let s = raw;
                s = s.replace(/\/\*[\s\S]*?\*\//g, '');
                s = s.replace(/(^|[^:\\])\/\/.*$/gm, '$1');
                s = s.replace(/,\s*([}\]])/g, '$1');
                return { ok: true, value: JSON.parse(s) };
            } catch (e2) {
                return { ok: false, error: (e2 && e2.message) ? e2.message : String(e2) };
            }
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
        if (this._modUpdateCheckInFlight) {
            this._modUpdateCheckPending = true;
            return;
        }
        this._modUpdateCheckInFlight = true;
        window.chrome.webview.postMessage({ type: 'CHECK_MOD_UPDATES' });
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
        if (this._modUpdateCheckPending) {
            this._modUpdateCheckPending = false;
            this.scheduleModUpdateCheck(300);
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
        if (dropdown) dropdown.hidden = true;
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
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
            if (!document.getElementById('btn-mods-actions')) return;
            if (!e.target.closest('.mods-actions-menu-wrap')) {
                this._closeModsActionsMenu();
            }
        }, { signal });

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

        document.getElementById('mods-action-backup-mods')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeModsActionsMenu();
            if (window.chrome?.webview?.postMessage) {
                window.chrome.webview.postMessage({ type: 'BACKUP_MODS' });
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
            document.body.addEventListener('click', async (e) => {
                const deleteBtn = e.target.closest('.btn-delete-mod');
                if (deleteBtn) {
                    e.preventDefault();
                    e.stopPropagation();
                    const name = deleteBtn.dataset.name;
                    if (name) {
                        await deleteModWithConfirmation(name);
                    }
                    return;
                }
            });
            _modsGlobalEventsAttached = true;
        }

        if (!window.handleDeleteMod) {
            window.handleDeleteMod = async (name) => {
                await deleteModWithConfirmation(name);
            };
        }
        
        if (!window.nuclearDelete) {
            window.nuclearDelete = async (el) => {
                const name = el.getAttribute('data-name');
                const msg = `[NUCLEAR] Deleting: ${name}`;
                console.log(msg);
                if (window.logToScreen) window.logToScreen(msg);
                await deleteModWithConfirmation(name);
            };
        }

        if (!window.nuclearEdit) {
            window.nuclearEdit = (el) => {
                const name = el.getAttribute('data-name');
                const openTab = el.getAttribute('data-open-tab') || 'general';
                console.log("[NUCLEAR] Edit requested for:", name, "tab:", openTab);
                this.showRenameModal(name, openTab);
            };
        }

        if (!window.nuclearModUpdate) {
            window.nuclearModUpdate = (el) => {
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
                editor.disabled = true;
                if (saveBtn) saveBtn.disabled = true;
                if (validateBtn) validateBtn.disabled = true;
                if (formatBtn) formatBtn.disabled = true;
                if (backdrop) backdrop.innerHTML = '\n';
                return;
            }

            editor.value = String(data.content ?? '');
            editor.disabled = false;
            setStatus('', 'neutral');

            if (backdrop) {
                let content = editor.value || '';
                content = content.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
                const highlighted = content.replace(/^([^=\s\n][^=\n]*=)/gm, (match) => {
                    const parts = match.split('=');
                    if (parts.length > 1) {
                        return `<span style="color: #6ed58a; font-weight: 500;">${parts[0]}</span>=`;
                    }
                    return match;
                });
                backdrop.innerHTML = highlighted + '\n';
                backdrop.scrollTop = editor.scrollTop;
                this._syncConfigEditorBackdropPad(overlay);
            }

            const isJson = String(data.relativePath || '').toLowerCase().endsWith('.json');
            if (saveBtn) saveBtn.disabled = false;
            if (validateBtn) validateBtn.disabled = !isJson;
            if (formatBtn) formatBtn.disabled = !isJson;
        } catch (e) {
            console.warn('[MODS] handleModFileContent failed', e);
        }
    }

    handleModFileWriteResult(data) {
        try {
            const overlay = document.getElementById('rename-mod-overlay');
            if (!overlay) return;
            const status = overlay.querySelector('#mod-config-file-status');
            if (!status) return;
            if (data.ok) {
                status.textContent = 'Saved.';
                status.className = 'mod-config-status mod-config-status--success';
            } else {
                const msg = String(data.error || 'Save failed.');
                status.textContent = msg;
                status.className = 'mod-config-status mod-config-status--error';
            }
        } catch (e) {
            console.warn('[MODS] handleModFileWriteResult failed', e);
        }
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
                    ? `Already part of ${tgt}.`
                    : `Adopted into ${tgt}.`;
                status.textContent = msg;
                status.className = 'mod-config-status mod-config-status--success';
            } else {
                const msg = String(data.error || 'Adopt failed.');
                status.textContent = msg;
                status.className = 'mod-config-status mod-config-status--error';
            }
        } catch (e) {
            console.warn('[MODS] handleModFileAdoptResult failed', e);
        }
    }

    hideRenameModal() {
        this._teardownConfigEditorLayout?.();
        const overlay = document.getElementById('rename-mod-overlay');
        if (overlay) {
            overlay.remove();
        }
        if (this.renameModalKeyHandler) {
            document.removeEventListener('keydown', this.renameModalKeyHandler);
            this.renameModalKeyHandler = null;
        }
    }

    showRenameModal(currentName, initialTab = 'general') {
        this.hideRenameModal();

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

        const editableConfigExts = new Set(['.json', '.txt', '.ini']);
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
        const isConfigRow = lower.endsWith('.ini') || lower.endsWith('.json') || lower.endsWith('.txt');

        const overlay = document.createElement('div');
        overlay.id = 'rename-mod-overlay';
        overlay.className = 'modal-overlay active rename-mod-overlay';

        if (isConfigRow) {
            const jsonMode = lower.endsWith('.json');
            const configSpellCheck = !!(window.app?.realData?.managerSettings?.configEditorSpellCheck);
            overlay.dataset.configRelativePath = normPath;
            overlay.innerHTML = `
                <div class="custom-modal polished rename-mod-modal rename-mod-modal-config" role="dialog" aria-modal="true" aria-label="${escapeHtml(editableName || window._t('mods'))}">
                    <div class="rename-mod-header">
                        <button type="button" class="close-status" id="rename-mod-close-top" aria-label="${escapeHtml(window._t('cancel'))}">&times;</button>
                    </div>
                    <div class="modal-body rename-mod-body">
                        <div class="config-editor-topbar">
                            <label class="rename-mod-label" for="rename-mod-input">File</label>
                            <div class="config-editor-toprow">
                                <input id="rename-mod-input" class="rename-mod-input" type="text" value="${escapeHtml(editableName)}" ${isCoreIni ? 'readonly' : ''} />
                                <div class="mod-config-actions mod-config-actions-inline config-editor-actions">
                                    ${jsonMode ? `<button type="button" class="btn-popup secondary mod-config-json-only" id="mod-config-validate" disabled>Validate JSON</button>` : ``}
                                    ${jsonMode ? `<button type="button" class="btn-popup secondary mod-config-json-only" id="mod-config-format" disabled>Format JSON</button>` : ``}
                                </div>
                            </div>
                        </div>

                        <div class="config-editor-main">
                            <div class="config-editor-pane config-editor-pane-left" role="group" aria-labelledby="rename-mod-config-heading">
                                <div class="rename-mod-type-heading" id="rename-mod-config-heading">Config file</div>
                                <p class="rename-mod-type-hint">Editing <code>${escapeHtml(editableName)}</code>.</p>
                                <div class="mod-config-single config-editor-single">
                                    <div class="mod-config-editor-wrap">
                                        <div id="mod-config-highlight-backdrop"></div>
                                        <textarea id="mod-config-editor" class="rename-mod-details mod-config-editor mod-config-editor-single" placeholder="Loading…" disabled spellcheck="${configSpellCheck}"></textarea>
                                    </div>
                                    <div id="mod-config-file-status" class="mod-config-status text-muted" aria-live="polite"></div>
                                </div>
                            </div>
                        </div>
                    </div>
                    <div class="modal-footer rename-mod-footer">
                        <div class="rename-mod-footer-spacer" aria-hidden="true"></div>
                        <button type="button" class="btn-popup secondary" id="rename-mod-cancel">${window._t('cancel')}</button>
                        ${isCoreIni ? `` : `<button type="button" class="btn-popup secondary" id="mod-config-rename" disabled><i data-lucide="edit-3"></i> Rename</button>`}
                        <button type="button" class="btn-popup primary" id="mod-config-save" disabled><i data-lucide="save"></i> Save file</button>
                    </div>
                </div>
            `;
        } else {
            overlay.innerHTML = `
                <div class="custom-modal polished rename-mod-modal" role="dialog" aria-modal="true" aria-label="${escapeHtml(editableName || window._t('mods'))}">
                    <div class="rename-mod-header">
                        <button type="button" class="close-status" id="rename-mod-close-top" aria-label="${escapeHtml(window._t('cancel'))}">&times;</button>
                    </div>
                    <nav class="rename-mod-tabs" role="tablist" aria-label="${escapeAttr(window._t('rename_mod_tabs_label'))}">
                        <button type="button" class="rename-mod-tab active" data-tab="general" role="tab" aria-selected="true" id="rename-mod-tab-general">${escapeHtml(window._t('rename_mod_tab_general'))}</button>
                        <button type="button" class="rename-mod-tab" data-tab="nexus" role="tab" aria-selected="false" id="rename-mod-tab-nexus">${escapeHtml(window._t('rename_mod_tab_nexus'))}</button>
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
                            <input id="rename-mod-nexus-mod-id" class="rename-mod-input" type="text" inputmode="numeric" placeholder="12345" value="${escapeAttr(currentMod?.nexusModId != null ? String(currentMod.nexusModId) : '')}" />
                            <label class="rename-mod-label" for="rename-mod-nexus-version">${window._t('rename_mod_nexus_version_label')}</label>
                            <input id="rename-mod-nexus-version" class="rename-mod-input" type="text" placeholder="1.0" value="${escapeAttr(currentMod?.nexusFileVersion || currentMod?.version || '')}" />
                            <label class="rename-mod-label" for="rename-mod-nexus-file-id">${window._t('rename_mod_nexus_file_id_label')}</label>
                            <input id="rename-mod-nexus-file-id" class="rename-mod-input" type="text" inputmode="numeric" placeholder="${escapeAttr(window._t('rename_mod_nexus_file_id_placeholder'))}" value="${escapeAttr(currentMod?.nexusFileId != null ? String(currentMod.nexusFileId) : '')}" />
                            <label class="rename-mod-label" for="rename-mod-nexus-uploaded">${window._t('rename_mod_nexus_uploaded_label')}</label>
                            <input id="rename-mod-nexus-uploaded" class="rename-mod-input" type="text" inputmode="numeric" placeholder="${escapeAttr(window._t('rename_mod_nexus_uploaded_placeholder'))}" value="${escapeAttr(currentMod?.nexusFileUploaded != null ? String(currentMod.nexusFileUploaded) : '')}" />
                            <p class="rename-mod-type-hint">${escapeHtml(window._t('rename_mod_nexus_file_id_hint'))}</p>
                        </div>
                    </div>
                    <div class="modal-footer rename-mod-footer">
                        <button type="button" class="btn-popup secondary" id="rename-mod-cancel">${window._t('cancel')}</button>
                        <button type="button" class="btn-popup primary" id="rename-mod-confirm"><i data-lucide="save"></i> ${window._t('rename_mod_save')}</button>
                    </div>
                </div>
            `;
        }

        document.body.appendChild(overlay);
        if (window.lucide) window.lucide.createIcons();

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
            };
            tabBtns.forEach((btn) => {
                btn.addEventListener('click', () => switchRenameTab(btn.dataset.tab));
            });
            const tabToOpen = initialTab === 'nexus' ? 'nexus' : 'general';
            switchRenameTab(tabToOpen);
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
        const initialNexusUrl = (currentMod?.url || '').trim();
        const initialNexusModId = currentMod?.nexusModId != null ? String(currentMod.nexusModId) : '';
        const initialNexusVersion = (currentMod?.nexusFileVersion || currentMod?.version || '').trim();
        const initialNexusFileId = currentMod?.nexusFileId != null ? String(currentMod.nexusFileId) : '';
        const initialNexusUploaded = currentMod?.nexusFileUploaded != null ? String(currentMod.nexusFileUploaded) : '';
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
            if (!modId && url && url !== initialNexusUrl) {
                modId = parseNexusModIdFromUrl(url);
            }
            return { url, modIdEntered, modId, version, fileId, uploaded };
        };

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
            let content = configEditor.value || '';
            content = content.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
            const highlighted = content.replace(/^([^=\s\n][^=\n]*=)/gm, (match) => {
                const parts = match.split('=');
                if (parts.length > 1) {
                    return `<span style="color: #6ed58a; font-weight: 500;">${parts[0]}</span>=`;
                }
                return match;
            });
            configBackdrop.innerHTML = highlighted + '\n';
            configBackdrop.scrollTop = configEditor.scrollTop;
            this._syncConfigEditorBackdropPad(overlay);
        };

        const isValidConfigFileBaseName = (value) => {
            const v = String(value ?? '').trim();
            if (!v) return { ok: false, value: '', error: 'Enter a file name.' };
            if (/[\\/]/.test(v)) return { ok: false, value: v, error: 'Filename only (no folders or slashes).' };
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
            return lp.endsWith('.ini') || lp.endsWith('.json') || lp.endsWith('.txt');
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
                configEditor.disabled = true;
                enableConfigButtons(false);
                updateConfigHighlighting();
                if (!rel) {
                    setConfigStatus('');
                    return;
                }
                setConfigStatus('Loading…');
                window.chrome.webview.postMessage({ type: 'READ_MOD_FILE', name: name, relativePath: rel });
            });
            enableConfigButtons(false);
        }

        if (configValidate && configEditor) {
            configValidate.addEventListener('click', () => {
                try {
                    const parsed = this._parseJsonRelaxed(configEditor.value || '');
                    if (parsed.ok) setConfigStatus('JSON is valid.', 'success');
                    else setConfigStatus(`Invalid JSON: ${parsed.error}`, 'error');
                } catch (e) {
                    setConfigStatus(`Invalid JSON: ${e.message}`, 'error');
                }
            });
        }

        if (configFormat && configEditor) {
            configFormat.addEventListener('click', () => {
                try {
                    const parsed = this._parseJsonRelaxed(configEditor.value || '');
                    if (!parsed.ok) {
                        setConfigStatus(`Invalid JSON: ${parsed.error}`, 'error');
                        return;
                    }
                    configEditor.value = JSON.stringify(parsed.value, null, 2);
                    updateConfigHighlighting();
                    setConfigStatus('Formatted JSON.', 'success');
                } catch (e) {
                    setConfigStatus(`Invalid JSON: ${e.message}`, 'error');
                }
            });
        }

        if (configSave && configEditor) {
            configSave.addEventListener('click', () => {
                const rel = getCurrentConfigRelativePath();
                if (!rel) return;
                setConfigStatus('Saving…');
                window.chrome.webview.postMessage({ type: 'WRITE_MOD_FILE', name: name, relativePath: rel, content: configEditor.value ?? '' });
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
                setConfigStatus('Renaming…');
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
            setConfigStatus('Loading…');
            window.chrome.webview.postMessage({ type: 'READ_MOD_FILE', name: name, relativePath: normPath });
        }

        if (configEditor) {
            configEditor.addEventListener('input', () => updateConfigHighlighting());
            updateConfigHighlighting();
        }
        if (isConfigRow) {
            this._setupConfigEditorLayout(overlay);
        }

        if (cancelBtn) cancelBtn.onclick = closeAction;
        if (closeTopBtn) closeTopBtn.onclick = closeAction;
        if (confirmBtn) confirmBtn.onclick = submitAction;
        if (renameInput && confirmBtn) renameInput.oninput = updateConfirmState;
        if (detailsInput) detailsInput.oninput = updateConfirmState;
        if (nexusUrlInput) nexusUrlInput.oninput = updateConfirmState;
        if (nexusModIdInput) nexusModIdInput.oninput = updateConfirmState;
        if (nexusVersionInput) nexusVersionInput.oninput = updateConfirmState;
        if (nexusFileIdInput) nexusFileIdInput.oninput = updateConfirmState;
        if (nexusUploadedInput) nexusUploadedInput.oninput = updateConfirmState;

        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) closeAction();
        });

        this.renameModalKeyHandler = (e) => {
            if (e.key === 'Escape') {
                e.preventDefault();
                closeAction();
                return;
            }
            const ae = document.activeElement;
            const inTabs = ae && ae.closest && ae.closest('.rename-mod-tabs');
            const isMultilineEditor = ae && (
                ae === detailsInput ||
                ae === configEditor ||
                ae.tagName === 'TEXTAREA' ||
                ae.isContentEditable
            );
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
            row.style.display = !term || searchText.includes(term) ? '' : 'none';
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
        if (this.tryInPlaceUpdate(data)) {
            this._applyUpdateFlagsToVisibleRows();
            if (this._shouldRecheckModUpdates(priorData, data)) {
                this.scheduleModUpdateCheck();
            }
            return;
        }
        this.refreshUI();
    }

    tryInPlaceUpdate(data) {
        if (!this.data) return false;
        
        const cleanName = n => n ? n.replace(/^Disabled\//, '') : '';
        const fileKeyOf = (n) => this._modFileKeyFromOriginalName(n);
        const expected = this.getSortedFilteredMods(data);
        const currentRows = Array.from(document.querySelectorAll('.mod-row'));
        
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
                unmatched[0].remove();
                this.data = data;
                this.updateDeployButtonText();
                this.updateSelectAllCheckbox(expected);
                return true;
            }
        }

        if (expected.length > currentRows.length) {
            const tbody = document.getElementById('mods-list-body');
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
                temp.innerHTML = ModsRenderer.renderModRow(mod).trim();
                const newRow = temp.querySelector('tr');
                if (!newRow) return false;

                const rows = Array.from(tbody.querySelectorAll('.mod-row'));
                let insertBefore = null;
                for (let i = modIndex + 1; i < expected.length; i++) {
                    const anchor = rows.find((r) => rowMatchesMod(r, expected[i]));
                    if (anchor) {
                        insertBefore = anchor;
                        break;
                    }
                }
                tbody.insertBefore(newRow, insertBefore);
                if (window.lucide) window.lucide.createIcons({ nodes: [newRow] });
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
            if (original.endsWith('.ini') || original.endsWith('.json') || original.endsWith('.txt')) return false;

            const matchesSearch = (m.name || "").toLowerCase().includes(searchTerm) ||
                (m.originalName || "").toLowerCase().includes(searchTerm);
            if (!matchesSearch) return false;

            if (m.isBundle && m.status === 'disabled') return false;

            if (this.currentPreset === 'uncategorized') {
                return !m.group;
            } else if (this.currentPreset !== 'all') {
                return m.group === this.currentPreset;
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
                const selectedMods = Array.from(document.querySelectorAll('.mod-row'))
                    .filter(tr => tr.querySelector('.mod-select').checked)
                    .map(tr => tr.dataset.name);
                window.chrome.webview.postMessage({
                    type: 'DEPLOY_ALL',
                    force: false,
                    mods: selectedMods
                });
            });
        }

        const presetSelect = document.getElementById('preset-select');
        if (presetSelect) {
            presetSelect.addEventListener('change', (e) => {
                const val = e.target.value;
                if (val === '__NEW__') {
                    const name = prompt(window._t('new_preset_prompt'));
                    if (name) {
                        this.modGroupsManager.createGroup(name);
                        this.currentPreset = name;
                        this.refreshUI();
                    } else {
                        presetSelect.value = this.currentPreset;
                    }
                } else {
                    this.currentPreset = val;
                    this.refreshUI();
                }
            });
        }

        const btnDeletePreset = document.getElementById('btn-delete-preset');
        if (btnDeletePreset) {
            btnDeletePreset.addEventListener('click', async () => {
                const name = this.currentPreset;
                if (name === 'all' || name === 'uncategorized') return;

                const ok = await window.appConfirm({
                    title: 'Confirmation',
                    message: window._t('delete_preset_confirm', name),
                    okText: window._t('delete'),
                    cancelText: window._t('cancel'),
                    danger: true
                });
                if (ok) {
                    this.modGroupsManager.deleteGroup(name);
                    this.currentPreset = 'all';
                    this.refreshUI();
                }
            });
        }

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
    }
}