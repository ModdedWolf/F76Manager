import { BundleRenderer } from './BundleRenderer.js';
let _bundleEventsAttached = false;
let _bundleIpcHandlerAttached = false;

function getBundleFormatLabel(value) {
    return value === 'DDS'
        ? window._t('bundle_format_dds')
        : window._t('bundle_format_general');
}

export class BundleManager {
    constructor() {
        this.data = null;
        this.isCreating = false;
        this.selectedMods = new Set();
        this.externalFiles = new Set();
        this.selectedFolders = new Set();
        this.bundleName = 'MyCustomBundle';
        this.isPickingInternal = false;
        this.inspectedBa2Path = '';
        this.inspectedFolderPath = '';
        this.ba2Contents = [];
        this.inspectorArchiveType = '';
        this.inspectorFileCount = 0;
        this.gameBa2List = [];
        this.ba2BrowseTarget = 'external';
        this.inspectorLoading = false;
        this.inspectorExpandedDirs = new Set();
        this.inspectorFilter = '';
        this.ba2Selections = new Map();
        this.ba2TotalPaths = new Map();
        this.ba2AllPaths = new Map();
        this.gameDataPath = '';
        this.pendingAutoInspectPath = '';
        this.pendingAutoInspectFolderPath = '';
        this.inspectorLoadTimeoutId = null;
        this.inspectorLoadRequestPath = '';
        this.inspectorLoadRequestMode = 'ba2';
        this.initGlobalHandlers();
        this.initIpcHandlers();
    }

    render(data) {
        this.data = data;
        return BundleRenderer.render(this, data);
    }

    onMount(data) {
        this.data = data;
        this.ensureIcons();
        
        const showWizardBtn = document.getElementById('btn-show-wizard');
        const createFirstBtn = document.getElementById('btn-create-bundle');
        const cancelBtn = document.getElementById('btn-cancel-wizard');
        
        if (showWizardBtn) showWizardBtn.onclick = () => { this.isCreating = true; this.refresh(); };
        if (createFirstBtn) createFirstBtn.onclick = () => { this.isCreating = true; this.refresh(); };
        if (cancelBtn) cancelBtn.onclick = () => {
            this.isCreating = false;
            this.selectedMods.clear();
            this.externalFiles.clear();
            this.selectedFolders.clear();
            this.ba2Selections.clear();
            this.ba2TotalPaths.clear();
            this.ba2AllPaths.clear();
            this.resetInspector();
            this.refresh();
        };

        if (this.isCreating) {
            const pickerList = document.querySelector('.picker-list');
            const scrollTop = pickerList ? pickerList.scrollTop : 0;

            this.setupWizardEvents();
            this.setupDropZone();
            this.syncInspectorWithStaged();
            this.loadGameBa2List();

            if (pickerList) {
                const newList = document.querySelector('.picker-list');
                if (newList) newList.scrollTop = scrollTop;
            }

            this.updateWizardState();

            if (this.pendingAutoInspectPath) {
                const path = this.pendingAutoInspectPath;
                this.pendingAutoInspectPath = '';
                this.inspectBa2(path);
            } else if (this.pendingAutoInspectFolderPath) {
                const path = this.pendingAutoInspectFolderPath;
                this.pendingAutoInspectFolderPath = '';
                this.inspectFolder(path);
            }
        }
    }

    getTotalStagedCount() {
        return this.selectedMods.size + this.externalFiles.size + this.selectedFolders.size;
    }

    wouldProduceBundlePayload() {
        for (const folder of this.selectedFolders) {
            const key = this.normalizeFolderKey(folder);
            if (!this.ba2TotalPaths.has(key)) return true;
            if (this.getSelectedCount(key) > 0) return true;
        }
        for (const src of this.collectBa2Sources()) {
            if (!this.ba2TotalPaths.has(src.key)) return true;
            if (this.getSelectedCount(src.key) > 0) return true;
        }
        return false;
    }

    setupDropZone() {
        const dropZone = document.getElementById('staged-drop-zone');
        if (!dropZone) return;

        if (this._dropZoneAbort) {
            this._dropZoneAbort.abort();
        }
        this._dropZoneAbort = new AbortController();
        const { signal } = this._dropZoneAbort;

        dropZone.addEventListener('dragover', (e) => {
            e.preventDefault();
            e.stopPropagation();
            e.dataTransfer.dropEffect = 'copy';
            if (!dropZone.classList.contains('drag-active')) {
                dropZone.classList.add('drag-active');
            }
        }, { signal });

        dropZone.addEventListener('dragenter', (e) => {
            e.preventDefault();
            e.stopPropagation();
            dropZone.classList.add('drag-active');
        }, { signal });

        dropZone.addEventListener('dragleave', (e) => {
            e.preventDefault();
            e.stopPropagation();
            if (e.relatedTarget === null || !dropZone.contains(e.relatedTarget)) {
                dropZone.classList.remove('drag-active');
            }
        }, { signal });

        dropZone.addEventListener('drop', (e) => {
            e.preventDefault();
            e.stopPropagation();
            dropZone.classList.remove('drag-active');

            const files = e.dataTransfer.files;
            if (files && files.length > 0) {
                let lastAdded = '';
                for (const file of files) {
                    if (file.name.toLowerCase().endsWith('.ba2')) {
                        const path = file.path || file.name;
                        this.externalFiles.add(path);
                        lastAdded = path;
                        if (window.chrome?.webview) {
                            window.chrome.webview.postMessage({ type: 'JS_LOG', message: `[BUNDLE] External file added: ${path}` });
                        }
                    } else if (window.chrome?.webview) {
                        window.chrome.webview.postMessage({ type: 'JS_LOG', message: `[BUNDLE] Rejected non-ba2 file: ${file.name}` });
                    }
                }
                if (lastAdded) {
                    this.pendingAutoInspectPath = lastAdded;
                }
                this.refresh();
            } else if (window.chrome?.webview) {
                window.chrome.webview.postMessage({ type: 'JS_LOG', message: '[BUNDLE] Drop event fired but no files found.' });
            }
        }, { signal });
    }

    promptAddExternalBa2() {
        if (!window.chrome?.webview) return;
        this._closeBundleActionsMenu();
        this.ba2BrowseTarget = 'external';
        window.chrome.webview.postMessage({
            type: 'BROWSE_FILE',
            title: window._t('select_external_ba2_title'),
            filter: 'Fallout 76 Archives (*.ba2)|*.ba2',
            initialDirectory: this.gameDataPath || ''
        });
    }

    promptAddFolder() {
        if (!window.chrome?.webview) return;
        this._closeBundleActionsMenu();
        window.chrome.webview.postMessage({ type: 'BUNDLE_BROWSE_FOLDER' });
    }

    openInternalModPicker() {
        this._closeBundleActionsMenu();
        this.isPickingInternal = true;
        this.refresh();
    }

    bindStagedListEvents(root = document) {
        root.querySelectorAll('.btn-remove-staged').forEach(btn => {
            btn.onclick = (e) => {
                e.stopPropagation();
                const name = btn.dataset.name;
                const type = btn.dataset.type;
                if (type === 'internal') this.selectedMods.delete(name);
                else if (type === 'folder') this.selectedFolders.delete(name);
                else this.externalFiles.delete(name);
                this.syncInspectorWithStaged();
                this.refresh();
            };
        });

        root.querySelectorAll('.file-entry-staged-ba2').forEach(row => {
            row.onclick = (e) => {
                if (e.target.closest('.btn-remove-staged')) return;
                if (e.target.closest('.ba2-tree-check')) return;
                const path = row.dataset.path;
                if (path) this.inspectBa2(path);
            };
        });

        root.querySelectorAll('.file-entry-staged-folder').forEach(row => {
            row.onclick = (e) => {
                if (e.target.closest('.btn-remove-staged')) return;
                const path = row.dataset.path;
                if (path) this.inspectFolder(path);
            };
        });
    }

    setupFileEntryEvents(stagedRoot = document) {
        this.bindStagedListEvents(stagedRoot);
    }

    _closeFileEntryContextMenu() {
        document.querySelectorAll('.file-entry-context-menu').forEach(menu => menu.remove());
    }

    clearWorkspace() {
        this._closeBundleActionsMenu();
        this._closeFileEntryContextMenu();
        this.selectedMods.clear();
        this.externalFiles.clear();
        this.selectedFolders.clear();
        this.ba2Selections.clear();
        this.ba2TotalPaths.clear();
        this.ba2AllPaths.clear();
        this.resetInspector();
        this.refresh();
    }

    setupFileEntryContextMenu() {
        const root = document.getElementById('ba2-inspector-root');
        if (!root) return;

        if (this._fileEntryContextAbort) {
            this._fileEntryContextAbort.abort();
        }
        this._fileEntryContextAbort = new AbortController();
        const { signal } = this._fileEntryContextAbort;

        root.addEventListener('contextmenu', (e) => {
            const archiveKey = this.getArchiveSelectionKey();
            const isInspecting = Boolean(this.inspectedBa2Path || this.inspectedFolderPath);
            if (!archiveKey || !isInspecting || this.inspectorLoading) return;
            if (!this.ba2Contents?.length && !this.ba2TotalPaths.has(archiveKey)) return;

            e.preventDefault();
            e.stopPropagation();
            this._closeFileEntryContextMenu();

            const { all, none } = this.getArchiveSelectionMenuState();
            const canExtract = this.canExtractSelection();
            const isBa2 = Boolean(this.inspectedBa2Path);
            const menu = document.createElement('div');
            menu.className = 'bundle-menu-dropdown file-entry-context-menu';
            menu.innerHTML = `
                <div class="bundle-menu-item${all ? ' bundle-menu-item--muted' : ''}" data-action="select-all">
                    <i data-lucide="list-checks"></i>
                    <span>${window._t('inspector_select_all')}</span>
                </div>
                <div class="bundle-menu-item${none ? ' bundle-menu-item--muted' : ''}" data-action="deselect-all">
                    <i data-lucide="list-minus"></i>
                    <span>${window._t('inspector_deselect_all')}</span>
                </div>
                ${isBa2 ? `
                <div class="bundle-menu-item${canExtract ? '' : ' bundle-menu-item--muted'}" data-action="extract">
                    <i data-lucide="folder-open"></i>
                    <span>${window._t('bundle_extract_to_folder')}</span>
                </div>` : ''}
                <div class="bundle-menu-divider" role="separator"></div>
                <div class="bundle-menu-item danger" data-action="clear-workspace">
                    <i data-lucide="trash-2"></i>
                    <span>${window._t('clear_workspace')}</span>
                </div>
            `;
            document.body.appendChild(menu);
            if (window.lucide) window.lucide.createIcons();

            menu.style.position = 'fixed';
            menu.style.left = `${e.clientX}px`;
            menu.style.top = `${e.clientY}px`;

            const selectAllItem = menu.querySelector('[data-action="select-all"]');
            const deselectAllItem = menu.querySelector('[data-action="deselect-all"]');
            const extractItem = menu.querySelector('[data-action="extract"]');
            const clearWorkspaceItem = menu.querySelector('[data-action="clear-workspace"]');

            if (selectAllItem) {
                selectAllItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.selectAllForArchive(archiveKey);
                    this.afterSelectionChange();
                    this._closeFileEntryContextMenu();
                };
            }

            if (deselectAllItem) {
                deselectAllItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.deselectAllForArchive(archiveKey);
                    this.afterSelectionChange();
                    this._closeFileEntryContextMenu();
                };
            }

            if (extractItem) {
                extractItem.onclick = (ev) => {
                    ev.stopPropagation();
                    if (!this.canExtractSelection()) return;
                    this.promptExtractToFolder();
                };
            }

            if (clearWorkspaceItem) {
                clearWorkspaceItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.clearWorkspace();
                };
            }

            const closeOnDismiss = (ev) => {
                if (!menu.contains(ev.target)) {
                    this._closeFileEntryContextMenu();
                }
            };

            setTimeout(() => {
                document.addEventListener('click', closeOnDismiss, { signal });
                document.addEventListener('contextmenu', closeOnDismiss, { signal });
                document.addEventListener('scroll', () => this._closeFileEntryContextMenu(), { signal, capture: true });
                document.addEventListener('keydown', (ev) => {
                    if (ev.key === 'Escape') this._closeFileEntryContextMenu();
                }, { signal });
            }, 0);
        }, { signal });
    }

    normalizeBa2InternalPath(path) {
        return String(path || '').replace(/\\/g, '/').toLowerCase();
    }

    normalizeArchiveKey(path) {
        const resolved = this.resolveArchivePath(path);
        return resolved.toLowerCase().replace(/\\/g, '/');
    }

    normalizeFolderKey(path) {
        return String(path || '').toLowerCase().replace(/\\/g, '/');
    }

    resolveArchivePath(modOrPath) {
        if (!modOrPath) return '';
        if (/^[a-zA-Z]:[\\/]/.test(modOrPath) || modOrPath.startsWith('\\\\')) return modOrPath;
        const base = this.gameDataPath || '';
        if (!base) return modOrPath;
        const sep = base.includes('\\') ? '\\' : '/';
        return `${base.replace(/[\\/]+$/, '')}${sep}${modOrPath.replace(/^[\\/]+/, '')}`;
    }

    getArchiveSelectionKey() {
        if (this.inspectedFolderPath) return this.normalizeFolderKey(this.inspectedFolderPath);
        if (this.inspectedBa2Path) return this.normalizeArchiveKey(this.inspectedBa2Path);
        return '';
    }

    getSelectionForArchive(archiveKey) {
        if (!archiveKey) return new Set();
        return this.ba2Selections.get(archiveKey) || new Set();
    }

    getSelectedCount(archiveKey) {
        return this.getSelectionForArchive(archiveKey).size;
    }

    isAllSelected(archiveKey) {
        if (this.inspectorFilter && archiveKey === this.getArchiveSelectionKey()) {
            const filteredTotal = this.getFilteredEntryCount();
            if (!filteredTotal) return false;
            return this.getFilteredSelectedCount(archiveKey) >= filteredTotal;
        }
        const total = this.ba2TotalPaths.get(archiveKey) || 0;
        if (!total) return false;
        return this.getSelectedCount(archiveKey) >= total;
    }

    pathMatchesInspectorFilter(normPath) {
        if (!this.inspectorFilter) return true;
        return normPath.includes(this.inspectorFilter.toLowerCase());
    }

    getEffectiveSelectionPaths(archiveKey) {
        const selected = this.getSelectionForArchive(archiveKey);
        const applyFilter = this.inspectorFilter && archiveKey === this.getArchiveSelectionKey();
        if (!applyFilter) return [...selected];
        return [...selected].filter(p => this.pathMatchesInspectorFilter(p));
    }

    getFilteredEntryCount() {
        if (!this.inspectorFilter) {
            return this.ba2TotalPaths.get(this.getArchiveSelectionKey()) || this.ba2Contents.length;
        }
        const filter = this.inspectorFilter.toLowerCase();
        let count = 0;
        for (const entry of this.ba2Contents) {
            if (this.normalizeBa2InternalPath(entry.path).includes(filter)) count++;
        }
        return count;
    }

    getFilteredSelectedCount(archiveKey) {
        if (!this.inspectorFilter || archiveKey !== this.getArchiveSelectionKey()) {
            return this.getSelectedCount(archiveKey);
        }
        let count = 0;
        for (const path of this.getSelectionForArchive(archiveKey)) {
            if (this.pathMatchesInspectorFilter(path)) count++;
        }
        return count;
    }

    initArchiveSelection(archiveKey, entries) {
        if (this.ba2Selections.has(archiveKey)) return;
        const allPaths = new Set();
        for (const entry of entries) {
            allPaths.add(this.normalizeBa2InternalPath(entry.path));
        }
        this.ba2Selections.set(archiveKey, new Set(allPaths));
        this.ba2AllPaths.set(archiveKey, allPaths);
        this.ba2TotalPaths.set(archiveKey, allPaths.size);
    }

    selectAllForArchive(archiveKey) {
        if (!this.inspectorFilter || archiveKey !== this.getArchiveSelectionKey()) {
            const allPaths = this.ba2AllPaths.get(archiveKey);
            if (allPaths) {
                this.ba2Selections.set(archiveKey, new Set(allPaths));
            }
            return;
        }
        const set = new Set(this.getSelectionForArchive(archiveKey));
        for (const entry of this.ba2Contents) {
            const norm = this.normalizeBa2InternalPath(entry.path);
            if (this.pathMatchesInspectorFilter(norm)) set.add(norm);
        }
        this.ba2Selections.set(archiveKey, set);
    }

    deselectAllForArchive(archiveKey) {
        if (!this.inspectorFilter || archiveKey !== this.getArchiveSelectionKey()) {
            this.ba2Selections.set(archiveKey, new Set());
            return;
        }
        const set = new Set(this.getSelectionForArchive(archiveKey));
        for (const entry of this.ba2Contents) {
            const norm = this.normalizeBa2InternalPath(entry.path);
            if (this.pathMatchesInspectorFilter(norm)) set.delete(norm);
        }
        this.ba2Selections.set(archiveKey, set);
    }

    toggleFileSelection(archiveKey, filePath, checked) {
        const set = new Set(this.getSelectionForArchive(archiveKey));
        if (checked) set.add(filePath);
        else set.delete(filePath);
        this.ba2Selections.set(archiveKey, set);
    }

    toggleFolderSelection(archiveKey, folderPath, checked) {
        const set = new Set(this.getSelectionForArchive(archiveKey));
        const prefix = folderPath ? `${folderPath}/` : '';
        for (const entry of this.ba2Contents) {
            const norm = this.normalizeBa2InternalPath(entry.path);
            const underFolder = prefix
                ? norm.startsWith(prefix) || norm === folderPath.toLowerCase()
                : true;
            if (!underFolder) continue;
            if (!this.pathMatchesInspectorFilter(norm)) continue;
            if (checked) set.add(norm);
            else set.delete(norm);
        }
        this.ba2Selections.set(archiveKey, set);
    }

    getStagedBa2Sources() {
        const sources = [];
        for (const mod of this.selectedMods) {
            const path = this.resolveArchivePath(mod);
            sources.push({
                path,
                inspectPath: path,
                modRef: mod,
                name: mod.split(/[\\/]/).pop() || mod
            });
        }
        for (const file of this.externalFiles) {
            sources.push({
                path: file,
                inspectPath: file,
                modRef: file,
                name: file.split(/[\\/]/).pop() || file
            });
        }
        return sources;
    }

    isStagedFolderPath(path) {
        if (!path) return false;
        const key = this.normalizeFolderKey(path);
        for (const folder of this.selectedFolders) {
            if (this.normalizeFolderKey(folder) === key) return true;
        }
        return false;
    }

    isStagedBa2Path(path) {
        if (!path) return false;
        const key = this.normalizeArchiveKey(path);
        for (const src of this.getStagedBa2Sources()) {
            if (this.normalizeArchiveKey(src.path) === key) return true;
            if (this.normalizeArchiveKey(src.inspectPath) === key) return true;
            if (this.normalizeArchiveKey(src.modRef) === key) return true;
        }
        for (const mod of this.selectedMods) {
            if (this.normalizeArchiveKey(mod) === key) return true;
            if (this.normalizeArchiveKey(this.resolveArchivePath(mod)) === key) return true;
        }
        for (const file of this.externalFiles) {
            if (this.normalizeArchiveKey(file) === key) return true;
        }
        return false;
    }

    syncInspectorWithStaged() {
        if (this.inspectedBa2Path && !this.isStagedBa2Path(this.inspectedBa2Path)) {
            this.resetInspector();
            return;
        }
        if (this.inspectedFolderPath && !this.isStagedFolderPath(this.inspectedFolderPath)) {
            this.resetInspector();
        }
    }

    collectBa2Sources() {
        const sources = [];
        const seen = new Set();

        const addSource = (path, modRef) => {
            const key = this.normalizeArchiveKey(path);
            if (seen.has(key)) return;
            seen.add(key);
            sources.push({ key, path: this.resolveArchivePath(path), modRef: modRef || path });
        };

        for (const mod of this.selectedMods) addSource(mod, mod);
        for (const file of this.externalFiles) addSource(file, file);

        return sources;
    }

    canCreateBundle() {
        return this.wouldProduceBundlePayload();
    }

    canUseArchiveSelection() {
        const key = this.getArchiveSelectionKey();
        if (!key || this.inspectorLoading) return false;
        if (!this.inspectedBa2Path && !this.inspectedFolderPath) return false;
        return Boolean(this.ba2Contents?.length || this.ba2TotalPaths.has(key));
    }

    canExtractSelection() {
        if (!this.inspectedBa2Path) return false;
        if (!this.canUseArchiveSelection()) return false;
        const archiveKey = this.getArchiveSelectionKey();
        return this.getEffectiveSelectionPaths(archiveKey).length > 0;
    }

    getArchiveSelectionMenuState() {
        const key = this.getArchiveSelectionKey();
        if (!key) return { all: false, none: true };
        const hasFilter = Boolean(this.inspectorFilter);
        const count = hasFilter ? this.getFilteredSelectedCount(key) : this.getSelectedCount(key);
        const total = hasFilter ? this.getFilteredEntryCount() : (this.ba2TotalPaths.get(key) || 0);
        return { all: total > 0 && count >= total, none: count === 0 };
    }

    _updateSelectionMenuItemStates() {
        const { all, none } = this.getArchiveSelectionMenuState();
        const menuSelectAllBtn = document.getElementById('btn-menu-select-all');
        const menuDeselectAllBtn = document.getElementById('btn-menu-deselect-all');
        if (menuSelectAllBtn) {
            menuSelectAllBtn.classList.toggle('mods-actions-item--muted', all);
        }
        if (menuDeselectAllBtn) {
            menuDeselectAllBtn.classList.toggle('mods-actions-item--muted', none);
        }
    }

    buildCreateBundlePayload(bundleName) {
        const compressionSelect = document.getElementById('bundle-compression-select');
        const compression = compressionSelect ? compressionSelect.value : 'Default';
        const formatSelect = document.getElementById('bundle-format-select');
        const format = formatSelect ? formatSelect.value : 'Auto';

        const fullMods = [];
        const ba2Partial = [];

        for (const src of this.collectBa2Sources()) {
            if (!this.ba2TotalPaths.has(src.key)) {
                fullMods.push(src.modRef);
                continue;
            }

            const effectivePaths = this.getEffectiveSelectionPaths(src.key);
            if (effectivePaths.length === 0) continue;

            const totalInArchive = this.ba2TotalPaths.get(src.key) || 0;
            const isFullArchive = effectivePaths.length >= totalInArchive
                && !(this.inspectorFilter && src.key === this.getArchiveSelectionKey());

            if (isFullArchive) {
                fullMods.push(src.modRef);
            } else {
                ba2Partial.push({
                    archive: src.path,
                    paths: effectivePaths
                });
            }
        }

        const fullFolders = [];
        const folderPartial = [];

        for (const folder of this.selectedFolders) {
            const key = this.normalizeFolderKey(folder);
            if (!this.ba2TotalPaths.has(key)) {
                fullFolders.push(folder);
                continue;
            }

            const effectivePaths = this.getEffectiveSelectionPaths(key);
            if (effectivePaths.length === 0) continue;

            const totalInFolder = this.ba2TotalPaths.get(key) || 0;
            const isFullFolder = effectivePaths.length >= totalInFolder
                && !(this.inspectorFilter && key === this.getArchiveSelectionKey());

            if (isFullFolder) {
                fullFolders.push(folder);
            } else {
                folderPartial.push({
                    folder,
                    paths: effectivePaths
                });
            }
        }

        const payload = {
            type: 'CREATE_BUNDLE',
            name: bundleName,
            mods: fullMods,
            ba2Partial,
            folders: fullFolders,
            folderPartial,
            compression,
            format
        };

        if (payload.mods.length === 0 && payload.ba2Partial.length === 0 && payload.folders.length === 0 && payload.folderPartial.length === 0) {
            if (window.chrome?.webview) {
                window.chrome.webview.postMessage({
                    type: 'JS_LOG',
                    message: '[BUNDLE] Create payload empty — no packable sources (check BA2 file selections).'
                });
            }
        }

        return payload;
    }

    buildExtractPayload() {
        const archiveKey = this.getArchiveSelectionKey();
        if (!archiveKey || !this.inspectedBa2Path) return null;

        const effectivePaths = this.getEffectiveSelectionPaths(archiveKey);
        if (effectivePaths.length === 0) return null;

        const totalInArchive = this.ba2TotalPaths.get(archiveKey) || 0;
        const payload = {
            type: 'BUNDLE_EXTRACT',
            archive: this.resolveArchivePath(this.inspectedBa2Path)
        };

        if (effectivePaths.length < totalInArchive) {
            payload.paths = effectivePaths;
        }

        return payload;
    }

    promptExtractToFolder() {
        if (!window.chrome?.webview) return;
        this._closeBundleActionsMenu();
        this._closeFileEntryContextMenu();

        const payload = this.buildExtractPayload();
        if (!payload) {
            window.chrome.webview.postMessage({
                type: 'STATUS',
                status: { type: 'error', text: window._t('bundle_nothing_to_extract') }
            });
            return;
        }

        window.chrome.webview.postMessage(payload);
    }

    resetInspector() {
        this.clearInspectorLoadTimeout();
        this.inspectedBa2Path = '';
        this.inspectedFolderPath = '';
        this.ba2Contents = [];
        this.inspectorArchiveType = '';
        this.inspectorFileCount = 0;
        this.inspectorLoading = false;
        this.inspectorExpandedDirs = new Set();
        this.inspectorFilter = '';
    }

    clearInspectorLoadTimeout() {
        if (this.inspectorLoadTimeoutId != null) {
            clearTimeout(this.inspectorLoadTimeoutId);
            this.inspectorLoadTimeoutId = null;
        }
        this.inspectorLoadRequestPath = '';
        this.inspectorLoadRequestMode = 'ba2';
    }

    scheduleInspectorLoadTimeout(path, mode = 'ba2') {
        this.clearInspectorLoadTimeout();
        const requestPath = path;
        this.inspectorLoadRequestPath = requestPath;
        this.inspectorLoadRequestMode = mode;
        this.inspectorLoadTimeoutId = setTimeout(() => {
            this.inspectorLoadTimeoutId = null;
            if (!this.inspectorLoading) return;
            if (mode === 'folder') {
                if (this.normalizeFolderKey(this.inspectedFolderPath) !== this.normalizeFolderKey(requestPath)) return;
            } else if (this.normalizeArchiveKey(this.inspectedBa2Path) !== this.normalizeArchiveKey(requestPath)) return;
            this.inspectorLoading = false;
            this.updateInspectorPanel({ preserveScroll: false });
            if (window.chrome?.webview) {
                window.chrome.webview.postMessage({
                    type: 'STATUS',
                    status: { type: 'error', text: window._t('inspector_load_timeout') }
                });
            }
        }, 60000);
    }

    ensureDefaultExpandedDirs() {
        if (this.inspectorExpandedDirs.size > 0 || !this.ba2Contents?.length) return;
        for (const entry of this.ba2Contents) {
            const path = String(entry.path || '');
            const slash = path.indexOf('/');
            if (slash >= 0) {
                this.inspectorExpandedDirs.add(path.slice(0, slash));
            }
        }
    }

    inspectBa2(path) {
        if (!path || !window.chrome?.webview) return;
        if (!this.isStagedBa2Path(path)) return;
        this.clearInspectorLoadTimeout();
        this.inspectedBa2Path = path;
        this.inspectedFolderPath = '';
        this.inspectorLoading = true;
        this.updateInspectorPanel({ preserveScroll: false });
        this.scheduleInspectorLoadTimeout(path, 'ba2');
        window.chrome.webview.postMessage({
            type: 'BUNDLE_LIST_BA2',
            path: this.resolveArchivePath(path)
        });
    }

    inspectFolder(path) {
        if (!path || !window.chrome?.webview) return;
        if (!this.isStagedFolderPath(path)) return;
        this.clearInspectorLoadTimeout();
        this.inspectedFolderPath = path;
        this.inspectedBa2Path = '';
        this.inspectorArchiveType = '';
        this.inspectorLoading = true;
        this.updateInspectorPanel({ preserveScroll: false });
        this.scheduleInspectorLoadTimeout(path, 'folder');
        window.chrome.webview.postMessage({
            type: 'BUNDLE_LIST_FOLDER',
            path
        });
    }

    handleInspectorMessage(data) {
        if (data.type === 'BA2_CONTENTS') {
            const path = data.path || this.inspectedBa2Path;
            if (!this.isStagedBa2Path(path)) {
                this.resetInspector();
                this.updateInspectorPanel({ preserveScroll: false });
                return;
            }
            this.clearInspectorLoadTimeout();
            this.inspectedBa2Path = path || this.inspectedBa2Path;
            this.inspectedFolderPath = '';
            this.ba2Contents = Array.isArray(data.entries) ? data.entries : [];
            this.inspectorArchiveType = data.archiveType || '';
            this.inspectorFileCount = data.fileCount ?? this.ba2Contents.length;
            this.inspectorLoading = false;
            const archiveKey = this.getArchiveSelectionKey();
            if (archiveKey && this.ba2Contents.length) {
                this.initArchiveSelection(archiveKey, this.ba2Contents);
            }
            this.ensureDefaultExpandedDirs();
            if (data.error && window.chrome?.webview) {
                window.chrome.webview.postMessage({
                    type: 'STATUS',
                    status: { type: 'error', text: data.error }
                });
            }
            this.updateInspectorPanel({ preserveScroll: false });
            const select = document.getElementById('bundle-staged-ba2-select');
            if (select && this.inspectedBa2Path) select.value = this.inspectedBa2Path;
            return;
        }

        if (data.type === 'FOLDER_CONTENTS') {
            const path = data.path || this.inspectedFolderPath;
            if (!this.isStagedFolderPath(path)) {
                this.resetInspector();
                this.updateInspectorPanel({ preserveScroll: false });
                return;
            }
            this.clearInspectorLoadTimeout();
            this.inspectedFolderPath = path || this.inspectedFolderPath;
            this.inspectedBa2Path = '';
            this.ba2Contents = Array.isArray(data.entries) ? data.entries : [];
            this.inspectorArchiveType = '';
            this.inspectorFileCount = data.fileCount ?? this.ba2Contents.length;
            this.inspectorLoading = false;
            const folderKey = this.getArchiveSelectionKey();
            if (folderKey && this.ba2Contents.length) {
                this.initArchiveSelection(folderKey, this.ba2Contents);
            }
            this.ensureDefaultExpandedDirs();
            this.maybeAutoSwitchFormat(this.ba2Contents);
            if (data.error && window.chrome?.webview) {
                window.chrome.webview.postMessage({
                    type: 'STATUS',
                    status: { type: 'error', text: data.error }
                });
            }
            this.updateInspectorPanel({ preserveScroll: false });
            return;
        }

        if (data.type === 'GAME_BA2_LIST') {
            this.gameBa2List = Array.isArray(data.files) ? data.files : [];
            if (data.dataPath) this.gameDataPath = data.dataPath;
            this.syncInspectorWithStaged();
            this.updateInspectorPanel({ preserveScroll: false });
        }
    }

    updateFileEntryHeaderToolbar() {
        const headerToolbar = document.getElementById('file-entry-header-toolbar');
        if (!headerToolbar) return;
        headerToolbar.innerHTML = (this.inspectedBa2Path || this.inspectedFolderPath)
            ? BundleRenderer.renderInspectorToolbar(this)
            : '';
    }

    updateInspectorPanel({ preserveScroll = true } = {}) {
        const scrollRoot = document.querySelector('.file-entry-inspect .ba2-explorer-tree');
        const scrollTop = preserveScroll && scrollRoot ? scrollRoot.scrollTop : 0;

        this.updateFileEntryHeaderToolbar();
        const root = document.getElementById('ba2-inspector-root');
        if (!root) return;
        root.innerHTML = BundleRenderer.renderInspectorContent(this);
        this.setupInspectorEvents();
        this.bindStagedListEvents(root);
        this.ensureIcons();
        this.updateWizardState();
        this.updateStagedActiveHighlight();

        if (preserveScroll) {
            requestAnimationFrame(() => {
                requestAnimationFrame(() => {
                    const nextScrollRoot = document.querySelector('.file-entry-inspect .ba2-explorer-tree');
                    if (nextScrollRoot) nextScrollRoot.scrollTop = scrollTop;
                });
            });
        }
    }

    updateStagedActiveHighlight() {
        const activeKey = this.inspectedBa2Path
            ? this.normalizeArchiveKey(this.inspectedBa2Path)
            : '';
        document.querySelectorAll('.file-entry-staged-ba2').forEach(row => {
            const path = row.dataset.path;
            const isActive = Boolean(activeKey && path && this.normalizeArchiveKey(path) === activeKey);
            row.classList.toggle('file-entry-staged-item--active', isActive);
        });

        const activeFolderKey = this.inspectedFolderPath
            ? this.normalizeFolderKey(this.inspectedFolderPath)
            : '';
        document.querySelectorAll('.file-entry-staged-folder').forEach(row => {
            const path = row.dataset.path;
            const isActive = Boolean(activeFolderKey && path && this.normalizeFolderKey(path) === activeFolderKey);
            row.classList.toggle('file-entry-staged-item--active', isActive);
        });
    }

    updateStagedPanelBadges() {
        const applyBadge = (row, key) => {
            if (!key) return;
            const total = this.ba2TotalPaths.get(key);
            const selected = this.getSelectedCount(key);
            let badge = row.querySelector('.staged-partial-badge');
            if (total && selected < total) {
                const text = window._t('staged_partial_files', selected, total);
                if (!badge) {
                    badge = document.createElement('div');
                    badge.className = 'staged-partial-badge';
                    row.querySelector('.bundle-staged-item-body')?.appendChild(badge);
                }
                badge.textContent = text;
            } else if (badge) {
                badge.remove();
            }
        };

        document.querySelectorAll('.file-entry-staged-ba2').forEach(row => {
            const path = row.dataset.path;
            if (path) applyBadge(row, this.normalizeArchiveKey(path));
        });

        document.querySelectorAll('.file-entry-staged-folder').forEach(row => {
            const path = row.dataset.path;
            if (path) applyBadge(row, this.normalizeFolderKey(path));
        });
    }

    afterSelectionChange() {
        this.updateInspectorPanel();
        this.updateStagedPanelBadges();
    }

    findInspectorFolderElement(folderPath) {
        const folders = document.querySelectorAll('.ba2-tree-folder[data-folder-path]');
        for (const el of folders) {
            if (el.dataset.folderPath === folderPath) return el;
        }
        return null;
    }

    toggleInspectorFolder(folderPath) {
        const folderEl = this.findInspectorFolderElement(folderPath);
        if (!folderEl) {
            if (this.inspectorExpandedDirs.has(folderPath)) {
                this.inspectorExpandedDirs.delete(folderPath);
            } else {
                this.inspectorExpandedDirs.add(folderPath);
            }
            this.updateInspectorPanel();
            return;
        }

        const willExpand = !this.inspectorExpandedDirs.has(folderPath);
        if (willExpand) {
            this.inspectorExpandedDirs.add(folderPath);
            const html = BundleRenderer.renderFolderChildrenHtml(this, folderPath);
            if (html) {
                const children = document.createElement('div');
                children.className = 'ba2-tree-children';
                children.innerHTML = html;
                folderEl.appendChild(children);
                this.bindInspectorTreeEvents(children);
                if (window.lucide) window.lucide.createIcons({ nodes: [children] });
            }
            folderEl.classList.add('ba2-tree-folder--expanded');
        } else {
            this.inspectorExpandedDirs.delete(folderPath);
            folderEl.querySelector(':scope > .ba2-tree-children')?.remove();
            folderEl.classList.remove('ba2-tree-folder--expanded');
        }
    }

    bindInspectorTreeEvents(scope = document) {
        const archiveKey = this.getArchiveSelectionKey();
        const queryAll = (selector) => (
            scope === document ? document.querySelectorAll(selector) : scope.querySelectorAll(selector)
        );

        queryAll('.ba2-tree-folder-check[data-indeterminate="true"], .ba2-tree-archive-root-check[data-indeterminate="true"]').forEach(cb => {
            cb.indeterminate = true;
        });

        queryAll('.ba2-tree-archive-root-check').forEach(cb => {
            cb.onclick = (e) => e.stopPropagation();
            cb.onchange = () => {
                if (!archiveKey) return;
                if (cb.checked) {
                    this.selectAllForArchive(archiveKey);
                } else {
                    this.deselectAllForArchive(archiveKey);
                }
                this.afterSelectionChange();
            };
        });

        queryAll('.ba2-tree-archive-root').forEach(row => {
            row.onclick = (e) => {
                if (e.target.closest('.ba2-tree-archive-root-check')) return;
                if (!archiveKey) return;
                if (this.isAllSelected(archiveKey)) {
                    this.deselectAllForArchive(archiveKey);
                } else {
                    this.selectAllForArchive(archiveKey);
                }
                this.afterSelectionChange();
            };
        });

        queryAll('.ba2-tree-file-check').forEach(cb => {
            cb.onclick = (e) => e.stopPropagation();
            cb.onchange = () => {
                const filePath = cb.dataset.filePath;
                if (!filePath || !archiveKey) return;
                this.toggleFileSelection(archiveKey, filePath, cb.checked);
                this.afterSelectionChange();
            };
        });

        queryAll('.ba2-tree-folder-check').forEach(cb => {
            cb.onclick = (e) => e.stopPropagation();
            cb.onchange = () => {
                const path = cb.dataset.folderPath;
                if (!path || !archiveKey) return;
                this.toggleFolderSelection(archiveKey, path, cb.checked);
                this.afterSelectionChange();
            };
        });

        queryAll('.ba2-tree-folder-btn').forEach(btn => {
            btn.onclick = (e) => {
                e.preventDefault();
                const path = btn.dataset.folderPath;
                if (!path) return;
                this.toggleInspectorFolder(path);
            };
        });
    }

    setupInspectorEvents() {
        const stagedBa2Select = document.getElementById('bundle-staged-ba2-select');
        const filterInput = document.getElementById('ba2-inspector-filter');

        if (stagedBa2Select) {
            stagedBa2Select.onchange = () => {
                const path = stagedBa2Select.value;
                if (path) this.inspectBa2(path);
            };
        }

        if (filterInput) {
            filterInput.oninput = (e) => {
                this.inspectorFilter = e.target.value;
                this.updateInspectorPanel();
                const next = document.getElementById('ba2-inspector-filter');
                if (next) {
                    next.focus();
                    next.setSelectionRange(next.value.length, next.value.length);
                }
            };
        }

        const selectAllBtn = document.getElementById('btn-inspector-select-all');
        const deselectAllBtn = document.getElementById('btn-inspector-deselect-all');
        const archiveKey = this.getArchiveSelectionKey();

        if (selectAllBtn && archiveKey) {
            selectAllBtn.onclick = () => {
                this.selectAllForArchive(archiveKey);
                this.afterSelectionChange();
            };
        }

        if (deselectAllBtn && archiveKey) {
            deselectAllBtn.onclick = () => {
                this.deselectAllForArchive(archiveKey);
                this.afterSelectionChange();
            };
        }

        const extractBtn = document.getElementById('btn-inspector-extract');
        if (extractBtn) {
            extractBtn.onclick = () => {
                if (extractBtn.disabled) return;
                this.promptExtractToFolder();
            };
        }

        this.bindInspectorTreeEvents();
    }

    loadGameBa2List() {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage({ type: 'BUNDLE_LIST_GAME_BA2S' });
        }
    }

    maybeAutoSwitchFormat(entries) {
        const hasDds = (entries || []).some(e =>
            String(e?.path || '').toLowerCase().endsWith('.dds')
        );
        if (!hasDds) return;

        const formatSelect = document.getElementById('bundle-format-select');
        if (!formatSelect || formatSelect.value === 'DDS') return;

        formatSelect.value = 'DDS';
        const typeSummary = document.getElementById('bundle-type-summary');
        if (typeSummary) typeSummary.textContent = getBundleFormatLabel('DDS');

        if (window.chrome?.webview) {
            window.chrome.webview.postMessage({
                type: 'STATUS',
                status: { type: 'info', text: window._t('bundle_format_auto_dds') }
            });
        }
    }

    initIpcHandlers() {
        if (_bundleIpcHandlerAttached || !window.chrome?.webview) return;
        _bundleIpcHandlerAttached = true;

        window.chrome.webview.addEventListener('message', (e) => {
            try {
                const data = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
                const bundle = window.app?.sections?.bundle;
                if (!bundle || !bundle.isCreating) return;

                if (data.type === 'FOLDER_SELECTED' && data.path) {
                    bundle.selectedFolders.add(data.path);
                    bundle.pendingAutoInspectFolderPath = data.path;
                    bundle.refresh();
                }
            } catch (_) { }
        });

        if (!window._bundleFileSelectedHandler) {
            window._bundleFileSelectedHandler = (e) => {
                try {
                    const data = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
                    if (data.type !== 'FILE_SELECTED') return;
                    const bundle = window.app?.sections?.bundle;
                    if (!bundle || !bundle.isCreating) return;
                    if (Array.isArray(data.files)) {
                        data.files.forEach(f => bundle.externalFiles.add(f));
                        if (data.files.length > 0) {
                            bundle.pendingAutoInspectPath = data.files[data.files.length - 1];
                        }
                        bundle.refresh();
                    }
                } catch (_) { }
            };
            window.chrome.webview.addEventListener('message', window._bundleFileSelectedHandler);
        }
    }

    ensureIcons() {
        setTimeout(() => {
            if (window.lucide) window.lucide.createIcons();
        }, 50);
    }

    initGlobalHandlers() {
        if (!window.handleRenameBundle) {
            window.handleRenameBundle = (name) => {
                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                const newName = prompt(window._t('rename_bundle_prompt'), name.replace('.ba2', ''));
                if (newName && newName !== name.replace('.ba2', '')) {
                     let cleanName = newName; 
                     if (cleanName.endsWith('.ba2')) cleanName = cleanName.substring(0, cleanName.length - 4);
                    window.chrome.webview.postMessage({ type: 'RENAME_MOD', currentName: name, newName: cleanName });
                }
            };
        }

        if (!window.handleToggleBundle) {
            window.handleToggleBundle = (name, currentStatus) => {
                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                const newStatus = currentStatus === 'enabled' ? false : true;
                window.chrome.webview.postMessage({ 
                    type: 'UPDATE_MOD_METADATA', 
                    originalName: name, 
                    metadata: { isEnabled: newStatus } 
                });
            };
        }
        
        if (!window.handleDeleteBundleFile) {
            window.handleDeleteBundleFile = (name) => {
                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                console.log("[BUNDLE] Requesting delete for file:", name);
                window.chrome.webview.postMessage({ type: 'DELETE_BUNDLE_FILE', name: name });
            };
        }

        if (!window.handlePromoteBundleToMod) {
            window.handlePromoteBundleToMod = async (name) => {
                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                const ok = await window.appConfirm({
                    title: window._t('move_bundle_to_mods'),
                    message: window._t('move_bundle_to_mods_confirm', name),
                    okText: window._t('move_bundle_to_mods'),
                    cancelText: window._t('cancel'),
                    danger: false
                });
                if (!ok || !window.chrome?.webview) return;
                window.chrome.webview.postMessage({ type: 'PROMOTE_BUNDLE_TO_MOD', name });
            };
        }

        if (!window.handleAddToBundle) {
            window.handleAddToBundle = (originalName) => {
                this.selectedMods.add(originalName);
                window.chrome.webview.postMessage({
                    type: 'STATUS',
                    status: { type: 'success', text: window._t('added_to_workspace_banner') }
                });
                this.refresh();
            };
        }

        if (!_bundleEventsAttached) {
            document.addEventListener('click', (e) => {
                if (!e.target.closest('.bundle-menu-dropdown') && !e.target.closest('.bundle-menu-trigger')) {
                    document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                }

                const trigger = e.target.closest('.bundle-menu-trigger');
                if (trigger) {
                    e.preventDefault();
                    e.stopPropagation();
                    
                    const name = trigger.dataset.name;
                    const status = trigger.dataset.status;

                    document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());

                    console.log("[BUNDLE] Opening Menu via Delegate for:", name);

                    const toggleAction = status === 'enabled' ? window._t('remove_from_list') : window._t('add_to_list');
                    const menu = document.createElement('div');
                    menu.className = 'bundle-menu-dropdown';
                    
                    const escapedName = name.replace(/\\/g, "\\\\").replace(/'/g, "\\'");

                    menu.innerHTML = `
                        <div class="bundle-menu-item" onclick="window.handleRenameBundle('${escapedName}')">
                            <i data-lucide="edit-2" style="width: 14px;"></i> ${window._t('rename')}
                        </div>
                        <div class="bundle-menu-item" onclick="window.handleToggleBundle('${escapedName}', '${status}')">
                            <i data-lucide="${status === 'enabled' ? 'minus-circle' : 'plus-circle'}" style="width: 14px;"></i> ${toggleAction}
                        </div>
                        <div class="bundle-menu-item" onclick="window.handlePromoteBundleToMod('${escapedName}')">
                            <i data-lucide="arrow-right-circle" style="width: 14px;"></i> ${window._t('move_bundle_to_mods')}
                        </div>
                        <div class="bundle-menu-item danger" onclick="window.handleDeleteBundleFile('${escapedName}')">
                            <i data-lucide="trash-2" style="width: 14px;"></i> ${window._t('delete_file')}
                        </div>
                    `;
                    
                    document.body.appendChild(menu);
                    if (window.lucide) window.lucide.createIcons();

                    const rect = trigger.getBoundingClientRect();
                    menu.style.top = `${rect.bottom + 5}px`;
                    const leftPos = rect.left - 150;
                    menu.style.left = `${leftPos > 10 ? leftPos : 10}px`;
                }
            });
            _bundleEventsAttached = true;
        }

        if (!window.nuclearOpenBundleMenu) {
            window.nuclearOpenBundleMenu = (el) => {
                const name = el.getAttribute('data-name');
                const status = el.getAttribute('data-status');

                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());

                console.log("[NUCLEAR] Opening Bundle Menu for:", name);

                const toggleAction = status === 'enabled' ? window._t('remove_from_list') : window._t('add_to_list');
                const menu = document.createElement('div');
                menu.className = 'bundle-menu-dropdown';
                
                const itemRename = document.createElement('div');
                itemRename.className = 'bundle-menu-item';
                itemRename.innerHTML = `<i data-lucide="edit-2" style="width: 14px;"></i> ${window._t('rename')}`;
                itemRename.onclick = (e) => {
                    e.stopPropagation();
                    window.handleRenameBundle(name);
                };
                menu.appendChild(itemRename);

                const itemToggle = document.createElement('div');
                itemToggle.className = 'bundle-menu-item';
                itemToggle.innerHTML = `<i data-lucide="${status === 'enabled' ? 'minus-circle' : 'plus-circle'}" style="width: 14px;"></i> ${toggleAction}`;
                itemToggle.onclick = (e) => {
                     e.stopPropagation();
                     window.handleToggleBundle(name, status);
                };
                menu.appendChild(itemToggle);

                const itemPromote = document.createElement('div');
                itemPromote.className = 'bundle-menu-item';
                itemPromote.innerHTML = `<i data-lucide="arrow-right-circle" style="width: 14px;"></i> ${window._t('move_bundle_to_mods')}`;
                itemPromote.onclick = (e) => {
                    e.stopPropagation();
                    void window.handlePromoteBundleToMod(name);
                };
                menu.appendChild(itemPromote);

                const itemDelete = document.createElement('div');
                itemDelete.className = 'bundle-menu-item danger';
                itemDelete.innerHTML = `<i data-lucide="trash-2" style="width: 14px;"></i> ${window._t('delete_file')}`;
                itemDelete.onclick = async (e) => {
                    e.stopPropagation();
                    const ok = await window.appConfirm({
                        title: 'Confirmation',
                        message: window._t('delete_bundle_confirm', name),
                        okText: window._t('delete'),
                        cancelText: window._t('cancel'),
                        danger: true
                    });
                    if (ok) {
                        window.handleDeleteBundleFile(name);
                    }
                };
                menu.appendChild(itemDelete);
                
                document.body.appendChild(menu);
                if (window.lucide) window.lucide.createIcons();

                const rect = el.getBoundingClientRect();
                menu.style.top = `${rect.bottom + 5}px`;
                const leftPos = rect.left - 150;
                menu.style.left = `${leftPos > 10 ? leftPos : 10}px`;
            };
        }
    }

    setupWizardEvents() {
        const nameInput = document.getElementById('bundle-name-input');
        const formatSelect = document.getElementById('bundle-format-select');
        const addExternalBtn = document.getElementById('btn-add-external');
        const addFolderBtn = document.getElementById('btn-add-folder');
        const addInternalBtn = document.getElementById('btn-add-internal');
        const clearBtn = document.getElementById('btn-clear-workspace');
        const menuSelectAllBtn = document.getElementById('btn-menu-select-all');
        const menuDeselectAllBtn = document.getElementById('btn-menu-deselect-all');
        const menuExtractBtn = document.getElementById('btn-menu-extract');

        if (nameInput) {
            nameInput.addEventListener('input', (e) => {
                this.bundleName = e.target.value.replace(/[^a-z0-9-_]/gi, '');
            });
        }

        const updateBundleTypeSummary = () => {
            const typeSummary = document.getElementById('bundle-type-summary');
            if (typeSummary && formatSelect) {
                typeSummary.textContent = getBundleFormatLabel(formatSelect.value);
            }
        };

        if (formatSelect) {
            formatSelect.addEventListener('change', updateBundleTypeSummary);
            updateBundleTypeSummary();
        }

        this._mountBundleActionsMenu();

        if (addInternalBtn) {
            addInternalBtn.onclick = () => this.openInternalModPicker();
        }

        const pickerDoneBtn = document.getElementById('btn-picker-done');
        const pickerBackBtn = document.getElementById('btn-close-picker');
        
        const closePicker = () => {
            this.isPickingInternal = false;
            if (!this.inspectedBa2Path && this.selectedMods.size > 0) {
                this.pendingAutoInspectPath = this.resolveArchivePath([...this.selectedMods][0]);
            }
            this.refresh();
        };

        if (pickerDoneBtn) pickerDoneBtn.onclick = closePicker;
        if (pickerBackBtn) pickerBackBtn.onclick = closePicker;

        if (addExternalBtn) {
            addExternalBtn.onclick = () => this.promptAddExternalBa2();
        }

        if (addFolderBtn) {
            addFolderBtn.onclick = () => this.promptAddFolder();
        }

        if (menuSelectAllBtn) {
            menuSelectAllBtn.onclick = () => {
                const archiveKey = this.getArchiveSelectionKey();
                if (!archiveKey || menuSelectAllBtn.disabled) return;
                this._closeBundleActionsMenu();
                this.selectAllForArchive(archiveKey);
                this.afterSelectionChange();
            };
        }

        if (menuDeselectAllBtn) {
            menuDeselectAllBtn.onclick = () => {
                const archiveKey = this.getArchiveSelectionKey();
                if (!archiveKey || menuDeselectAllBtn.disabled) return;
                this._closeBundleActionsMenu();
                this.deselectAllForArchive(archiveKey);
                this.afterSelectionChange();
            };
        }

        if (menuExtractBtn) {
            menuExtractBtn.onclick = () => {
                if (menuExtractBtn.disabled) return;
                this.promptExtractToFolder();
            };
        }

        if (clearBtn) {
            clearBtn.onclick = () => this.clearWorkspace();
        }

        this.setupInspectorEvents();
        this.setupFileEntryEvents();
        this.setupFileEntryContextMenu();

        document.querySelectorAll('.picker-item').forEach(item => {
            item.onclick = (e) => {
                const name = item.dataset.name;
                const isSelected = this.selectedMods.has(name);
                
                if (isSelected) {
                    this.selectedMods.delete(name);
                    item.classList.remove('selected');
                } else {
                    this.selectedMods.add(name);
                    item.classList.add('selected');
                }

                const checkbox = item.querySelector('.picker-checkbox');
                if (checkbox) {
                    checkbox.style.border = this.selectedMods.has(name) ? '2px solid var(--primary-green)' : '2px solid rgba(255,255,255,0.2)';
                    checkbox.style.background = this.selectedMods.has(name) ? 'var(--primary-green)' : 'transparent';
                    checkbox.innerHTML = this.selectedMods.has(name) ? '<i data-lucide="check" style="width: 14px; color: #121212;"></i>' : '';
                    if (window.lucide) window.lucide.createIcons();
                }

                const countBadge = document.getElementById('picker-count-badge');
                if (countBadge) {
                    countBadge.innerText = window._t('n_selected_badge', this.selectedMods.size);
                }
            };
        });


        const startBundlingClick = () => {
            const bundleName = this.bundleName || 'MyCustomBundle';
            this.startBundling(bundleName);
        };

        document.querySelectorAll('.btn-start-bundling').forEach(btn => {
            btn.onclick = startBundlingClick;
        });

        this.updateWizardState();
    }

    _closeBundleActionsMenu() {
        const dropdown = document.getElementById('bundle-actions-dropdown');
        const trigger = document.getElementById('btn-bundle-actions');
        if (dropdown) dropdown.hidden = true;
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
    }

    _mountBundleActionsMenu() {
        const trigger = document.getElementById('btn-bundle-actions');
        const dropdown = document.getElementById('bundle-actions-dropdown');
        if (!trigger || !dropdown) return;

        if (this._bundleActionsMenuAbort) {
            this._bundleActionsMenuAbort.abort();
        }
        this._bundleActionsMenuAbort = new AbortController();
        const { signal } = this._bundleActionsMenuAbort;

        dropdown.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');

        trigger.addEventListener('click', (e) => {
            e.stopPropagation();
            const open = dropdown.hidden;
            if (open) {
                this.updateWizardState();
            }
            dropdown.hidden = !open;
            trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
            if (open && window.lucide) window.lucide.createIcons();
        }, { signal });

        document.addEventListener('click', (e) => {
            if (!document.getElementById('btn-bundle-actions')) return;
            if (!e.target.closest('.mods-actions-menu-wrap')) {
                this._closeBundleActionsMenu();
            }
        }, { signal });
    }

    updateWizardState() {
        const countLabel = document.getElementById('selected-count');
        if (countLabel) countLabel.innerText = window._t('n_selected_badge', this.selectedMods.size);

        const total = this.getTotalStagedCount();
        document.querySelectorAll('.bundle-workspace-count').forEach(el => {
            el.textContent = `${window._t('total_items')}: ${total}`;
        });
        const createSummary = document.querySelector('.bundle-create-summary');
        if (createSummary) {
            const totalStrong = createSummary.querySelector('strong');
            if (totalStrong) totalStrong.textContent = String(total);
        }

        const canCreate = this.canCreateBundle();
        document.querySelectorAll('.btn-start-bundling').forEach(btn => {
            btn.disabled = !canCreate;
        });

        const canSelectArchive = this.canUseArchiveSelection();
        const menuSelectAllBtn = document.getElementById('btn-menu-select-all');
        const menuDeselectAllBtn = document.getElementById('btn-menu-deselect-all');
        const menuExtractBtn = document.getElementById('btn-menu-extract');
        if (menuSelectAllBtn) menuSelectAllBtn.disabled = !canSelectArchive;
        if (menuDeselectAllBtn) menuDeselectAllBtn.disabled = !canSelectArchive;
        if (menuExtractBtn) menuExtractBtn.disabled = !this.canExtractSelection();
        this._updateSelectionMenuItemStates();
    }

    startBundling(bundleName) {
        if (!this.canCreateBundle()) return;

        if (window.chrome && window.chrome.webview) {
            const payload = this.buildCreateBundlePayload(bundleName);
            if (payload.mods.length === 0 && payload.ba2Partial.length === 0 && payload.folders.length === 0 && payload.folderPartial.length === 0) {
                window.chrome.webview.postMessage({
                    type: 'STATUS',
                    status: { type: 'error', text: window._t('bundle_nothing_to_pack') }
                });
                return;
            }

            window.chrome.webview.postMessage(payload);
            
            window.chrome.webview.postMessage({
                type: 'STATUS',
                status: { type: 'info', text: window._t('starting_bundle_banner', bundleName) }
            });

            this.isCreating = false;
            this.isPickingInternal = false;
            this.selectedMods.clear();
            this.externalFiles.clear();
            this.selectedFolders.clear();
            this.ba2Selections.clear();
            this.ba2TotalPaths.clear();
            this.ba2AllPaths.clear();
            this.resetInspector();

            if (window.app && typeof window.app.replaceCurrentSectionContent === 'function') {
                window.app.replaceCurrentSectionContent();
            } else {
                this.refresh();
            }
        }
    }

    refresh() {
        if (window.app && window.app.replaceCurrentSectionContent) {
            window.app.replaceCurrentSectionContent();
            return;
        }
        const contentArea = document.getElementById('content-area');
        if (contentArea) {
            contentArea.classList.add('content-refreshing');
            contentArea.innerHTML = `<div class="section-content">${this.render(this.data)}</div>`;
            this.onMount(this.data);
            requestAnimationFrame(() => {
                requestAnimationFrame(() => contentArea.classList.remove('content-refreshing'));
            });
        }
    }

    updateValues(data) {
        if (window.app?.currentSection !== 'bundle') return;
        this.data = data;
        if (!this.isCreating) {
            this.refresh();
        }
    }
}
