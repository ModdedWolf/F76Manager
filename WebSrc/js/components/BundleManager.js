import { BundleRenderer } from './BundleRenderer.js';
import { mountMeshViewer } from '../utils/meshViewer.js';
import { mountBa2TextView } from './Ba2TextEditor.js';
let _bundleEventsAttached = false;
let _bundleIpcHandlerAttached = false;

function getBundleFormatLabel(value) {
    return value === 'DDS'
        ? window._t('bundle_format_dds')
        : value === 'Auto'
            ? window._t('bundle_format_auto')
            : window._t('bundle_format_general');
}

const AUDIO_PREVIEW_EXTENSIONS = new Set(['mp3', 'wav', 'ogg', 'flac', 'xwm', 'fuz']);

function formatPlayerTime(seconds) {
    const total = Math.max(0, Math.floor(Number(seconds) || 0));
    const m = Math.floor(total / 60);
    const s = total % 60;
    return `${m}:${String(s).padStart(2, '0')}`;
}

function escapeMenuText(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

function shortenMenuName(name, max = 28) {
    const text = String(name || '');
    return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

function showBundleStatus(type, text) {
    if (window.app && typeof window.app.showBanner === 'function') {
        window.app.showBanner({ type, text });
        return;
    }
    console.warn('[Bundle]', type, text);
}

export class BundleManager {
    constructor() {
        this.data = null;
        this.isCreating = false;
        this.selectedMods = new Set();
        this.externalFiles = new Set();
        this.selectedFolders = new Set();
        this.isolatedFolders = new Set();
        this.looseStaged = new Map();
        this.bundleName = 'MyCustomBundle';
        this.editingBundle = null;
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
        this.folderDataPaths = new Map();
        this.ba2Replacements = new Map();
        this.ba2Removed = new Map();
        this.overlaySourceSizes = new Map();
        this.inspectedResolvedPath = '';
        this.previewCacheKey = '';
        this.gameDataPath = '';
        this.pendingAutoInspectPath = '';
        this.pendingAutoInspectFolderPath = '';
        this.inspectorLoadTimeoutId = null;
        this.inspectorLoadRequestPath = '';
        this.inspectorLoadRequestMode = 'ba2';
        this.previewPath = '';
        this.previewRequestId = 0;
        this.previewResult = null;
        this.previewLoading = false;
        this.previewCache = new Map();
        this.previewMaterialZoom = null;
        this._meshViewer = null;
        this._meshWireframe = false;
        this._audioBlobUrl = null;
        this._audioVolume = 1;
        this._audioToggle = null;
        this._meshAnimToggle = null;
        this._audioProgressRaf = null;
        this._audioInfoOpen = false;
        this._audioKeyAbort = null;
        this._audioAutoplay = false;
        this._filterTimer = null;
        this._inspectorMetaMinWidth = 0;
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
            this.exitEditMode();
            this.discardAllIsolatedFolders();
            this.selectedMods.clear();
            this.externalFiles.clear();
            this.selectedFolders.clear();
            this.ba2Selections.clear();
            this.ba2TotalPaths.clear();
            this.ba2AllPaths.clear();
            this.ba2Replacements.clear();
            this.ba2Removed.clear();
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
        } else if (this._bundleCreateInfoAbort) {
            this._bundleCreateInfoAbort.abort();
            this._bundleCreateInfoAbort = null;
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
                const webview = window.chrome?.webview;
                if (webview && typeof webview.postMessageWithAdditionalObjects === 'function') {
                    try {
                        // WebView2 does not expose File.path; hand the File
                        // objects to the host so it can read the real paths.
                        const sources = [];
                        for (const src of this.collectBa2Sources()) {
                            sources.push({ kind: 'ba2', ref: src.modRef });
                        }
                        for (const folder of this.selectedFolders) {
                            sources.push({ kind: 'folder', ref: folder });
                        }
                        webview.postMessageWithAdditionalObjects(
                            { type: 'BUNDLE_ADD_DROPPED_FILES', sources },
                            Array.from(files)
                        );
                        return;
                    } catch (err) {
                        webview.postMessage({ type: 'JS_LOG', message: `[BUNDLE] postMessageWithAdditionalObjects failed: ${err?.message || err}` });
                    }
                }
                let lastAdded = '';
                for (const file of files) {
                    if (file.name.toLowerCase().endsWith('.ba2')) {
                        const path = file.path;
                        if (!path) {
                            if (window.chrome?.webview) {
                                window.chrome.webview.postMessage({ type: 'JS_LOG', message: `[BUNDLE] Rejected dropped file with no path: ${file.name}` });
                            }
                            continue;
                        }
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

    async promptAddFolder() {
        if (!window.chrome?.webview) return;
        this._closeBundleActionsMenu();
        this._closeFileEntryContextMenu();

        const mergeArchive = this.inspectedBa2Path ? this.toHostArchiveRef(this.inspectedBa2Path) : '';
        if (mergeArchive) {
            const archiveName = String(this.inspectedBa2Path).split(/[\\/]/).pop();
            const choice = await window.appConfirm?.({
                title: window._t('bundle_merge_folder_title'),
                message: window._t('bundle_merge_folder_prompt', archiveName),
                okText: window._t('bundle_merge_folder_ok'),
                altText: window._t('bundle_add_separately'),
                cancelText: window._t('cancel')
            });
            if (choice === true) {
                window.chrome.webview.postMessage({ type: 'BUNDLE_BROWSE_FOLDER', mergeInto: mergeArchive });
                return;
            }
            if (choice !== 'alt') return;
        }

        window.chrome.webview.postMessage({ type: 'BUNDLE_BROWSE_FOLDER' });
    }

    promptOverlayIntoTree(targetDir, mode) {
        if (!window.chrome?.webview || !this.inspectedBa2Path) return;
        this._closeFileEntryContextMenu();
        window.chrome.webview.postMessage({
            type: 'BUNDLE_PICK_OVERLAY',
            archive: this.toHostArchiveRef(this.inspectedBa2Path),
            targetDir: this.normalizeBa2InternalPath(targetDir || ''),
            mode: mode === 'folder' ? 'folder' : 'files'
        });
    }

    async offerMergeDroppedFolders(folders) {
        const list = (folders || []).filter(Boolean);
        if (list.length === 0) return false;
        const mergeArchive = this.inspectedBa2Path ? this.toHostArchiveRef(this.inspectedBa2Path) : '';
        if (!mergeArchive || !window.chrome?.webview) return false;

        const archiveName = String(this.inspectedBa2Path).split(/[\\/]/).pop();
        const choice = await window.appConfirm?.({
            title: window._t('bundle_merge_folder_title'),
            message: window._t('bundle_merge_folder_prompt', archiveName),
            okText: window._t('bundle_merge_folder_ok'),
            altText: window._t('bundle_add_separately'),
            cancelText: window._t('cancel')
        });
        if (choice === true) {
            window.chrome.webview.postMessage({
                type: 'BUNDLE_OVERLAY_FROM_FOLDERS',
                archive: mergeArchive,
                folders: list,
                targetDir: ''
            });
            return true;
        }
        if (choice === 'alt') return false;
        return true;
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
                else if (type === 'folder') {
                    this.forgetLooseStagedFolder(name);
                    this.discardIsolatedFolder(name);
                    this.selectedFolders.delete(name);
                }
                else {
                    this.externalFiles.delete(name);
                    this.forgetLooseStagedArchive(name);
                }
                if (type === 'folder') {
                    const folderKey = this.normalizeFolderKey(name);
                    this.ba2Replacements.delete(folderKey);
                    this.ba2Removed.delete(folderKey);
                } else {
                    const archiveKey = this.normalizeArchiveKey(name);
                    this.ba2Replacements.delete(archiveKey);
                    this.ba2Removed.delete(archiveKey);
                }
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
        this.discardAllIsolatedFolders();
        this.selectedMods.clear();
        this.externalFiles.clear();
        this.selectedFolders.clear();
        this.ba2Selections.clear();
        this.ba2TotalPaths.clear();
        this.ba2AllPaths.clear();
        this.ba2Replacements.clear();
        this.ba2Removed.clear();
        this.resetInspector();
        this.refresh();
    }

    discardIsolatedFolder(path) {
        if (!path) return;
        const key = this.normalizeFolderKey(path);
        let matched = '';
        for (const folder of this.isolatedFolders) {
            if (this.normalizeFolderKey(folder) === key) {
                matched = folder;
                break;
            }
        }
        if (!matched) return;
        this.isolatedFolders.delete(matched);
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage({ type: 'BUNDLE_DISCARD_ISOLATED', path: matched });
        }
    }

    discardAllIsolatedFolders({ deleteOnDisk = true } = {}) {
        this.looseStaged?.clear();
        if (!this.isolatedFolders?.size) {
            this.isolatedFolders = new Set();
            return;
        }
        if (deleteOnDisk && window.chrome?.webview) {
            for (const folder of [...this.isolatedFolders]) {
                window.chrome.webview.postMessage({ type: 'BUNDLE_DISCARD_ISOLATED', path: folder });
            }
        }
        this.isolatedFolders.clear();
    }

    isLooseModKey(name) {
        if (!name) return false;
        if (/^(Disabled\/)?Loose\//i.test(String(name))) return true;
        const mods = this.data?.mods || window.app?.realData?.mods || [];
        const mod = mods.find(m => m?.originalName === name);
        return !!(mod?.isLoose || mod?.type === 'loose');
    }

    isModStaged(name) {
        return this.selectedMods.has(name) || this.looseStaged.has(name);
    }

    getStagedModCount() {
        return this.selectedMods.size + this.looseStaged.size;
    }

    stageLooseMod(key) {
        if (!key || this.looseStaged.has(key) || !window.chrome?.webview) return;
        this.looseStaged.set(key, null);
        window.chrome.webview.postMessage({ type: 'BUNDLE_STAGE_LOOSE', key });
    }

    unstageLooseMod(key) {
        if (!this.looseStaged.has(key)) return;
        const entry = this.looseStaged.get(key);
        this.looseStaged.delete(key);
        if (!entry) return;
        const folder = entry.folder;
        if (folder) {
            const folderKey = this.normalizeFolderKey(folder);
            this.discardIsolatedFolder(folder);
            this.selectedFolders.delete(folder);
            this.ba2Selections.delete(folderKey);
            this.ba2TotalPaths.delete(folderKey);
            this.ba2AllPaths.delete(folderKey);
            this.ba2Replacements.delete(folderKey);
            this.ba2Removed.delete(folderKey);
            this.folderDataPaths.delete(folderKey);
        }
        for (const archive of entry.archives) {
            this.removeArchiveFromWorkspace(archive);
        }
        this.syncInspectorWithStaged();
    }

    forgetLooseStagedFolder(folder) {
        const key = this.normalizeFolderKey(folder);
        for (const [looseKey, entry] of [...this.looseStaged]) {
            if (!entry?.folder || this.normalizeFolderKey(entry.folder) !== key) continue;
            entry.folder = '';
            if (entry.archives.length === 0) this.looseStaged.delete(looseKey);
        }
    }

    forgetLooseStagedArchive(archive) {
        const key = this.normalizeArchiveKey(archive);
        for (const [looseKey, entry] of [...this.looseStaged]) {
            if (!entry) continue;
            entry.archives = entry.archives.filter(a => this.normalizeArchiveKey(a) !== key);
            if (!entry.folder && entry.archives.length === 0) this.looseStaged.delete(looseKey);
        }
    }

    handleLooseStaged(data) {
        const key = data?.key;
        const folder = data?.path || '';
        const archives = Array.isArray(data?.archives) ? data.archives.filter(Boolean) : [];
        if (!key || (!folder && archives.length === 0)) return;
        if (!this.looseStaged.has(key)) {
            if (folder) window.chrome?.webview?.postMessage({ type: 'BUNDLE_DISCARD_ISOLATED', path: folder });
            return;
        }
        this.looseStaged.set(key, { folder, archives });
        if (folder) {
            this.selectedFolders.add(folder);
            this.isolatedFolders.add(folder);
        }
        archives.forEach(a => this.externalFiles.add(a));
        if (this.isPickingInternal) {
            this.syncPickerSelection();
        } else {
            if (folder) this.pendingAutoInspectFolderPath = folder;
            else this.pendingAutoInspectPath = archives[archives.length - 1];
            this.refresh();
        }
    }

    handleLooseStageFailed(data) {
        const key = data?.key;
        if (key) this.looseStaged.delete(key);
        const name = String(key || '').split('/').pop();
        showBundleStatus('error', window._t('bundle_loose_stage_failed', name, data?.error || ''));
        if (this.isPickingInternal) this.syncPickerSelection();
    }

    syncPickerSelection() {
        document.querySelectorAll('.picker-item').forEach(item => {
            const selected = this.isModStaged(item.dataset.name);
            item.classList.toggle('selected', selected);
            const checkbox = item.querySelector('.picker-checkbox');
            if (checkbox) {
                checkbox.style.border = selected ? '2px solid var(--primary-green)' : '2px solid rgba(255,255,255,0.2)';
                checkbox.style.background = selected ? 'var(--primary-green)' : 'transparent';
                checkbox.innerHTML = selected ? '<i data-lucide="check" style="width: 14px; color: #121212;"></i>' : '';
            }
        });
        if (window.lucide) window.lucide.createIcons();
        const countBadge = document.getElementById('picker-count-badge');
        if (countBadge) countBadge.innerText = window._t('n_selected_badge', this.getStagedModCount());
    }

    startEditBundle(originalName) {
        const mods = this.data?.mods || window.app?.realData?.mods || [];
        const mod = mods.find(m => m?.originalName === originalName);
        const files = (mod?.files?.length ? mod.files : [originalName])
            .filter(f => /\.ba2$/i.test(String(f || '')));
        if (files.length === 0) return;

        if (!this.editingBundle) this._nameBeforeEdit = this.bundleName;
        this._closeFileEntryContextMenu();
        this.discardAllIsolatedFolders();
        this.selectedMods.clear();
        this.externalFiles.clear();
        this.selectedFolders.clear();
        this.ba2Selections.clear();
        this.ba2TotalPaths.clear();
        this.ba2AllPaths.clear();
        this.ba2Replacements.clear();
        this.ba2Removed.clear();
        this.resetInspector();

        const baseName = String(originalName).split('/').pop()
            .replace(/\.ba2$/i, '')
            .replace(/ - Textures$/i, '');
        this.editingBundle = { key: originalName, name: mod?.name || baseName };
        this.bundleName = baseName;
        files.forEach(f => this.selectedMods.add(f));

        this.isCreating = true;
        this.isPickingInternal = false;
        this.pendingAutoInspectPath = this.resolveArchivePath(files[0]);
        this.refresh();
    }

    exitEditMode() {
        if (!this.editingBundle) return;
        this.editingBundle = null;
        this.bundleName = this._nameBeforeEdit || 'MyCustomBundle';
        this._nameBeforeEdit = null;
    }

    getReplacement(archiveKey, internalPath) {
        if (!archiveKey || !internalPath) return '';
        return this.ba2Replacements.get(archiveKey)?.get(this.normalizeBa2InternalPath(internalPath)) || '';
    }

    rememberOverlaySourceSize(source, size) {
        const bytes = Number(size);
        if (source && Number.isFinite(bytes) && bytes > 0) this.overlaySourceSizes.set(source, bytes);
    }

    getOverlaySourceSize(source) {
        return source ? this.overlaySourceSizes.get(source) : undefined;
    }

    getReplacementCount(archiveKey) {
        return this.getOverlayCounts(archiveKey).replaced;
    }

    getAddedCount(archiveKey) {
        return this.getOverlayCounts(archiveKey).added;
    }

    getOverlayCounts(archiveKey) {
        const map = this.ba2Replacements.get(archiveKey);
        if (!map || map.size === 0) return { replaced: 0, added: 0 };
        const allPaths = this.ba2AllPaths.get(archiveKey);
        let replaced = 0;
        let added = 0;
        for (const path of map.keys()) {
            if (allPaths?.has(path)) replaced++;
            else added++;
        }
        return { replaced, added };
    }

    isOverlayAddition(archiveKey, internalPath) {
        const norm = this.normalizeBa2InternalPath(internalPath);
        if (!archiveKey || !norm || !this.getReplacement(archiveKey, norm)) return false;
        return !this.ba2AllPaths.get(archiveKey)?.has(norm);
    }

    getOverlayAdditions(archiveKey) {
        const map = this.ba2Replacements.get(archiveKey);
        if (!map) return [];
        const allPaths = this.ba2AllPaths.get(archiveKey);
        const out = [];
        for (const [path, source] of map) {
            if (!allPaths?.has(path)) out.push({ path, source });
        }
        return out;
    }

    setReplacement(archiveKey, internalPath, source) {
        if (!archiveKey || !internalPath || !source) return;
        const norm = this.normalizeBa2InternalPath(internalPath);
        const removed = this.ba2Removed.get(archiveKey);
        if (removed?.has(norm)) {
            removed.delete(norm);
            if (removed.size === 0) this.ba2Removed.delete(archiveKey);
            if (!this.ba2AllPaths.has(archiveKey)) this.ba2AllPaths.set(archiveKey, new Set());
            this.ba2AllPaths.get(archiveKey).add(norm);
            this.ba2TotalPaths.set(archiveKey, this.ba2AllPaths.get(archiveKey).size);
            if (!this.ba2Contents.some(e => this.normalizeBa2InternalPath(e.path) === norm)) {
                this.ba2Contents.push({ path: norm, unpackedSize: 0, added: true });
            }
        }
        if (!this.ba2Replacements.has(archiveKey)) this.ba2Replacements.set(archiveKey, new Map());
        this.ba2Replacements.get(archiveKey).set(norm, source);
        this.previewCache.delete(`${archiveKey}|${norm}`);
        this.previewCache.delete(`${archiveKey}|${norm}|${source}`);
        this.enforceReplacementSelections(archiveKey);
    }

    collectPathsForRemoval(archiveKey, filePath, folderPath) {
        const paths = [];
        if (filePath) {
            const norm = this.normalizeBa2InternalPath(filePath);
            if (norm) paths.push(norm);
            return paths;
        }
        if (folderPath == null) return paths;
        const prefix = folderPath ? `${this.normalizeBa2InternalPath(folderPath)}/` : '';
        const folderNorm = folderPath ? this.normalizeBa2InternalPath(folderPath) : '';
        const allPaths = this.ba2AllPaths.get(archiveKey);
        if (allPaths) {
            for (const path of allPaths) {
                if (!folderNorm || path === folderNorm || path.startsWith(prefix)) paths.push(path);
            }
        } else {
            for (const entry of this.ba2Contents || []) {
                const norm = this.normalizeBa2InternalPath(entry.path);
                if (!folderNorm || norm === folderNorm || norm.startsWith(prefix)) paths.push(norm);
            }
        }
        return paths;
    }

    removeEntries(archiveKey, paths) {
        if (!archiveKey || !paths?.length) return;
        if (!this.ba2Removed.has(archiveKey)) this.ba2Removed.set(archiveKey, new Set());
        const removed = this.ba2Removed.get(archiveKey);
        const allPaths = this.ba2AllPaths.get(archiveKey) || new Set();
        const selected = this.getSelectionForArchive(archiveKey);
        let removedCount = 0;
        const removedSet = new Set();

        for (const raw of paths) {
            const norm = this.normalizeBa2InternalPath(raw);
            if (!norm || removed.has(norm)) continue;
            removed.add(norm);
            removedSet.add(norm);
            allPaths.delete(norm);
            selected.delete(norm);
            this.clearReplacement(archiveKey, norm);
            removedCount++;
        }
        if (removedCount === 0) return;

        this.ba2AllPaths.set(archiveKey, allPaths);
        this.ba2Selections.set(archiveKey, selected);
        this.ba2TotalPaths.set(archiveKey, allPaths.size);
        this.ba2Contents = (this.ba2Contents || []).filter(
            e => !removedSet.has(this.normalizeBa2InternalPath(e.path))
        );
        this.inspectorFileCount = Math.max(0, (this.inspectorFileCount || 0) - removedCount);

        if (this.previewPath && removedSet.has(this.normalizeBa2InternalPath(this.previewPath))) {
            this.clearPreview();
        }
        this.afterSelectionChange();
        showBundleStatus('success', window._t('bundle_removed_entries', removedCount));
    }

    restoreRemoved(archiveKey) {
        if (!archiveKey) return;
        const removed = this.ba2Removed.get(archiveKey);
        if (!removed?.size) return;

        if (!this.ba2AllPaths.has(archiveKey)) this.ba2AllPaths.set(archiveKey, new Set());
        const allPaths = this.ba2AllPaths.get(archiveKey);
        const selected = new Set(this.getSelectionForArchive(archiveKey));
        for (const path of removed) {
            allPaths.add(path);
            selected.add(path);
        }
        this.ba2AllPaths.set(archiveKey, allPaths);
        this.ba2Selections.set(archiveKey, selected);
        this.ba2TotalPaths.set(archiveKey, allPaths.size);
        this.ba2Removed.delete(archiveKey);

        if (this.inspectedBa2Path && this.normalizeArchiveKey(this.inspectedBa2Path) === archiveKey) {
            this.inspectBa2(this.inspectedBa2Path);
        } else if (this.inspectedFolderPath && this.normalizeFolderKey(this.inspectedFolderPath) === archiveKey) {
            this.inspectFolder(this.inspectedFolderPath);
        } else {
            this.afterSelectionChange();
        }
    }

    applyRemovedFilterToContents(archiveKey, entries) {
        const removed = this.ba2Removed.get(archiveKey);
        if (!removed?.size || !Array.isArray(entries)) return { entries: entries || [], removedCount: 0 };
        const filtered = [];
        let removedCount = 0;
        for (const entry of entries) {
            const norm = this.normalizeBa2InternalPath(entry?.path);
            if (norm && removed.has(norm)) {
                removedCount++;
                continue;
            }
            filtered.push(entry);
        }
        return { entries: filtered, removedCount };
    }

    clearReplacement(archiveKey, internalPath) {
        const map = this.ba2Replacements.get(archiveKey);
        if (!map) return;
        const norm = this.normalizeBa2InternalPath(internalPath);
        const source = map.get(norm);
        map.delete(norm);
        if (map.size === 0) this.ba2Replacements.delete(archiveKey);
        this.previewCache.delete(`${archiveKey}|${norm}`);
        if (source) this.previewCache.delete(`${archiveKey}|${norm}|${source}`);
    }

    enforceReplacementSelections(onlyKey = '') {
        for (const [archiveKey, map] of this.ba2Replacements) {
            if (onlyKey && archiveKey !== onlyKey) continue;
            if (!this.ba2Selections.has(archiveKey)) continue;
            const allPaths = this.ba2AllPaths.get(archiveKey);
            const set = new Set(this.getSelectionForArchive(archiveKey));
            for (const path of map.keys()) {
                if (!allPaths || allPaths.has(path)) set.add(path);
            }
            this.ba2Selections.set(archiveKey, set);
        }
    }

    promptReplaceFile(internalPath) {
        if (!window.chrome?.webview || !this.inspectedBa2Path || !internalPath) return;
        this._closeFileEntryContextMenu();
        window.chrome.webview.postMessage({
            type: 'BUNDLE_PICK_REPLACEMENT',
            archive: this.toHostArchiveRef(this.inspectedBa2Path),
            path: internalPath
        });
    }

    undoReplaceFile(internalPath) {
        const archiveKey = this.getArchiveSelectionKey();
        this._closeFileEntryContextMenu();
        if (!archiveKey || !internalPath) return;
        this.clearReplacement(archiveKey, internalPath);
        if (this.previewPath === this.normalizeBa2InternalPath(internalPath)) {
            this.previewFile(this.previewPath);
        }
        this.afterSelectionChange();
    }

    handleReplacementSelected(data) {
        const archiveKey = this.normalizeArchiveKey(data.archive);
        if (!archiveKey || !this.isStagedBa2Path(data.archive)) return;
        const norm = this.normalizeBa2InternalPath(data.path);
        this.rememberOverlaySourceSize(data.source, data.size);
        this.setReplacement(archiveKey, norm, data.source);
        if (archiveKey === this.getArchiveSelectionKey()) {
            this.afterSelectionChange();
            if (this.previewPath === norm) this.previewFile(norm);
        } else {
            this.updateStagedPanelBadges();
        }
        const fileName = String(data.path || '').split('/').pop();
        showBundleStatus('success', window._t('bundle_replace_done', fileName));
    }

    handleOverlayFiles(data) {
        const archiveKey = this.normalizeArchiveKey(data.archive);
        if (!archiveKey || !this.isStagedBa2Path(data.archive)) return;
        const files = Array.isArray(data.files) ? data.files : [];
        if (files.length === 0) return;

        let replaced = 0;
        let added = 0;
        const allPaths = this.ba2AllPaths.get(archiveKey);
        for (const file of files) {
            const path = this.normalizeBa2InternalPath(file?.path);
            const source = file?.source;
            if (!path || !source) continue;
            const existed = allPaths?.has(path);
            this.rememberOverlaySourceSize(source, file?.size);
            this.setReplacement(archiveKey, path, source);
            if (existed) replaced++;
            else added++;
        }

        if (archiveKey === this.getArchiveSelectionKey()) {
            this.afterSelectionChange();
        } else {
            this.updateStagedPanelBadges();
        }

        const archiveName = String(data.archive || '').split(/[\\/]/).pop();
        showBundleStatus('success', window._t('bundle_overlay_result', added, replaced, archiveName));
    }

    getModPartners(originalName) {
        const mods = this.data?.mods || window.app?.realData?.mods || [];
        const mod = mods.find(m => m?.originalName === originalName);
        return Array.isArray(mod?.bundlePartners) ? mod.bundlePartners.filter(Boolean) : [];
    }

    toHostArchiveRef(path) {
        if (!path) return path;
        const key = this.normalizeArchiveKey(path);
        for (const src of this.collectBa2Sources()) {
            if (src.key === key) return src.modRef;
        }
        return path;
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
            if (e.target.closest('.ba2-preview-pane')) return;
            const archiveKey = this.getArchiveSelectionKey();
            const isInspecting = Boolean(this.inspectedBa2Path || this.inspectedFolderPath);
            const hasStaged = (this.selectedMods?.size || 0) + (this.externalFiles?.size || 0) + (this.selectedFolders?.size || 0) > 0;
            const showInspectItems = Boolean(archiveKey && isInspecting && !this.inspectorLoading
                && (this.ba2Contents?.length || this.ba2TotalPaths.has(archiveKey)));

            e.preventDefault();
            e.stopPropagation();
            this._closeFileEntryContextMenu();

            const { all, none } = showInspectItems ? this.getArchiveSelectionMenuState() : { all: false, none: true };
            const canExtract = showInspectItems ? this.canExtractSelection() : false;
            const isBa2 = Boolean(this.inspectedBa2Path);
            const fileRow = showInspectItems ? e.target.closest('.ba2-tree-file[data-preview-path]') : null;
            const fileRowPath = fileRow?.dataset.previewPath || '';
            const fileRowReplaced = Boolean(fileRowPath && this.getReplacement(archiveKey, fileRowPath));
            const fileRowIsAddition = fileRowReplaced && this.isOverlayAddition(archiveKey, fileRowPath);
            const folderHeader = showInspectItems && !fileRow
                ? e.target.closest('.ba2-tree-folder-row')
                : null;
            const folderRowPath = folderHeader?.closest('.ba2-tree-folder[data-folder-path]')?.dataset.folderPath || '';
            const archiveRootRow = showInspectItems && isBa2 && !fileRow
                ? e.target.closest('.ba2-tree-archive-root')
                : null;
            const overlayTargetDir = isBa2 ? (folderRowPath || (archiveRootRow ? '' : null)) : null;
            const showOverlayHere = overlayTargetDir !== null;
            const canRemoveEntry = Boolean(showInspectItems && (fileRowPath || folderHeader));
            const removePaths = canRemoveEntry
                ? this.collectPathsForRemoval(archiveKey, fileRowPath || '', fileRowPath ? null : (folderRowPath || ''))
                : [];
            const removeTargetPath = fileRowPath || folderRowPath || '';
            const removeTargetName = removeTargetPath.split('/').filter(Boolean).pop() || removeTargetPath;
            const removeLabel = !canRemoveEntry || removePaths.length === 0
                ? ''
                : fileRowPath
                    ? window._t('bundle_remove_file', shortenMenuName(removeTargetName))
                    : window._t('bundle_remove_folder', shortenMenuName(removeTargetName), removePaths.length);
            const removedCount = showInspectItems ? (this.ba2Removed.get(archiveKey)?.size || 0) : 0;
            const expandPaths = showInspectItems ? BundleRenderer.collectFolderPaths(this, {
                foldersWithSubfoldersOnly: this.getFilteredEntryCount() > 5000
            }) : [];
            const allExpanded = expandPaths.length === 0
                || expandPaths.every(path => this.inspectorExpandedDirs.has(path));
            const noneExpanded = this.inspectorExpandedDirs.size === 0;
            const menu = document.createElement('div');
            menu.className = 'bundle-menu-dropdown file-entry-context-menu';
            menu.innerHTML = `
                ${fileRowPath && isBa2 ? `
                <div class="bundle-menu-item" data-action="replace-file">
                    <i data-lucide="replace"></i>
                    <span>${window._t('bundle_replace_file')}</span>
                </div>
                ${fileRowReplaced ? `
                <div class="bundle-menu-item" data-action="undo-replace">
                    <i data-lucide="undo-2"></i>
                    <span>${window._t(fileRowIsAddition ? 'bundle_remove_added' : 'bundle_undo_replace')}</span>
                </div>` : ''}
                <div class="bundle-menu-divider" role="separator"></div>` : ''}
                ${showOverlayHere ? `
                <div class="bundle-menu-item" data-action="add-files-here">
                    <i data-lucide="file-plus"></i>
                    <span>${window._t('bundle_add_files_here')}</span>
                </div>
                <div class="bundle-menu-item" data-action="add-folder-here">
                    <i data-lucide="folder-input"></i>
                    <span>${window._t('bundle_add_folder_here')}</span>
                </div>
                <div class="bundle-menu-divider" role="separator"></div>` : ''}
                <div class="bundle-menu-item" data-action="add-external">
                    <i data-lucide="plus"></i>
                    <span>${window._t('add_external_ba2')}</span>
                </div>
                <div class="bundle-menu-item" data-action="add-internal">
                    <i data-lucide="list-plus"></i>
                    <span>${window._t('add_mod_from_list')}</span>
                </div>
                <div class="bundle-menu-item" data-action="add-folder">
                    <i data-lucide="folder-plus"></i>
                    <span>${window._t('add_folder')}</span>
                </div>
                ${showInspectItems ? `
                <div class="bundle-menu-divider" role="separator"></div>
                <div class="bundle-menu-item${all ? ' bundle-menu-item--muted' : ''}" data-action="select-all">
                    <i data-lucide="list-checks"></i>
                    <span>${window._t('inspector_select_all')}</span>
                </div>
                <div class="bundle-menu-item${none ? ' bundle-menu-item--muted' : ''}" data-action="deselect-all">
                    <i data-lucide="list-minus"></i>
                    <span>${window._t('inspector_deselect_all')}</span>
                </div>
                <div class="bundle-menu-divider" role="separator"></div>
                <div class="bundle-menu-item${allExpanded ? ' bundle-menu-item--muted' : ''}" data-action="expand-all">
                    <i data-lucide="chevrons-up-down"></i>
                    <span>${window._t('inspector_expand_all')}</span>
                </div>
                <div class="bundle-menu-item${noneExpanded ? ' bundle-menu-item--muted' : ''}" data-action="collapse-all">
                    <i data-lucide="chevrons-down-up"></i>
                    <span>${window._t('inspector_collapse_all')}</span>
                </div>
                ${isBa2 ? `
                <div class="bundle-menu-item${canExtract ? '' : ' bundle-menu-item--muted'}" data-action="isolate">
                    <i data-lucide="package-plus"></i>
                    <span>${window._t('bundle_add_to_bundle')}</span>
                </div>
                <div class="bundle-menu-item${canExtract ? '' : ' bundle-menu-item--muted'}" data-action="extract">
                    <i data-lucide="folder-open"></i>
                    <span>${window._t('bundle_extract_to_folder')}</span>
                </div>` : ''}` : ''}
                ${removeLabel || removedCount > 0 || hasStaged ? `
                <div class="bundle-menu-divider" role="separator"></div>` : ''}
                ${removedCount > 0 ? `
                <div class="bundle-menu-item" data-action="restore-removed">
                    <i data-lucide="undo-2"></i>
                    <span>${window._t('bundle_restore_removed', removedCount)}</span>
                </div>` : ''}
                ${removeLabel ? `
                <div class="bundle-menu-item danger" data-action="remove-entry" title="${escapeMenuText(removeTargetPath)}">
                    <i data-lucide="${fileRowPath ? 'file-x' : 'folder-x'}"></i>
                    <span>${escapeMenuText(removeLabel)}</span>
                </div>` : ''}
                ${hasStaged ? `
                <div class="bundle-menu-item danger" data-action="clear-workspace">
                    <i data-lucide="eraser"></i>
                    <span>${window._t('clear_workspace')}</span>
                </div>` : ''}
            `;
            document.body.appendChild(menu);
            if (window.lucide) window.lucide.createIcons();

            menu.style.position = 'fixed';
            menu.style.left = `${e.clientX}px`;
            menu.style.top = `${e.clientY}px`;
            const menuRect = menu.getBoundingClientRect();
            menu.style.left = `${Math.max(8, Math.min(e.clientX, window.innerWidth - menuRect.width - 8))}px`;
            menu.style.top = `${Math.max(8, Math.min(e.clientY, window.innerHeight - menuRect.height - 8))}px`;

            const addInternalItem = menu.querySelector('[data-action="add-internal"]');
            const addExternalItem = menu.querySelector('[data-action="add-external"]');
            const addFolderItem = menu.querySelector('[data-action="add-folder"]');
            const addFilesHereItem = menu.querySelector('[data-action="add-files-here"]');
            const addFolderHereItem = menu.querySelector('[data-action="add-folder-here"]');
            const replaceFileItem = menu.querySelector('[data-action="replace-file"]');
            const undoReplaceItem = menu.querySelector('[data-action="undo-replace"]');

            if (replaceFileItem) {
                replaceFileItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.promptReplaceFile(fileRowPath);
                };
            }

            if (undoReplaceItem) {
                undoReplaceItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.undoReplaceFile(fileRowPath);
                };
            }

            if (addFilesHereItem) {
                addFilesHereItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.promptOverlayIntoTree(overlayTargetDir || '', 'files');
                };
            }

            if (addFolderHereItem) {
                addFolderHereItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.promptOverlayIntoTree(overlayTargetDir || '', 'folder');
                };
            }

            if (addInternalItem) {
                addInternalItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this._closeFileEntryContextMenu();
                    this.openInternalModPicker();
                };
            }

            if (addExternalItem) {
                addExternalItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this._closeFileEntryContextMenu();
                    this.promptAddExternalBa2();
                };
            }

            if (addFolderItem) {
                addFolderItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this._closeFileEntryContextMenu();
                    void this.promptAddFolder();
                };
            }

            const removeEntryItem = menu.querySelector('[data-action="remove-entry"]');
            const restoreRemovedItem = menu.querySelector('[data-action="restore-removed"]');
            const selectAllItem = menu.querySelector('[data-action="select-all"]');
            const deselectAllItem = menu.querySelector('[data-action="deselect-all"]');
            const expandAllItem = menu.querySelector('[data-action="expand-all"]');
            const collapseAllItem = menu.querySelector('[data-action="collapse-all"]');
            const isolateItem = menu.querySelector('[data-action="isolate"]');
            const extractItem = menu.querySelector('[data-action="extract"]');
            const clearWorkspaceItem = menu.querySelector('[data-action="clear-workspace"]');

            if (removeEntryItem) {
                removeEntryItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.removeEntries(archiveKey, removePaths);
                    this._closeFileEntryContextMenu();
                };
            }

            if (restoreRemovedItem) {
                restoreRemovedItem.onclick = (ev) => {
                    ev.stopPropagation();
                    this.restoreRemoved(archiveKey);
                    this._closeFileEntryContextMenu();
                };
            }

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

            if (expandAllItem) {
                expandAllItem.onclick = (ev) => {
                    ev.stopPropagation();
                    if (allExpanded) return;
                    this.expandAllInspectorFolders(expandPaths);
                    this._closeFileEntryContextMenu();
                };
            }

            if (collapseAllItem) {
                collapseAllItem.onclick = (ev) => {
                    ev.stopPropagation();
                    if (noneExpanded) return;
                    this.collapseAllInspectorFolders();
                    this._closeFileEntryContextMenu();
                };
            }

            if (isolateItem) {
                isolateItem.onclick = (ev) => {
                    ev.stopPropagation();
                    if (!this.canExtractSelection()) return;
                    this.isolateSelection();
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

    getFolderOverrides() {
        const parts = [];
        for (const folder of this.selectedFolders) {
            const key = this.normalizeFolderKey(folder);
            const paths = this.ba2Selections.get(key) || this.ba2AllPaths.get(key);
            parts.push(`${key}:${paths ? paths.size : -1}:${this.folderDataPaths.has(key) ? 1 : 0}`);
        }
        const signature = parts.join('|');
        if (this._folderOverrideCache?.signature === signature) return this._folderOverrideCache.map;

        const map = new Map();
        for (const folder of this.selectedFolders) {
            const key = this.normalizeFolderKey(folder);
            const paths = this.ba2Selections.get(key) || this.ba2AllPaths.get(key);
            const dataPaths = this.folderDataPaths.get(key);
            if (!paths || !dataPaths) continue;
            const folderName = String(folder).replace(/[\\/]+$/, '').split(/[\\/]/).pop();
            for (const p of paths) {
                const dataPath = dataPaths.get(p);
                if (dataPath) map.set(dataPath, folderName);
            }
        }
        this._folderOverrideCache = { signature, map };
        return map;
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

        for (const mod of this.selectedMods) {
            if (this.isLooseModKey(mod)) continue;
            addSource(mod, mod);
            const base = String(mod || '').replace(/\.ba2$/i, '');
            if (base && !/ - Textures$/i.test(base)) {
                const texName = `${base} - Textures.ba2`;
                const mods = this.data?.mods || window.app?.realData?.mods || [];
                const hasTex = mods.some(m => {
                    const n = String(m?.originalName || m?.name || '');
                    return this.normalizeArchiveKey(n) === this.normalizeArchiveKey(texName);
                });
                if (hasTex) addSource(texName, texName);
            }
        }
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
        const replacements = [];

        for (const src of this.collectBa2Sources()) {
            const replaced = this.ba2Replacements.get(src.key);
            if (replaced) {
                for (const [path, source] of replaced) {
                    replacements.push({ archive: src.modRef, path, source });
                }
            }

            if (!this.ba2TotalPaths.has(src.key)) {
                fullMods.push(src.modRef);
                continue;
            }

            const effectivePaths = this.getEffectiveSelectionPaths(src.key);
            if (replaced) {
                const allPaths = this.ba2AllPaths.get(src.key);
                for (const path of replaced.keys()) {
                    if (allPaths?.has(path) && !effectivePaths.includes(path)) effectivePaths.push(path);
                }
            }
            if (effectivePaths.length === 0) continue;

            const totalInArchive = this.ba2TotalPaths.get(src.key) || 0;
            const isFullArchive = effectivePaths.length >= totalInArchive
                && !(this.inspectorFilter && src.key === this.getArchiveSelectionKey())
                && !this.ba2Removed.get(src.key)?.size;

            if (isFullArchive) {
                fullMods.push(src.modRef);
            } else {
                ba2Partial.push({
                    archive: src.modRef,
                    paths: effectivePaths
                });
            }
        }

        const fullFolders = [];
        const folderPartial = [];

        for (const folder of this.selectedFolders) {
            const key = this.normalizeFolderKey(folder);
            const replaced = this.ba2Replacements.get(key);
            const dataPaths = this.folderDataPaths.get(key);
            if (replaced && dataPaths) {
                for (const [relPath, source] of replaced) {
                    const dataPath = dataPaths.get(relPath);
                    if (!dataPath) continue;
                    replacements.push({ archive: folder, path: dataPath, source });
                }
            }

            if (!this.ba2TotalPaths.has(key)) {
                fullFolders.push(folder);
                continue;
            }

            const effectivePaths = this.getEffectiveSelectionPaths(key);
            if (replaced) {
                const allPaths = this.ba2AllPaths.get(key);
                for (const path of replaced.keys()) {
                    if (allPaths?.has(path) && !effectivePaths.includes(path)) effectivePaths.push(path);
                }
            }
            if (effectivePaths.length === 0) continue;

            const totalInFolder = this.ba2TotalPaths.get(key) || 0;
            const isFullFolder = effectivePaths.length >= totalInFolder
                && !(this.inspectorFilter && key === this.getArchiveSelectionKey())
                && !this.ba2Removed.get(key)?.size;

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
            replacements,
            compression,
            format
        };
        if (this.editingBundle) payload.editBundle = this.editingBundle.key;

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
            archive: this.toHostArchiveRef(this.inspectedBa2Path)
        };

        if (effectivePaths.length < totalInArchive || this.ba2Removed.get(archiveKey)?.size) {
            payload.paths = effectivePaths;
        }

        return payload;
    }

    buildIsolatePayload() {
        const archiveKey = this.getArchiveSelectionKey();
        if (!archiveKey || !this.inspectedBa2Path) return null;

        const effectivePaths = this.getEffectiveSelectionPaths(archiveKey);
        if (effectivePaths.length === 0) return null;

        const selected = new Set(effectivePaths);
        const replacements = [];
        const replaced = this.ba2Replacements.get(archiveKey);
        if (replaced) {
            for (const [path, source] of replaced) {
                if (selected.has(path) || this.isOverlayAddition(archiveKey, path)) {
                    replacements.push({ path, source });
                }
            }
        }

        return {
            type: 'BUNDLE_ISOLATE_SELECTION',
            archive: this.toHostArchiveRef(this.inspectedBa2Path),
            paths: effectivePaths,
            replacements
        };
    }

    promptExtractToFolder() {
        if (!window.chrome?.webview) return;
        this._closeBundleActionsMenu();
        this._closeFileEntryContextMenu();

        const payload = this.buildExtractPayload();
        if (!payload) {
            showBundleStatus('error', window._t('bundle_nothing_to_extract'));
            return;
        }

        window.chrome.webview.postMessage(payload);
    }

    isolateSelection() {
        if (!window.chrome?.webview) return;
        this._closeBundleActionsMenu();
        this._closeFileEntryContextMenu();

        const payload = this.buildIsolatePayload();
        if (!payload) {
            showBundleStatus('error', window._t('bundle_nothing_to_extract'));
            return;
        }

        const archiveName = String(this.inspectedBa2Path || '').split(/[\\/]/).pop() || payload.archive;
        showBundleStatus('info', window._t('bundle_isolate_started', payload.paths.length, archiveName));
        window.chrome.webview.postMessage(payload);
    }

    removeArchiveFromWorkspace(archiveRef) {
        if (!archiveRef) return;
        const targetKey = this.normalizeArchiveKey(this.resolveArchivePath(archiveRef) || archiveRef);
        const matches = (value) => {
            const key = this.normalizeArchiveKey(value);
            if (key === targetKey) return true;
            return this.normalizeArchiveKey(this.resolveArchivePath(value) || value) === targetKey;
        };

        for (const mod of [...this.selectedMods]) {
            if (matches(mod)) this.selectedMods.delete(mod);
        }
        for (const file of [...this.externalFiles]) {
            if (matches(file)) {
                this.externalFiles.delete(file);
                this.forgetLooseStagedArchive(file);
            }
        }

        this.ba2Selections.delete(targetKey);
        this.ba2TotalPaths.delete(targetKey);
        this.ba2AllPaths.delete(targetKey);
        this.ba2Replacements.delete(targetKey);
        this.ba2Removed.delete(targetKey);
        this.folderDataPaths?.delete?.(targetKey);

        if (this.inspectedBa2Path && matches(this.inspectedBa2Path)) {
            this.resetInspector();
        }
    }

    applyIsolatedSelection(archiveRef, folderPath, count) {
        if (!folderPath) return;
        this.removeArchiveFromWorkspace(archiveRef);
        this.selectedFolders.add(folderPath);
        this.isolatedFolders.add(folderPath);
        this.pendingAutoInspectFolderPath = folderPath;
        const archiveName = String(archiveRef || '').split(/[\\/]/).pop() || archiveRef;
        showBundleStatus('success', window._t('bundle_isolate_success', count || 0, archiveName));
        this.refresh();
    }

    resetInspector() {
        this.clearInspectorLoadTimeout();
        this.inspectedBa2Path = '';
        this.inspectedResolvedPath = '';
        this.inspectedFolderPath = '';
        this.ba2Contents = [];
        this.inspectorArchiveType = '';
        this.inspectorFileCount = 0;
        this.inspectorLoading = false;
        this.inspectorExpandedDirs = new Set();
        this.inspectorFilter = '';
        this.clearPreview();
    }

    clearPreview() {
        this.disposeMeshViewer();
        this.disposeAudioPlayer();
        this._textView?.destroy();
        this._textView = null;
        this._textViewPath = '';
        this.previewPath = '';
        this.previewResult = null;
        this.previewLoading = false;
        this.previewMaterialZoom = null;
        this._meshWireframe = false;
        this.previewRequestId += 1;
    }

    disposeMeshViewer() {
        if (this._meshViewer) {
            this._meshViewer.dispose();
            this._meshViewer = null;
        }
        this._meshAnimToggle = null;
    }

    disposeAudioPlayer() {
        this._audioToggle = null;
        if (this._audioProgressRaf != null) {
            cancelAnimationFrame(this._audioProgressRaf);
            this._audioProgressRaf = null;
        }
        const el = document.getElementById('ba2-preview-audio');
        if (el) {
            try { el.pause(); el.removeAttribute('src'); el.load(); } catch (_) { /* noop */ }
        }
        if (this._audioBlobUrl) {
            URL.revokeObjectURL(this._audioBlobUrl);
            this._audioBlobUrl = null;
        }
    }

    bindAudioControls(audio) {
        if (!audio || audio.dataset.controlsBound === '1') return;
        const playBtn = document.getElementById('ba2-audio-play');
        const timeLabel = document.getElementById('ba2-audio-time');
        const seek = document.getElementById('ba2-audio-seek');
        const muteBtn = document.getElementById('ba2-audio-mute');
        const volume = document.getElementById('ba2-audio-volume');
        const loopBtn = document.getElementById('ba2-audio-loop');
        const player = document.getElementById('ba2-audio-player');
        if (!playBtn || !timeLabel || !seek) return;
        audio.dataset.controlsBound = '1';

        const fallbackDuration = Number(this.previewResult?.audio?.duration) || 0;
        let seeking = false;

        const totalSeconds = () => {
            const d = Number(audio.duration);
            return Number.isFinite(d) && d > 0 ? d : fallbackDuration;
        };
        const renderPlayState = () => {
            const playing = !audio.paused && !audio.ended;
            const playIcon = document.getElementById('ba2-audio-icon-play');
            const pauseIcon = document.getElementById('ba2-audio-icon-pause');
            if (playIcon) playIcon.hidden = playing;
            if (pauseIcon) pauseIcon.hidden = !playing;
            const label = window._t(playing ? 'preview_audio_pause' : 'preview_audio_play');
            playBtn.title = window._t('preview_shortcut_space', label);
            playBtn.setAttribute('aria-label', label);
        };
        const renderMuteState = () => {
            const muted = audio.muted || audio.volume === 0;
            const volumeIcon = document.getElementById('ba2-audio-icon-volume');
            const mutedIcon = document.getElementById('ba2-audio-icon-muted');
            if (volumeIcon) volumeIcon.hidden = muted;
            if (mutedIcon) mutedIcon.hidden = !muted;
            if (muteBtn) {
                const label = window._t(muted ? 'preview_audio_unmute' : 'preview_audio_mute');
                muteBtn.title = label;
                muteBtn.setAttribute('aria-label', label);
            }
        };
        const renderProgress = () => {
            const total = totalSeconds();
            const current = Number(audio.currentTime) || 0;
            timeLabel.textContent = `${formatPlayerTime(current)} / ${formatPlayerTime(total)}`;
            if (!seeking && total > 0) {
                seek.value = String(Math.round((current / total) * 10000));
                seek.style.setProperty('--value', `${(current / total) * 100}%`);
            }
        };
        const stopProgressLoop = () => {
            if (this._audioProgressRaf != null) {
                cancelAnimationFrame(this._audioProgressRaf);
                this._audioProgressRaf = null;
            }
        };
        const startProgressLoop = () => {
            stopProgressLoop();
            const tick = () => {
                renderProgress();
                if (!audio.paused && !audio.ended) {
                    this._audioProgressRaf = requestAnimationFrame(tick);
                } else {
                    this._audioProgressRaf = null;
                }
            };
            this._audioProgressRaf = requestAnimationFrame(tick);
        };

        audio.volume = this._audioVolume;
        if (volume) {
            volume.value = String(Math.round(this._audioVolume * 100));
            volume.style.setProperty('--value', `${this._audioVolume * 100}%`);
        }

        const togglePlay = () => {
            if (audio.paused || audio.ended) {
                audio.play().catch(() => { /* source not ready yet */ });
            } else {
                audio.pause();
            }
        };

        playBtn.addEventListener('click', togglePlay);
        audio.addEventListener('play', () => { renderPlayState(); startProgressLoop(); });
        audio.addEventListener('pause', () => { stopProgressLoop(); renderPlayState(); renderProgress(); });
        audio.addEventListener('ended', () => { stopProgressLoop(); renderPlayState(); renderProgress(); });
        audio.addEventListener('loadedmetadata', renderProgress);
        audio.addEventListener('timeupdate', renderProgress);
        audio.addEventListener('volumechange', renderMuteState);

        seek.addEventListener('input', () => {
            const total = totalSeconds();
            if (total <= 0) return;
            seeking = true;
            const ratio = Number(seek.value) / 10000;
            seek.style.setProperty('--value', `${ratio * 100}%`);
            audio.currentTime = ratio * total;
            timeLabel.textContent = `${formatPlayerTime(ratio * total)} / ${formatPlayerTime(total)}`;
        });
        seek.addEventListener('change', () => { seeking = false; renderProgress(); });

        if (volume) {
            volume.addEventListener('input', () => {
                const value = Number(volume.value) / 100;
                this._audioVolume = value;
                audio.volume = value;
                if (value > 0) audio.muted = false;
                volume.style.setProperty('--value', `${value * 100}%`);
                renderMuteState();
            });
        }
        if (muteBtn) {
            muteBtn.addEventListener('click', () => {
                audio.muted = !audio.muted;
                renderMuteState();
            });
        }
        if (loopBtn) {
            loopBtn.addEventListener('click', () => {
                audio.loop = !audio.loop;
                loopBtn.classList.toggle('ba2-audio-icon-btn--on', audio.loop);
                loopBtn.setAttribute('aria-pressed', audio.loop ? 'true' : 'false');
            });
        }
        this._audioToggle = togglePlay;
        this.ensureAudioSpaceHandler();

        renderPlayState();
        renderMuteState();
        renderProgress();
    }

    ensureAudioSpaceHandler() {
        if (this._audioKeyAbort) return;
        this._audioKeyAbort = new AbortController();
        document.addEventListener('keydown', (event) => {
            if (event.code !== 'Space') return;
            if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
            if (window.app?.currentSection !== 'bundle') return;
            const target = event.target;
            if (target && typeof target.closest === 'function'
                && target.closest('input, textarea, select, [contenteditable], button')) return;
            if (this._meshAnimToggle && document.getElementById('ba2-mesh-anim')) {
                event.preventDefault();
                this._meshAnimToggle();
                return;
            }
            if (!this._audioToggle) return;
            if (!document.getElementById('ba2-preview-audio')) return;
            event.preventDefault();
            this._audioToggle();
        }, { signal: this._audioKeyAbort.signal });
    }

    getAudioTreeRows() {
        return [...document.querySelectorAll('.ba2-tree-file[data-preview-path]')]
            .filter(row => AUDIO_PREVIEW_EXTENSIONS.has(String(row.dataset.previewPath).split('.').pop().toLowerCase()));
    }

    stepAudioPreview(direction) {
        const rows = this.getAudioTreeRows();
        const index = rows.findIndex(row => row.dataset.previewPath === this.previewPath);
        const target = rows[index + direction];
        if (!target) return;
        this.previewFile(target.dataset.previewPath);
        this._audioAutoplay = true;
        target.scrollIntoView({ block: 'nearest' });
    }

    bindAudioDockNav() {
        document.querySelectorAll('.ba2-audio-dock button').forEach(btn => {
            btn.onmousedown = (e) => e.preventDefault();
        });
        const prevBtn = document.getElementById('ba2-audio-prev');
        const nextBtn = document.getElementById('ba2-audio-next');
        const extractBtn = document.getElementById('ba2-audio-extract');
        if (extractBtn) extractBtn.onclick = () => this.extractPreviewFile();
        const infoBtn = document.getElementById('ba2-audio-info');
        if (infoBtn) {
            infoBtn.onclick = () => {
                this._audioInfoOpen = !this._audioInfoOpen;
                document.querySelector('.ba2-preview-pane')
                    ?.classList.toggle('ba2-preview-pane--hidden', !this._audioInfoOpen);
                infoBtn.classList.toggle('ba2-audio-icon-btn--on', this._audioInfoOpen);
                infoBtn.setAttribute('aria-pressed', this._audioInfoOpen ? 'true' : 'false');
            };
        }
        if (!prevBtn && !nextBtn) return;
        const rows = this.getAudioTreeRows();
        const index = rows.findIndex(row => row.dataset.previewPath === this.previewPath);
        if (prevBtn) {
            prevBtn.disabled = index <= 0;
            prevBtn.onclick = () => this.stepAudioPreview(-1);
        }
        if (nextBtn) {
            nextBtn.disabled = index < 0 || index >= rows.length - 1;
            nextBtn.onclick = () => this.stepAudioPreview(1);
        }
    }

    playIfAutoplay(audio) {
        if (!this._audioAutoplay) return;
        this._audioAutoplay = false;
        audio.play().catch(() => { /* autoplay blocked or not ready */ });
    }

    async mountAudioPlayer() {
        this.bindAudioDockNav();
        const el = document.getElementById('ba2-preview-audio');
        if (!el || this.previewResult?.kind !== 'audio') return;
        const url = el.dataset.audioUrl;
        if (!url) return;
        if (el.src) {
            this.bindAudioControls(el);
            return;
        }
        const requestId = this.previewRequestId;
        try {
            const response = await fetch(url);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const blob = await response.blob();
            if (requestId !== this.previewRequestId) return;
            const current = document.getElementById('ba2-preview-audio');
            if (!current) return;
            this.bindAudioControls(current);
            this._audioBlobUrl = URL.createObjectURL(blob);
            current.src = this._audioBlobUrl;
            this.playIfAutoplay(current);
        } catch (_) {
            // Blob fetch failed; let the player stream from the host URL.
            if (requestId === this.previewRequestId && document.body.contains(el)) {
                this.bindAudioControls(el);
                el.src = url;
                this.playIfAutoplay(el);
            }
        }
    }

    rememberPreview(cacheKey, result) {
        if (!cacheKey) return;
        if (this.previewCache.has(cacheKey)) this.previewCache.delete(cacheKey);
        this.previewCache.set(cacheKey, result);
        while (this.previewCache.size > 20) {
            const oldest = this.previewCache.keys().next().value;
            this.previewCache.delete(oldest);
        }
    }

    getPreviewReadTarget(path) {
        const archiveKey = this.getArchiveSelectionKey();
        const replacement = archiveKey ? this.getReplacement(archiveKey, path) : '';
        if (replacement) {
            const sepIndex = Math.max(replacement.lastIndexOf('\\'), replacement.lastIndexOf('/'));
            return {
                source: replacement.slice(0, sepIndex),
                mode: 'folder',
                path: replacement.slice(sepIndex + 1),
                archive: archiveKey,
                entryPath: path
            };
        }
        const folder = Boolean(this.inspectedFolderPath);
        return {
            source: folder ? this.inspectedFolderPath : this.toHostArchiveRef(this.inspectedBa2Path),
            mode: folder ? 'folder' : 'ba2',
            path,
            archive: archiveKey,
            entryPath: path
        };
    }

    confirmDiscardTextEdit(message = window._t('bundle_preview_unsaved_confirm')) {
        return window.appConfirm({
            title: window._t('bundle_preview_unsaved'),
            message,
            okText: window._t('bundle_preview_discard'),
            cancelText: window._t('cancel'),
            danger: true
        });
    }

    reloadPreviewIfFileChanged(data) {
        if (!this.previewPath || this._textView?.isDirty()) return;
        const target = this.getPreviewReadTarget(this.previewPath);
        if (target.mode !== 'folder') return;
        const changed = this.normalizeBa2InternalPath(data?.path || '');
        const current = this.normalizeBa2InternalPath(target.path || '');
        if (!changed || changed !== current) return;
        if (data?.source && this.normalizeFolderKey(data.source) !== this.normalizeFolderKey(target.source)) return;
        this.previewFile(this.previewPath, { quiet: true });
    }

    previewFile(path, { force = false, quiet = false } = {}) {
        if (!path || !window.chrome?.webview) return;
        const archiveKey = this.getArchiveSelectionKey();
        if (!archiveKey) return;
        if (!force && this._textView?.isDirty()) {
            if (path === this.previewPath) return;
            void this.confirmDiscardTextEdit().then(ok => {
                if (!ok) return;
                this._textView?.destroy();
                this._textView = null;
                this.previewFile(path, { force: true });
            });
            return;
        }

        const replacement = this.getReplacement(archiveKey, path);
        const cacheKey = replacement ? `${archiveKey}|${path}|${replacement}` : `${archiveKey}|${path}`;
        const modelCacheKey = this._meshModelRequest ? `${cacheKey}|model` : cacheKey;
        this.previewCacheKey = modelCacheKey;
        this._audioAutoplay = false;
        this.previewPath = path;
        if (!quiet) {
            this.previewMaterialZoom = null;
            this._meshWireframe = false;
            if (!this._meshModelRequest) {
                this._meshModelVisible = false;
                this._meshModelLoading = false;
            }
        }
        const target = this.getPreviewReadTarget(path);
        // Loose files change on disk. Replaying the last decode is what kept the old emote
        // on screen until the app was restarted.
        const liveDisk = target.mode === 'folder';
        if (liveDisk) this.previewCache.delete(modelCacheKey);
        const cached = liveDisk ? null : this.previewCache.get(modelCacheKey);
        if (cached) {
            this.previewLoading = false;
            this.previewResult = cached;
            if (this._meshModelRequest) {
                this._meshModelVisible = true;
                this._meshModelLoading = false;
                this._meshModelRequest = false;
            }
            this.previewRequestId += 1;
            this.updatePreviewPane();
            return;
        }

        const keepVisible = Boolean(quiet && this.previewResult);
        this.previewLoading = !keepVisible;
        if (!keepVisible) this.previewResult = null;
        this.previewRequestId += 1;
        const requestId = this.previewRequestId;
        if (!keepVisible) this.updatePreviewPane();

        const payload = {
            type: 'BUNDLE_PREVIEW_FILE',
            source: target.source,
            mode: target.mode,
            path: target.path,
            requestId
        };
        if (this._meshModelRequest) payload.model = true;
        window.chrome.webview.postMessage(payload);
    }

    openPreviewPopout() {
        if (!window.chrome?.webview || !this.previewPath) return;
        const target = this.getPreviewReadTarget(this.previewPath);
        window.chrome.webview.postMessage({
            type: 'OPEN_BUNDLE_PREVIEW_POPOUT',
            ...target,
            name: this.previewPath.split('/').pop()
        });
    }

    mountTextView() {
        const prevState = this._textView?.getEditState() || null;
        const prevPath = this._textViewPath;
        this._textView?.destroy();
        this._textView = null;
        this._textViewPath = '';
        const pane = document.querySelector('.ba2-preview-pane');
        if (!pane || this.previewLoading || this.previewResult?.kind !== 'text' || !this.previewPath) return;
        this._textViewPath = this.previewPath;
        this._textView = mountBa2TextView(pane, {
            idPrefix: 'ba2-text-font',
            target: this.getPreviewReadTarget(this.previewPath),
            post: (msg) => window.chrome?.webview?.postMessage(msg),
            confirm: (message) => this.confirmDiscardTextEdit(message),
            onStatus: (type, text) => showBundleStatus(type, text),
            onPopout: () => this.openPreviewPopout(),
            initialEdit: prevPath === this.previewPath ? prevState : null
        });
    }

    handleTextEditMessage(data) {
        const handled = this._textView?.handleMessage(data);
        if (data.type !== 'BUNDLE_TEXT_EDIT_SAVED' || !data.archive || !data.path || !data.source) return;
        const norm = this.normalizeBa2InternalPath(data.path);
        this.rememberOverlaySourceSize(data.source, data.size);
        this.setReplacement(data.archive, norm, data.source);
        showBundleStatus('success', window._t('bundle_preview_saved', norm.split('/').pop()));
        if (!this.isCreating) return;
        if (data.archive === this.getArchiveSelectionKey()) {
            this.afterSelectionChange();
            // Only re-preview when the save came from the other window; local saves stay put.
            if (!handled && this.previewPath === norm && !this._textView?.isDirty()) {
                this.previewFile(norm, { force: true });
            }
        } else {
            this.updateStagedPanelBadges();
        }
    }

    extractPreviewFile() {
        if (!window.chrome?.webview || !this.inspectedBa2Path || !this.previewPath) return;
        this._closeBundleActionsMenu();
        this._closeFileEntryContextMenu();
        window.chrome.webview.postMessage({
            type: 'BUNDLE_EXTRACT',
            archive: this.toHostArchiveRef(this.inspectedBa2Path),
            paths: [this.previewPath],
            single: true
        });
    }

    highlightPreviewRow() {
        document.querySelectorAll('.ba2-tree-file').forEach(row => {
            row.classList.toggle('ba2-tree-file--previewing', row.dataset.previewPath === this.previewPath);
        });
    }

    bindPreviewEvents() {
        const extractBtn = document.getElementById('btn-preview-extract');
        if (extractBtn) extractBtn.onclick = () => this.extractPreviewFile();
        document.querySelectorAll('.ba2-preview-footer [data-preview-popout]').forEach(btn => {
            btn.onclick = () => this.openPreviewPopout();
        });
        this.mountTextView();

        document.querySelectorAll('[data-material-texture]').forEach(btn => {
            btn.onclick = () => {
                this.previewMaterialZoom = Number(btn.dataset.materialTexture);
                this.updatePreviewPane();
            };
        });
        const backBtn = document.getElementById('btn-preview-material-back');
        if (backBtn) {
            backBtn.onclick = () => {
                this.previewMaterialZoom = null;
                this.updatePreviewPane();
            };
        }

        const wireBtn = document.getElementById('btn-mesh-wire');
        if (wireBtn) {
            wireBtn.classList.toggle('ba2-preview-mesh-tool--on', this._meshWireframe);
            wireBtn.onclick = () => {
                this._meshWireframe = !this._meshWireframe;
                wireBtn.classList.toggle('ba2-preview-mesh-tool--on', this._meshWireframe);
                if (this._meshViewer) this._meshViewer.setWireframe(this._meshWireframe);
            };
        }
        const modelBtn = document.getElementById('btn-mesh-model');
        if (modelBtn) {
            modelBtn.onclick = () => this.toggleMeshModel();
        }
        const resetBtn = document.getElementById('btn-mesh-reset');
        if (resetBtn) resetBtn.onclick = () => this._meshViewer?.reset();
        this.bindMeshAnimControls();
    }

    toggleMeshModel() {
        if (this._meshModelLoading || !this.previewPath) return;
        const mesh = this.previewResult?.mesh;
        const hasModel = Array.isArray(mesh?.shapes) && mesh.shapes.length > 0;
        if (this._meshModelVisible && hasModel) {
            this._meshModelVisible = false;
            this.updatePreviewPane();
            return;
        }
        if (hasModel) {
            this._meshModelVisible = true;
            this.updatePreviewPane();
            return;
        }
        this._meshModelRequest = true;
        this._meshModelLoading = true;
        const btn = document.getElementById('btn-mesh-model');
        if (btn) {
            btn.disabled = true;
            btn.textContent = window._t('preview_hkx_model_loading');
        }
        this.previewFile(this.previewPath, { force: true, quiet: true });
    }

    mountMeshViewer() {
        this.disposeMeshViewer();
        const stage = document.getElementById('ba2-mesh-stage');
        const mesh = this.previewResult?.kind === 'mesh' ? this.previewResult.mesh : null;
        if (!stage || !mesh) return;
        const showModel = Boolean(this._meshModelVisible) && Array.isArray(mesh.shapes) && mesh.shapes.length > 0;
        const display = showModel
            ? { ...mesh, bones: null }
            : { ...mesh, shapes: [], textures: [], vertexCount: 0, triangleCount: 0 };
        this._meshViewer = mountMeshViewer(stage, display);
        if (this._meshWireframe) this._meshViewer.setWireframe(true);
        this.bindMeshAnimControls();
    }

    bindMeshAnimControls() {
        const viewer = this._meshViewer;
        const root = document.getElementById('ba2-mesh-anim');
        if (!viewer?.hasAnimation || !root) {
            this._meshAnimToggle = null;
            return;
        }
        const playBtn = document.getElementById('ba2-mesh-anim-play');
        const playIcon = document.getElementById('ba2-mesh-anim-icon-play');
        const pauseIcon = document.getElementById('ba2-mesh-anim-icon-pause');
        const seek = document.getElementById('ba2-mesh-anim-seek');
        const timeLabel = document.getElementById('ba2-mesh-anim-time');
        const loopBtn = document.getElementById('ba2-mesh-anim-loop');
        const duration = viewer.getDuration();

        const formatTime = (sec) => {
            const total = Math.max(0, Math.floor(sec || 0));
            const m = Math.floor(total / 60);
            const s = total % 60;
            return `${m}:${String(s).padStart(2, '0')}`;
        };

        const renderPlay = () => {
            const playing = viewer.isPlaying();
            if (playIcon) playIcon.hidden = playing;
            if (pauseIcon) pauseIcon.hidden = !playing;
            if (playBtn) {
                const label = window._t(playing ? 'preview_audio_pause' : 'preview_audio_play');
                playBtn.title = window._t('preview_shortcut_space', label);
                playBtn.setAttribute('aria-label', label);
            }
        };

        const renderProgress = () => {
            const t = viewer.getTime();
            const pct = duration > 0 ? Math.min(100, Math.max(0, (t / duration) * 100)) : 0;
            if (seek) {
                seek.value = String(Math.round(pct * 10));
                seek.style.setProperty('--value', `${pct}%`);
            }
            if (timeLabel) timeLabel.textContent = `${formatTime(t)} / ${formatTime(duration)}`;
        };

        const toggle = () => {
            viewer.toggle();
            renderPlay();
            if (viewer.isPlaying()) startProgress();
        };

        let progressRaf = 0;
        const startProgress = () => {
            if (progressRaf) return;
            const tick = () => {
                renderProgress();
                if (viewer.isPlaying()) progressRaf = requestAnimationFrame(tick);
                else progressRaf = 0;
            };
            progressRaf = requestAnimationFrame(tick);
        };

        if (playBtn) playBtn.onclick = () => toggle();
        if (loopBtn) {
            loopBtn.onclick = () => {
                const next = !viewer.isLooping();
                viewer.setLoop(next);
                loopBtn.classList.toggle('ba2-audio-icon-btn--on', next);
                loopBtn.setAttribute('aria-pressed', next ? 'true' : 'false');
            };
        }
        if (seek) {
            seek.oninput = () => {
                const pct = Number(seek.value) / 1000;
                viewer.seek(pct * duration);
                renderProgress();
            };
        }

        this._meshAnimToggle = toggle;
        this.ensureAudioSpaceHandler();
        renderPlay();
        renderProgress();
        if (viewer.isPlaying()) startProgress();
    }

    updatePreviewPane() {
        this.disposeMeshViewer();
        this.disposeAudioPlayer();
        const dockSlot = document.getElementById('ba2-audio-dock-slot');
        if (dockSlot) {
            dockSlot.innerHTML = BundleRenderer.renderAudioDockHtml(this);
            if (window.lucide) {
                try { window.lucide.createIcons({ nodes: [dockSlot] }); } catch (_) { }
            }
        }
        const pane = document.querySelector('.ba2-preview-pane');
        if (!pane) return;
        const holder = document.createElement('div');
        holder.innerHTML = BundleRenderer.renderPreviewPaneHtml(this);
        const next = holder.firstElementChild;
        if (!next) return;
        pane.replaceWith(next);
        this.bindPreviewEvents();
        this.mountMeshViewer();
        this.mountAudioPlayer();
        this.highlightPreviewRow();
        if (window.lucide) window.lucide.createIcons({ nodes: [next] });
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
            showBundleStatus('error', window._t('inspector_load_timeout'));
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
        this.clearPreview();
        this.inspectedBa2Path = path;
        this.inspectedFolderPath = '';
        this.inspectorLoading = true;
        this.updateInspectorPanel({ preserveScroll: false });
        this.scheduleInspectorLoadTimeout(path, 'ba2');
        window.chrome.webview.postMessage({
            type: 'BUNDLE_LIST_BA2',
            path: this.toHostArchiveRef(path)
        });
    }

    inspectFolder(path) {
        if (!path || !window.chrome?.webview) return;
        if (!this.isStagedFolderPath(path)) return;
        this.clearInspectorLoadTimeout();
        this.clearPreview();
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
            if (path && (!this.inspectedBa2Path || this.normalizeArchiveKey(this.inspectedBa2Path) !== this.normalizeArchiveKey(path))) {
                this.inspectedBa2Path = path;
            }
            this.inspectedResolvedPath = data.resolvedPath || '';
            this.inspectedFolderPath = '';
            const archiveKeyHint = this.normalizeArchiveKey(path || this.inspectedBa2Path);
            const filteredBa2 = this.applyRemovedFilterToContents(archiveKeyHint, Array.isArray(data.entries) ? data.entries : []);
            this.ba2Contents = filteredBa2.entries;
            this.inspectorArchiveType = data.archiveType || '';
            this.inspectorFileCount = Math.max(0, (data.fileCount ?? (Array.isArray(data.entries) ? data.entries.length : 0)) - filteredBa2.removedCount);
            this.inspectorLoading = false;
            const archiveKey = this.getArchiveSelectionKey();
            if (archiveKey && this.ba2Contents.length) {
                this.initArchiveSelection(archiveKey, this.ba2Contents);
            }
            this.ensureDefaultExpandedDirs();
            if (data.error) {
                showBundleStatus('error', data.error);
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
            const folderKeyHint = this.normalizeFolderKey(path || this.inspectedFolderPath);
            const filteredFolder = this.applyRemovedFilterToContents(folderKeyHint, Array.isArray(data.entries) ? data.entries : []);
            this.ba2Contents = filteredFolder.entries;
            this.inspectorArchiveType = '';
            this.inspectorFileCount = Math.max(0, (data.fileCount ?? (Array.isArray(data.entries) ? data.entries.length : 0)) - filteredFolder.removedCount);
            this.inspectorLoading = false;
            const folderKey = this.getArchiveSelectionKey();
            if (folderKey && this.ba2Contents.length) {
                this.initArchiveSelection(folderKey, this.ba2Contents);
                const dataPaths = new Map();
                for (const entry of this.ba2Contents) {
                    if (entry.dataPath) dataPaths.set(this.normalizeBa2InternalPath(entry.path), entry.dataPath);
                }
                this.folderDataPaths.set(folderKey, dataPaths);
            }
            this.ensureDefaultExpandedDirs();
            this.maybeAutoSwitchFormat(this.ba2Contents);
            if (data.error) {
                showBundleStatus('error', data.error);
            }
            this.updateInspectorPanel({ preserveScroll: false });
            return;
        }

        if (data.type === 'BUNDLE_PREVIEW_SOURCE_CHANGED') {
            this.reloadPreviewIfFileChanged(data);
            return;
        }

        if (data.type === 'BUNDLE_FILE_PREVIEW') {
            if (Number(data.requestId) !== this.previewRequestId) return;
            this.previewLoading = false;
            this.previewResult = data;
            if (this._meshModelRequest) {
                this._meshModelLoading = false;
                this._meshModelRequest = false;
                const shapes = data?.mesh?.shapes;
                this._meshModelVisible = Array.isArray(shapes) && shapes.length > 0;
            }
            const liveDisk = this.getPreviewReadTarget(this.previewPath).mode === 'folder';
            if (!liveDisk && this.previewPath && data.kind !== 'audio' && (!data.error || data.error === 'unsupported')) {
                this.rememberPreview(this.previewCacheKey || `${this.getArchiveSelectionKey()}|${this.previewPath}`, data);
            }
            this.updatePreviewPane();
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
        const pathFooter = document.getElementById('file-entry-path-footer');
        if (pathFooter) pathFooter.innerHTML = BundleRenderer.renderInspectorPathFooter(this);
        const headerToolbar = document.getElementById('file-entry-header-toolbar');
        if (!headerToolbar) return;
        this._inspectorMetaMinWidth = 0;
        headerToolbar.innerHTML = (this.inspectedBa2Path || this.inspectedFolderPath)
            ? BundleRenderer.renderInspectorToolbar(this)
            : '';
    }

    rerenderInspectorTree({ resetScroll = false } = {}) {
        const treeEl = document.querySelector('.file-entry-inspect .ba2-explorer-tree');
        if (!treeEl) return;
        const scrollTop = treeEl.scrollTop;
        treeEl.innerHTML = BundleRenderer.renderExplorerTreeHtml(this);
        this.bindInspectorTreeEvents(treeEl);
        if (window.lucide) {
            try { window.lucide.createIcons({ nodes: [treeEl] }); } catch (_) { }
        }
        treeEl.scrollTop = resetScroll ? 0 : scrollTop;
    }

    expandAllInspectorFolders(precomputedPaths) {
        const paths = precomputedPaths ?? BundleRenderer.collectFolderPaths(this, {
            foldersWithSubfoldersOnly: this.getFilteredEntryCount() > 5000
        });
        for (const path of paths) this.inspectorExpandedDirs.add(path);
        this.rerenderInspectorTree();
    }

    collapseAllInspectorFolders() {
        this.inspectorExpandedDirs.clear();
        this.rerenderInspectorTree();
    }

    refreshInspectorFilter() {
        this.rerenderInspectorTree({ resetScroll: true });

        const countEl = document.getElementById('ba2-inspector-count');
        if (countEl) {
            countEl.textContent = BundleRenderer.getInspectorCountLabel(this);
            const meta = countEl.closest('.bundle-inspector-pathbar-meta');
            if (meta) {
                meta.style.minWidth = '';
                const width = meta.offsetWidth;
                this._inspectorMetaMinWidth = Math.max(this._inspectorMetaMinWidth || 0, width);
                if (this._inspectorMetaMinWidth) {
                    meta.style.minWidth = `${this._inspectorMetaMinWidth}px`;
                }
            }
        }

        const canIsolateOrExtract = this.canExtractSelection();
        const isolateBtn = document.getElementById('btn-inspector-isolate');
        if (isolateBtn) isolateBtn.disabled = !canIsolateOrExtract;
        const extractBtn = document.getElementById('btn-inspector-extract');
        if (extractBtn) {
            extractBtn.disabled = !canIsolateOrExtract;
        }

        this._updateSelectionMenuItemStates();
        this.updateWizardState();
    }

    updateInspectorPanel({ preserveScroll = true } = {}) {
        const scrollRoot = document.querySelector('.file-entry-inspect .ba2-explorer-tree');
        const scrollTop = preserveScroll && scrollRoot ? scrollRoot.scrollTop : 0;

        this.updateFileEntryHeaderToolbar();
        const root = document.getElementById('ba2-inspector-root');
        if (!root) return;
        this.disposeAudioPlayer();
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
            let badge = row.querySelector('.staged-partial-badge:not(.staged-replaced-badge)');
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

        const applyReplacedBadge = (row, key) => {
            const { replaced, added } = this.getOverlayCounts(key);
            const body = row.querySelector('.bundle-staged-item-body');
            let replacedBadge = row.querySelector('.staged-replaced-badge:not(.staged-added-badge)');
            let addedBadge = row.querySelector('.staged-added-badge');

            if (replaced > 0) {
                if (!replacedBadge) {
                    replacedBadge = document.createElement('div');
                    replacedBadge.className = 'staged-partial-badge staged-replaced-badge';
                    body?.appendChild(replacedBadge);
                }
                replacedBadge.textContent = window._t('bundle_replaced_count', replaced);
            } else if (replacedBadge) {
                replacedBadge.remove();
            }

            if (added > 0) {
                if (!addedBadge) {
                    addedBadge = document.createElement('div');
                    addedBadge.className = 'staged-partial-badge staged-replaced-badge staged-added-badge';
                    body?.appendChild(addedBadge);
                }
                addedBadge.textContent = window._t('bundle_added_count', added);
            } else if (addedBadge) {
                addedBadge.remove();
            }
        };

        document.querySelectorAll('.file-entry-staged-ba2').forEach(row => {
            const path = row.dataset.path;
            if (!path) return;
            const key = this.normalizeArchiveKey(path);
            applyBadge(row, key);
            applyReplacedBadge(row, key);
        });

        document.querySelectorAll('.file-entry-staged-folder').forEach(row => {
            const path = row.dataset.path;
            if (path) applyBadge(row, this.normalizeFolderKey(path));
        });
    }

    afterSelectionChange() {
        this.enforceReplacementSelections();
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

        queryAll('.ba2-tree-file').forEach(row => {
            row.onclick = (e) => {
                if (e.target.closest('.ba2-tree-check')) return;
                const path = row.dataset.previewPath;
                if (!path) return;
                if (path === this.previewPath) {
                    this.clearPreview();
                    this.updatePreviewPane();
                    return;
                }
                this.previewFile(path);
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
            const filterClear = document.getElementById('ba2-inspector-filter-clear');
            const syncFilterClear = () => {
                if (filterClear) filterClear.hidden = !filterInput.value;
            };
            syncFilterClear();

            filterInput.oninput = (e) => {
                this.inspectorFilter = e.target.value;
                syncFilterClear();
                if (this._filterTimer) clearTimeout(this._filterTimer);
                this._filterTimer = setTimeout(() => {
                    this._filterTimer = null;
                    this.refreshInspectorFilter();
                }, 150);
            };

            filterInput.onkeydown = (e) => {
                if (e.key === 'Escape' && filterInput.value) {
                    e.preventDefault();
                    filterInput.value = '';
                    filterInput.dispatchEvent(new Event('input', { bubbles: true }));
                }
            };

            if (filterClear) {
                filterClear.onclick = () => {
                    filterInput.value = '';
                    filterInput.dispatchEvent(new Event('input', { bubbles: true }));
                    filterInput.focus();
                };
            }
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

        const isolateBtn = document.getElementById('btn-inspector-isolate');
        if (isolateBtn) {
            isolateBtn.onclick = () => {
                if (isolateBtn.disabled) return;
                this.isolateSelection();
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
        this.bindPreviewEvents();
        this.mountAudioPlayer();
    }

    loadGameBa2List() {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage({ type: 'BUNDLE_LIST_GAME_BA2S' });
        }
    }

    maybeAutoSwitchFormat(entries) {
        const list = entries || [];
        if (list.length === 0) return;

        const paths = list.map(e => String(e?.path || '').toLowerCase());
        const hasDds = paths.some(p => p.endsWith('.dds'));
        const allDds = hasDds && paths.every(p => p.endsWith('.dds'));
        const hasNonDds = paths.some(p => p && !p.endsWith('.dds'));

        const formatSelect = document.getElementById('bundle-format-select');
        if (!formatSelect) return;

        let next = formatSelect.value;
        let statusKey = null;
        if (allDds) {
            next = 'DDS';
            statusKey = 'bundle_format_auto_dds';
        } else if (hasDds && hasNonDds) {
            next = 'Auto';
            statusKey = 'bundle_format_auto_mixed';
        } else if (hasNonDds && formatSelect.value === 'DDS') {
            next = 'Auto';
            statusKey = 'bundle_format_auto_mixed';
        }

        if (next === formatSelect.value) return;
        formatSelect.value = next;
        const typeSummary = document.getElementById('bundle-type-summary');
        if (typeSummary) typeSummary.textContent = getBundleFormatLabel(next);

        if (statusKey) {
            showBundleStatus('info', window._t(statusKey));
        }
    }

    initIpcHandlers() {
        if (_bundleIpcHandlerAttached || !window.chrome?.webview) return;
        _bundleIpcHandlerAttached = true;

        window.chrome.webview.addEventListener('message', (e) => {
            try {
                const data = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
                const bundle = window.app?.sections?.bundle;
                if (data?.type === 'CONFIG_EDITOR_FONT_CHANGED') {
                    const ms = window.app?.realData?.managerSettings;
                    if (ms) {
                        ms.configEditorFontSize = data.fontSize;
                        ms.configEditorFontFamily = data.fontFamily;
                    }
                    bundle?._textView?.applyFont({ size: data.fontSize, family: data.fontFamily });
                    return;
                }
                if (!bundle) return;
                if (data?.type === 'BUNDLE_TEXT_EDIT_CONTENT'
                    || data?.type === 'BUNDLE_TEXT_EDIT_SAVED'
                    || data?.type === 'BUNDLE_TEXT_EDIT_SAVE_FAILED') {
                    bundle.handleTextEditMessage(data);
                    return;
                }
                if (!bundle.isCreating) return;

                if (data.type === 'BUNDLE_REPLACEMENT_SELECTED' && data.path && data.source) {
                    bundle.handleReplacementSelected(data);
                    return;
                }

                if (data.type === 'BUNDLE_OVERLAY_FILES' && data.archive && Array.isArray(data.files)) {
                    bundle.handleOverlayFiles(data);
                    return;
                }

                if (data.type === 'FOLDER_SELECTED' && data.path) {
                    bundle.selectedFolders.add(data.path);
                    bundle.pendingAutoInspectFolderPath = data.path;
                    bundle.refresh();
                    return;
                }

                if (data.type === 'BUNDLE_LOOSE_STAGED') {
                    bundle.handleLooseStaged(data);
                    return;
                }

                if (data.type === 'BUNDLE_LOOSE_STAGE_FAILED') {
                    bundle.handleLooseStageFailed(data);
                    return;
                }

                if (data.type === 'BUNDLE_ISOLATED' && data.path) {
                    bundle.applyIsolatedSelection(data.archive, data.path, data.count);
                    return;
                }

                if (data.type === 'BUNDLE_FILES_DROPPED') {
                    const droppedFiles = Array.isArray(data.files) ? data.files.filter(Boolean) : [];
                    const droppedFolders = Array.isArray(data.folders) ? data.folders.filter(Boolean) : [];
                    const replacements = Array.isArray(data.replacements) ? data.replacements : [];
                    const unmatched = Array.isArray(data.unmatched) ? data.unmatched.filter(Boolean) : [];
                    droppedFiles.forEach(f => bundle.externalFiles.add(f));
                    void (async () => {
                        let foldersHandled = false;
                        if (droppedFolders.length > 0 && bundle.inspectedBa2Path) {
                            foldersHandled = await bundle.offerMergeDroppedFolders(droppedFolders);
                        }
                        if (!foldersHandled) {
                            droppedFolders.forEach(f => bundle.selectedFolders.add(f));
                        }
                        if (droppedFiles.length > 0) {
                            bundle.pendingAutoInspectPath = droppedFiles[droppedFiles.length - 1];
                        } else if (!foldersHandled && droppedFolders.length > 0) {
                            bundle.pendingAutoInspectFolderPath = droppedFolders[droppedFolders.length - 1];
                        }

                        let replacedCount = 0;
                        let touchedOpen = false;
                        const openKey = bundle.getArchiveSelectionKey();
                        for (const rep of replacements) {
                            const source = rep?.source;
                            if (!source) continue;
                            const kind = String(rep.kind || '').toLowerCase();
                            if (kind === 'ba2') {
                                const archiveKey = bundle.normalizeArchiveKey(rep.archive);
                                if (!archiveKey || !bundle.isStagedBa2Path(rep.archive)) continue;
                                const norm = bundle.normalizeBa2InternalPath(rep.path);
                                if (!norm) continue;
                                bundle.rememberOverlaySourceSize(source, rep.size);
                                bundle.setReplacement(archiveKey, norm, source);
                                replacedCount++;
                                if (archiveKey === openKey) {
                                    touchedOpen = true;
                                    if (bundle.previewPath === norm) bundle.previewFile(norm);
                                }
                            } else if (kind === 'folder') {
                                const folderKey = bundle.normalizeFolderKey(rep.folder);
                                if (!folderKey || !bundle.isStagedFolderPath(rep.folder)) continue;
                                const norm = bundle.normalizeBa2InternalPath(rep.path);
                                if (!norm) continue;
                                bundle.rememberOverlaySourceSize(source, rep.size);
                                bundle.setReplacement(folderKey, norm, source);
                                if (rep.dataPath) {
                                    if (!bundle.folderDataPaths.has(folderKey)) {
                                        bundle.folderDataPaths.set(folderKey, new Map());
                                    }
                                    bundle.folderDataPaths.get(folderKey).set(norm, String(rep.dataPath).toLowerCase());
                                }
                                replacedCount++;
                                if (folderKey === openKey) {
                                    touchedOpen = true;
                                    if (bundle.previewPath === norm) bundle.previewFile(norm);
                                }
                            }
                        }
                        if (replacedCount > 0) {
                            if (touchedOpen) bundle.afterSelectionChange();
                            else bundle.updateStagedPanelBadges();
                            showBundleStatus('success', window._t('bundle_drop_replace_result', replacedCount));
                        }
                        if (unmatched.length > 0) {
                            showBundleStatus(
                                replacedCount > 0 ? 'info' : 'error',
                                window._t('bundle_drop_replace_none', unmatched.join(', '))
                            );
                        }

                        if (droppedFiles.length > 0 || (!foldersHandled && droppedFolders.length > 0)) {
                            bundle.refresh();
                        }
                    })();
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
        if (!window.handleEditBundle) {
            window.handleEditBundle = (name) => {
                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                this.startEditBundle(name);
            };
        }

        if (!window.handleRenameBundle) {
            window.handleRenameBundle = async (name) => {
                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                const currentBase = name.replace('.ba2', '');
                const newName = await window.appPrompt({
                    compact: true,
                    title: window._t('rename_bundle_title'),
                    promptLabel: window._t('rename_bundle_label'),
                    defaultValue: currentBase,
                    okText: window._t('rename'),
                    maxLength: 200
                });
                let cleanName = String(newName || '').trim();
                if (cleanName.toLowerCase().endsWith('.ba2')) cleanName = cleanName.substring(0, cleanName.length - 4);
                if (cleanName && cleanName !== currentBase) {
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

        if (!window.handleOpenBundleFolder) {
            window.handleOpenBundleFolder = (name) => {
                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                window.chrome?.webview?.postMessage({ type: 'OPEN_BUNDLE_FOLDER', name });
            };
        }

        if (!window.handleShareBundle) {
            window.handleShareBundle = (name) => {
                document.querySelectorAll('.bundle-menu-dropdown').forEach(d => d.remove());
                window.chrome?.webview?.postMessage({ type: 'SHARE_BUNDLE', name });
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
                if (this.isLooseModKey(originalName)) {
                    this.stageLooseMod(originalName);
                    showBundleStatus('success', window._t('added_to_workspace_banner'));
                    return;
                }
                this.selectedMods.add(originalName);
                for (const partner of this.getModPartners(originalName)) this.selectedMods.add(partner);
                showBundleStatus('success', window._t('added_to_workspace_banner'));
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
                        <div class="bundle-menu-item" onclick="window.handleEditBundle('${escapedName}')">
                            <i data-lucide="file-pen" style="width: 14px;"></i> ${window._t('edit_bundle')}
                        </div>
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

                const itemEdit = document.createElement('div');
                itemEdit.className = 'bundle-menu-item';
                itemEdit.innerHTML = `<i data-lucide="file-pen" style="width: 14px;"></i> ${window._t('edit_bundle')}`;
                itemEdit.onclick = (e) => {
                    e.stopPropagation();
                    menu.remove();
                    this.startEditBundle(name);
                };
                menu.appendChild(itemEdit);
                
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

                const itemOpenFolder = document.createElement('div');
                itemOpenFolder.className = 'bundle-menu-item';
                itemOpenFolder.innerHTML = `<i data-lucide="folder-open" style="width: 14px;"></i> ${window._t('bundle_open_folder')}`;
                itemOpenFolder.onclick = (e) => {
                    e.stopPropagation();
                    window.handleOpenBundleFolder(name);
                };
                menu.appendChild(itemOpenFolder);

                const itemShare = document.createElement('div');
                itemShare.className = 'bundle-menu-item';
                itemShare.innerHTML = `<i data-lucide="share-2" style="width: 14px;"></i> ${window._t('bundle_share')}`;
                itemShare.onclick = (e) => {
                    e.stopPropagation();
                    window.handleShareBundle(name);
                };
                menu.appendChild(itemShare);

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
        const menuIsolateBtn = document.getElementById('btn-menu-isolate');
        const menuExtractBtn = document.getElementById('btn-menu-extract');

        if (nameInput) {
            const nameClear = document.getElementById('bundle-name-clear');
            const syncNameClear = () => {
                if (nameClear) nameClear.hidden = !nameInput.value || nameInput.disabled;
            };
            nameInput.addEventListener('input', (e) => {
                this.bundleName = e.target.value.replace(/[^a-z0-9-_]/gi, '');
                syncNameClear();
            });
            syncNameClear();
            if (nameClear) {
                nameClear.addEventListener('click', () => {
                    nameInput.value = '';
                    nameInput.dispatchEvent(new Event('input', { bubbles: true }));
                    nameInput.focus();
                });
            }
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

        if (menuIsolateBtn) {
            menuIsolateBtn.onclick = () => {
                if (menuIsolateBtn.disabled) return;
                this.isolateSelection();
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
                if (this.isLooseModKey(name)) {
                    if (this.looseStaged.has(name)) this.unstageLooseMod(name);
                    else this.stageLooseMod(name);
                    this.syncPickerSelection();
                    return;
                }

                const isSelected = this.selectedMods.has(name);
                const group = [name, ...this.getModPartners(name)];
                for (const member of group) {
                    if (isSelected) this.selectedMods.delete(member);
                    else this.selectedMods.add(member);
                }
                this.syncPickerSelection();
            };
        });


        const startBundlingClick = () => {
            const bundleName = this.bundleName || 'MyCustomBundle';
            this.startBundling(bundleName);
        };

        document.querySelectorAll('.btn-start-bundling').forEach(btn => {
            btn.onclick = startBundlingClick;
        });

        this._mountCreateBa2Info();
        this.updateWizardState();
    }

    _mountCreateBa2Info() {
        if (this._bundleCreateInfoAbort) {
            this._bundleCreateInfoAbort.abort();
            this._bundleCreateInfoAbort = null;
        }

        const wrap = document.querySelector('.bundle-create-info-wrap');
        const btn = document.getElementById('btn-create-ba2-info');
        const popover = document.getElementById('create-ba2-info-popover');
        if (!wrap || !btn || !popover) return;

        this._bundleCreateInfoAbort = new AbortController();
        const { signal } = this._bundleCreateInfoAbort;
        let pinned = false;
        let holdClosed = false;

        const setOpen = (open) => {
            popover.hidden = !open;
            btn.setAttribute('aria-expanded', open ? 'true' : 'false');
            btn.classList.toggle('active', open);
        };

        const showIfAllowed = () => {
            if (!holdClosed) setOpen(true);
        };

        wrap.addEventListener('mouseenter', showIfAllowed, { signal });
        wrap.addEventListener('mouseleave', () => {
            holdClosed = false;
            if (!pinned && document.activeElement !== btn) setOpen(false);
        }, { signal });
        btn.addEventListener('focus', showIfAllowed, { signal });
        btn.addEventListener('blur', () => {
            holdClosed = false;
            if (!pinned && !wrap.matches(':hover')) setOpen(false);
        }, { signal });
        btn.addEventListener('click', () => {
            pinned = !pinned;
            if (pinned) {
                holdClosed = false;
                setOpen(true);
            } else {
                holdClosed = true;
                setOpen(false);
            }
        }, { signal });

        document.addEventListener('click', (e) => {
            if (!document.getElementById('btn-create-ba2-info')) return;
            if (e.target.closest('.bundle-create-info-wrap')) return;
            pinned = false;
            holdClosed = false;
            setOpen(false);
        }, { signal });

        document.addEventListener('keydown', (e) => {
            if (e.key !== 'Escape' || popover.hidden) return;
            pinned = false;
            holdClosed = true;
            setOpen(false);
            if (document.activeElement !== btn) btn.focus();
        }, { signal });
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
        const canIsolateOrExtract = this.canExtractSelection();
        const menuSelectAllBtn = document.getElementById('btn-menu-select-all');
        const menuDeselectAllBtn = document.getElementById('btn-menu-deselect-all');
        const menuIsolateBtn = document.getElementById('btn-menu-isolate');
        const menuExtractBtn = document.getElementById('btn-menu-extract');
        if (menuSelectAllBtn) menuSelectAllBtn.disabled = !canSelectArchive;
        if (menuDeselectAllBtn) menuDeselectAllBtn.disabled = !canSelectArchive;
        if (menuIsolateBtn) menuIsolateBtn.disabled = !canIsolateOrExtract;
        if (menuExtractBtn) menuExtractBtn.disabled = !canIsolateOrExtract;
        this._updateSelectionMenuItemStates();
    }

    startBundling(bundleName) {
        if (!this.canCreateBundle()) return;

        if (window.chrome && window.chrome.webview) {
            const formatSelect = document.getElementById('bundle-format-select');
            const anyUninspected = this.collectBa2Sources().some(src => !this.ba2TotalPaths.has(src.key))
                || [...this.selectedFolders].some(f => !this.ba2TotalPaths.has(this.normalizeFolderKey(f)));
            if (anyUninspected && formatSelect) {
                formatSelect.value = 'Auto';
                const typeSummary = document.getElementById('bundle-type-summary');
                if (typeSummary) typeSummary.textContent = getBundleFormatLabel('Auto');
            }

            const payload = this.buildCreateBundlePayload(bundleName);
            if (payload.mods.length === 0 && payload.ba2Partial.length === 0 && payload.folders.length === 0 && payload.folderPartial.length === 0) {
                showBundleStatus('error', window._t('bundle_nothing_to_pack'));
                return;
            }

            window.chrome.webview.postMessage(payload);
            
            showBundleStatus('info', this.editingBundle
                ? window._t('saving_bundle_banner', this.editingBundle.name)
                : window._t('starting_bundle_banner', bundleName));

            this.isCreating = false;
            this.exitEditMode();
            this.isPickingInternal = false;
            // Leave isolated folders on disk until CreateBundle finishes copying; startup cleans leftovers.
            this.discardAllIsolatedFolders({ deleteOnDisk: false });
            this.selectedMods.clear();
            this.externalFiles.clear();
            this.selectedFolders.clear();
            this.ba2Selections.clear();
            this.ba2TotalPaths.clear();
            this.ba2AllPaths.clear();
            this.ba2Replacements.clear();
            this.ba2Removed.clear();
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
