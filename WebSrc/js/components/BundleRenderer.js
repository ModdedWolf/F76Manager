import { escapeHtml, escapeAttr } from '../utils/htmlSafe.js';
import { renderBa2TextView } from './Ba2TextEditor.js';

function previewNoneMessage(error) {
    if (error === 'unsupported') return window._t('preview_unsupported_format');
    if (error === 'hkx_behavior') return window._t('preview_hkx_behavior');
    if (error === 'hkx_character') return window._t('preview_hkx_character');
    if (error === 'hkx_project') return window._t('preview_hkx_project');
    if (error === 'hkx_not_clip') return window._t('preview_hkx_not_clip');
    if (error === 'hkx_no_skeleton') return window._t('preview_hkx_no_skeleton');
    return window._t('preview_unavailable');
}

function formatBa2Size(bytes) {
    const n = Number(bytes) || 0;
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

function formatTreeFileSize(manager, file, replacement, isAddition) {
    const overlaySize = replacement ? manager.getOverlaySourceSize(replacement) : undefined;
    if (overlaySize) return formatBa2Size(overlaySize);
    if (isAddition && !file.size) return '—';
    return formatBa2Size(file.size);
}

function normalizeBa2InternalPath(path) {
    return String(path || '').replace(/\\/g, '/').toLowerCase();
}

function collectNodeFilePaths(node, folderPath, out = []) {
    for (const file of node.files) {
        out.push(normalizeBa2InternalPath(file.fullPath));
    }
    for (const key of Object.keys(node.folders)) {
        const childPath = folderPath ? `${folderPath}/${key}` : key;
        collectNodeFilePaths(node.folders[key], childPath, out);
    }
    return out;
}

function annotateTreeFileCounts(node) {
    let total = node.files.reduce((n, file) => n + (file.added ? 0 : 1), 0);
    for (const key of Object.keys(node.folders)) {
        total += annotateTreeFileCounts(node.folders[key]);
    }
    node.totalFiles = total;
    return total;
}

function countSelectedInNode(node, selectedSet) {
    let count = 0;
    for (const file of node.files) {
        if (file.added) continue;
        if (selectedSet.has(normalizeBa2InternalPath(file.fullPath))) count++;
    }
    for (const key of Object.keys(node.folders)) {
        count += countSelectedInNode(node.folders[key], selectedSet);
    }
    return count;
}

function getFolderCheckStateFromCounts(total, selected) {
    if (!total) return { checked: false, indeterminate: false };
    if (selected === 0) return { checked: false, indeterminate: false };
    if (selected === total) return { checked: true, indeterminate: false };
    return { checked: false, indeterminate: true };
}

function getFolderCheckState(selectedSet, filePaths) {
    return getFolderCheckStateFromCounts(filePaths.length, countSelectedPaths(selectedSet, filePaths));
}

function countSelectedPaths(selectedSet, filePaths) {
    let selected = 0;
    for (const p of filePaths) {
        if (selectedSet.has(p)) selected++;
    }
    return selected;
}

function getFileEntryEmptyMessage(manager) {
    const stagedBa2s = manager.getStagedBa2Sources();
    if (
        stagedBa2s.length === 0
        && manager.selectedFolders.size > 0
        && manager.selectedMods.size === 0
        && manager.externalFiles.size === 0
    ) {
        return window._t('file_entry_folders_only');
    }
    return window._t('file_entry_expand_ba2');
}

function getOverlayBadgeHtml(manager, archivePath) {
    const key = manager.normalizeArchiveKey(archivePath);
    const { replaced, added } = manager.getOverlayCounts(key);
    let html = '';
    if (replaced > 0) {
        html += `<div class="staged-partial-badge staged-replaced-badge">${escapeHtml(window._t('bundle_replaced_count', replaced))}</div>`;
    }
    if (added > 0) {
        html += `<div class="staged-partial-badge staged-replaced-badge staged-added-badge">${escapeHtml(window._t('bundle_added_count', added))}</div>`;
    }
    return html;
}

function getStagedPartialBadge(manager, archivePath) {
    const key = manager.normalizeArchiveKey(archivePath);
    const overlayBadge = getOverlayBadgeHtml(manager, archivePath);
    const total = manager.ba2TotalPaths.get(key);
    if (!total) return overlayBadge;
    const selected = manager.getSelectedCount(key);
    if (selected >= total) return overlayBadge;
    return `<div class="staged-partial-badge">${escapeHtml(window._t('staged_partial_files', selected, total))}</div>${overlayBadge}`;
}

function buildTreeFromEntries(entries) {
    const root = { folders: {}, files: [] };
    for (const entry of entries) {
        const fullPath = String(entry.path || '');
        const parts = fullPath.split('/').filter(Boolean);
        if (parts.length === 0) continue;
        let node = root;
        for (let i = 0; i < parts.length - 1; i++) {
            const seg = parts[i];
            if (!node.folders[seg]) node.folders[seg] = { folders: {}, files: [] };
            node = node.folders[seg];
        }
        node.files.push({
            name: parts[parts.length - 1],
            size: entry.unpackedSize,
            fullPath,
            added: Boolean(entry.added)
        });
    }
    annotateTreeFileCounts(root);
    return root;
}

function pathMatchesFilter(fullPath, filter) {
    if (!filter) return true;
    return fullPath.toLowerCase().includes(filter.toLowerCase());
}

function renderTreeNode(node, folderPath, manager, depth = 0, readOnly = false) {
    const filter = manager.inspectorFilter || '';
    const expanded = manager.inspectorExpandedDirs || new Set();
    const archiveKey = readOnly ? '' : manager.getArchiveSelectionKey();
    const selectedSet = readOnly ? new Set() : manager.getSelectionForArchive(archiveKey);
    const folderKeys = Object.keys(node.folders).sort((a, b) => a.localeCompare(b));
    const files = [...node.files].sort((a, b) => a.name.localeCompare(b.name));
    const pad = 8 + depth * 14;

    let html = '';

    for (const key of folderKeys) {
        const child = node.folders[key];
        const childPath = folderPath ? `${folderPath}/${key}` : key;
        const isExpanded = expanded.has(childPath);
        let childHtml = '';
        if (isExpanded || filter) {
            childHtml = renderTreeNode(child, childPath, manager, depth + 1, readOnly);
            if (!childHtml && filter) continue;
        }

        const totalFiles = child.totalFiles ?? 0;
        const selectedCount = countSelectedInNode(child, selectedSet);
        const folderState = getFolderCheckStateFromCounts(totalFiles, selectedCount);
        const folderCheckHtml = readOnly ? '' : `
                    <input type="checkbox" class="ba2-tree-check ba2-tree-folder-check"
                        data-check-type="folder"
                        data-folder-path="${escapeAttr(childPath)}"
                        ${folderState.checked ? 'checked' : ''}
                        ${folderState.indeterminate ? 'data-indeterminate="true"' : ''}>`;
        html += `
            <div class="ba2-tree-folder${isExpanded ? ' ba2-tree-folder--expanded' : ''}" data-folder-path="${escapeAttr(childPath)}">
                <div class="ba2-tree-row ba2-tree-folder-row${readOnly ? ' ba2-tree-row--readonly' : ''}" style="padding-left: ${pad}px">
                    ${folderCheckHtml}
                    <button type="button" class="ba2-tree-folder-btn" data-folder-path="${escapeAttr(childPath)}">
                        <i data-lucide="chevron-right" class="ba2-tree-chevron"></i>
                        <i data-lucide="folder" class="ba2-tree-folder-icon"></i>
                        <span>${escapeHtml(key)}</span>
                    </button>
                </div>
                ${isExpanded ? `<div class="ba2-tree-children">${childHtml}</div>` : ''}
            </div>
        `;
    }

    const folderOverrides = !readOnly && manager.inspectedBa2Path && manager.selectedFolders.size > 0
        ? manager.getFolderOverrides()
        : null;

    for (const file of files) {
        if (!pathMatchesFilter(file.fullPath, filter)) continue;
        const normPath = normalizeBa2InternalPath(file.fullPath);
        const replacement = readOnly ? '' : manager.getReplacement(archiveKey, normPath);
        const isAddition = Boolean(file.added) || (!!replacement && manager.isOverlayAddition(archiveKey, normPath));
        const overridingFolder = !replacement && !isAddition && folderOverrides ? folderOverrides.get(normPath) || '' : '';
        const isChecked = selectedSet.has(normPath) || Boolean(replacement) || isAddition;
        const fileCheckHtml = readOnly ? '' : `
                <input type="checkbox" class="ba2-tree-check ba2-tree-file-check"
                    data-check-type="file"
                    data-file-path="${escapeAttr(normPath)}"
                    ${isChecked ? 'checked' : ''}
                    ${replacement || isAddition ? 'disabled' : ''}>`;
        const isPreviewing = !readOnly && manager.previewPath === normPath;
        let overlayTitle = '';
        let overlayBadgeLabel = '';
        if (isAddition && replacement) {
            overlayTitle = window._t('bundle_added_from', replacement);
            overlayBadgeLabel = window._t('bundle_added_badge');
        } else if (replacement) {
            overlayTitle = window._t('bundle_replaced_from', replacement);
            overlayBadgeLabel = window._t('bundle_replaced_badge');
        } else if (overridingFolder) {
            overlayTitle = window._t('bundle_overridden_by_folder', overridingFolder);
            overlayBadgeLabel = window._t('bundle_replaced_badge');
        }
        const overlayBadge = overlayTitle
            ? `<span class="ba2-tree-replaced-badge${isAddition ? ' ba2-tree-added-badge' : ''}" title="${escapeAttr(overlayTitle)}">${escapeHtml(overlayBadgeLabel)}</span>`
            : '';
        html += `
            <div class="ba2-tree-row ba2-tree-file${readOnly ? ' ba2-tree-row--readonly' : ''}${isPreviewing ? ' ba2-tree-file--previewing' : ''}${overlayTitle ? ' ba2-tree-file--replaced' : ''}${isAddition ? ' ba2-tree-file--added' : ''}" data-preview-path="${escapeAttr(normPath)}" style="padding-left: ${pad}px">
                ${fileCheckHtml}
                <i data-lucide="${fileIconName(file.name)}" class="ba2-tree-file-icon"></i>
                <span class="ba2-tree-file-name">${escapeHtml(file.name)}</span>
                ${overlayBadge}
                <span class="ba2-tree-file-size">${escapeHtml(formatTreeFileSize(manager, file, replacement, isAddition))}</span>
            </div>
        `;
    }

    return html;
}

function renderStagedRootsHtml(manager) {
    const activeKey = manager.inspectedBa2Path
        ? manager.normalizeArchiveKey(manager.inspectedBa2Path)
        : '';
    let html = '';

    for (const m of manager.selectedMods) {
        const inspectPath = manager.resolveArchivePath(m);
        const key = manager.normalizeArchiveKey(inspectPath);
        if (activeKey && key === activeKey && manager.inspectedBa2Path) continue;
        const activeClass = activeKey && key === activeKey ? ' file-entry-staged-item--active' : '';
        const partialBadge = getStagedPartialBadge(manager, inspectPath);
        html += `
            <div class="file-entry-staged-item file-entry-staged-ba2 bundle-staged-item bundle-staged-item--internal staged-item staged-item-internal${activeClass}" data-path="${escapeAttr(inspectPath)}">
                <i data-lucide="package" class="bundle-staged-icon"></i>
                <div class="bundle-staged-item-body">
                    <div class="bundle-staged-item-name">${escapeHtml(m.split('/').pop())}</div>
                    <div class="bundle-staged-item-meta">${window._t('internal_mod')}</div>
                    ${partialBadge}
                </div>
                <button class="btn-icon small btn-remove-staged" data-name="${escapeAttr(m)}" data-type="internal" title="${escapeAttr(window._t('remove_button'))}">
                    <i data-lucide="x"></i>
                </button>
            </div>
        `;
    }

    for (const f of manager.externalFiles) {
        const key = manager.normalizeArchiveKey(f);
        if (activeKey && key === activeKey && manager.inspectedBa2Path) continue;
        const activeClass = activeKey && key === activeKey ? ' file-entry-staged-item--active' : '';
        const partialBadge = getStagedPartialBadge(manager, f);
        html += `
            <div class="file-entry-staged-item file-entry-staged-ba2 bundle-staged-item bundle-staged-item--external staged-item staged-item-external${activeClass}" data-path="${escapeAttr(f)}">
                <i data-lucide="file-archive" class="bundle-staged-icon"></i>
                <div class="bundle-staged-item-body">
                    <div class="bundle-staged-item-name">${escapeHtml(f.split(/[\\/]/).pop())}</div>
                    <div class="bundle-staged-item-meta bundle-staged-item-meta--external">${escapeHtml(f)}</div>
                    ${partialBadge}
                </div>
                <button class="btn-icon small btn-remove-staged" data-name="${escapeAttr(f)}" data-type="external" title="${escapeAttr(window._t('remove_button'))}">
                    <i data-lucide="x"></i>
                </button>
            </div>
        `;
    }

    const activeFolderKey = manager.inspectedFolderPath
        ? manager.normalizeFolderKey(manager.inspectedFolderPath)
        : '';

    for (const f of manager.selectedFolders) {
        const folderKey = manager.normalizeFolderKey(f);
        if (activeFolderKey && folderKey === activeFolderKey && manager.inspectedFolderPath) continue;
        const activeClass = activeFolderKey && folderKey === activeFolderKey ? ' file-entry-staged-item--active' : '';
        const folderTotal = manager.ba2TotalPaths.get(folderKey);
        let folderPartialBadge = '';
        if (folderTotal) {
            const folderSelected = manager.getSelectedCount(folderKey);
            if (folderSelected < folderTotal) {
                folderPartialBadge = `<div class="staged-partial-badge">${escapeHtml(window._t('staged_partial_files', folderSelected, folderTotal))}</div>`;
            }
        }
        html += `
            <div class="file-entry-staged-item file-entry-staged-folder bundle-staged-item bundle-staged-item--folder staged-item${activeClass}" data-path="${escapeAttr(f)}">
                <i data-lucide="folder" class="bundle-staged-icon"></i>
                <div class="bundle-staged-item-body">
                    <div class="bundle-staged-item-name">${escapeHtml(f.split(/[\\/]/).pop() || f)}</div>
                    <div class="bundle-staged-item-meta">${window._t('staged_folder')}</div>
                    ${folderPartialBadge}
                </div>
                <button class="btn-icon small btn-remove-staged" data-name="${escapeAttr(f)}" data-type="folder" title="${escapeAttr(window._t('remove_button'))}">
                    <i data-lucide="x"></i>
                </button>
            </div>
        `;
    }

    return html;
}

function getInspectorCountLabel(manager) {
    const key = manager.getArchiveSelectionKey();
    if (!key) return '';
    const hasFilter = Boolean(manager.inspectorFilter);
    const selectedCount = hasFilter
        ? manager.getFilteredSelectedCount(key)
        : manager.getSelectedCount(key);
    const totalCount = hasFilter
        ? manager.getFilteredEntryCount()
        : (manager.ba2TotalPaths.get(key) || manager.inspectorFileCount || 0);
    if (!totalCount) return '';
    return hasFilter
        ? window._t('inspector_selected_count_filtered', selectedCount, totalCount)
        : window._t('inspector_selected_count', selectedCount, totalCount);
}

function renderExplorerTreeHtml(manager) {
    return buildBa2ExplorerTreeHtml(manager, { unified: true });
}

function renderInspectorPathFooter(manager) {
    const pathDisplay = manager.inspectedFolderPath
        || (manager.inspectedBa2Path
            ? (manager.inspectedResolvedPath || manager.resolveArchivePath(manager.inspectedBa2Path))
            : '');
    if (!pathDisplay) return '';
    return `<div class="bundle-path-bar" title="${escapeAttr(pathDisplay)}">${escapeHtml(pathDisplay)}</div>`;
}

function renderInspectorToolbar(manager) {
    if (manager.inspectedFolderPath) {
        const filterValue = escapeAttr(manager.inspectorFilter || '');
        const countLabel = getInspectorCountLabel(manager);

        return `
        <div class="bundle-inspector-pathbar">
            <div class="search-box bundle-inspector-search-wrap">
                <i data-lucide="search"></i>
                <input type="text" id="ba2-inspector-filter" class="bundle-field" placeholder="${escapeHtml(window._t('inspector_search_placeholder'))}" value="${filterValue}">
                <button type="button" class="search-clear" id="ba2-inspector-filter-clear" title="${escapeAttr(window._t('clear_search'))}" aria-label="${escapeAttr(window._t('clear_search'))}" ${manager.inspectorFilter ? '' : 'hidden'}>
                    <i data-lucide="x"></i>
                </button>
            </div>
            <div class="bundle-inspector-pathbar-meta">
                <span id="ba2-inspector-count">${escapeHtml(countLabel)}</span>
            </div>
            <div class="bundle-inspector-pathbar-actions">
                <button type="button" class="bundle-text-btn" id="btn-inspector-select-all">${window._t('inspector_select_all')}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-deselect-all">${window._t('inspector_deselect_all')}</button>
            </div>
        </div>
    `;
    }

    const stagedSources = manager.getStagedBa2Sources();
    const stagedOptions = stagedSources.map(src => `
        <option value="${escapeAttr(src.inspectPath)}" ${manager.normalizeArchiveKey(src.inspectPath) === manager.normalizeArchiveKey(manager.inspectedBa2Path) ? 'selected' : ''}>${escapeHtml(src.name)}</option>
    `).join('');
    const filterValue = escapeAttr(manager.inspectorFilter || '');
    const archiveKey = manager.getArchiveSelectionKey();
    const canExtract = archiveKey && manager.getEffectiveSelectionPaths(archiveKey).length > 0;
    const countLabel = getInspectorCountLabel(manager);
    const showStagedSwitcher = stagedSources.length >= 2;

    return `
        <div class="bundle-inspector-pathbar">
            ${showStagedSwitcher ? `
                <select id="bundle-staged-ba2-select" class="bundle-field bundle-staged-ba2-select" title="${escapeAttr(window._t('staged_items', stagedSources.length))}">
                    ${stagedOptions}
                </select>
            ` : ''}
            <div class="search-box bundle-inspector-search-wrap">
                <i data-lucide="search"></i>
                <input type="text" id="ba2-inspector-filter" class="bundle-field" placeholder="${escapeHtml(window._t('inspector_search_placeholder'))}" value="${filterValue}">
                <button type="button" class="search-clear" id="ba2-inspector-filter-clear" title="${escapeAttr(window._t('clear_search'))}" aria-label="${escapeAttr(window._t('clear_search'))}" ${manager.inspectorFilter ? '' : 'hidden'}>
                    <i data-lucide="x"></i>
                </button>
            </div>
            <div class="bundle-inspector-pathbar-meta">
                ${manager.inspectorArchiveType ? `<span>${escapeHtml(manager.inspectorArchiveType)}</span>` : ''}
                <span id="ba2-inspector-count">${escapeHtml(countLabel)}</span>
            </div>
            <div class="bundle-inspector-pathbar-actions">
                <button type="button" class="bundle-text-btn" id="btn-inspector-select-all">${window._t('inspector_select_all')}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-deselect-all">${window._t('inspector_deselect_all')}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-isolate" ${canExtract ? '' : 'disabled'}>${window._t('bundle_add_to_bundle')}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-extract" ${canExtract ? '' : 'disabled'}>${window._t('bundle_extract_to_folder')}</button>
            </div>
        </div>
    `;
}

function buildBa2ExplorerTreeHtml(manager, { unified = false } = {}) {
    const isFolderMode = Boolean(manager.inspectedFolderPath);
    const inspectedPath = isFolderMode ? manager.inspectedFolderPath : manager.inspectedBa2Path;

    if (manager.inspectorLoading) {
        return `<div class="ba2-explorer-empty">${escapeHtml(window._t('inspector_loading'))}</div>`;
    }
    if (!inspectedPath) {
        if (unified) return '';
        return `<div class="ba2-explorer-empty">${escapeHtml(getFileEntryEmptyMessage(manager))}</div>`;
    }
    const archiveKeyForOverlay = !isFolderMode ? manager.getArchiveSelectionKey() : '';
    const overlayAdditions = (!isFolderMode && archiveKeyForOverlay)
        ? manager.getOverlayAdditions(archiveKeyForOverlay)
        : [];
    if ((!manager.ba2Contents || manager.ba2Contents.length === 0) && overlayAdditions.length === 0) {
        const emptyMsg = isFolderMode ? window._t('inspector_folder_empty') : window._t('no_ba2_selected');
        return `<div class="ba2-explorer-empty">${escapeHtml(emptyMsg)}</div>`;
    }

    const filter = manager.inspectorFilter || '';
    let entries = Array.isArray(manager.ba2Contents) ? [...manager.ba2Contents] : [];
    if (overlayAdditions.length > 0) {
        const existing = new Set(entries.map(e => normalizeBa2InternalPath(e.path)));
        for (const { path } of overlayAdditions) {
            if (existing.has(path)) continue;
            entries.push({ path, unpackedSize: 0, added: true });
            existing.add(path);
        }
    }
    if (filter) {
        entries = entries.filter(e => pathMatchesFilter(String(e.path || ''), filter));
    }
    if (entries.length === 0) {
        return `<div class="ba2-explorer-empty">${escapeHtml(window._t('inspector_no_matches'))}</div>`;
    }

    const tree = buildTreeFromEntries(entries);
    const body = renderTreeNode(tree, '', manager, 1, false);
    if (!body) {
        return `<div class="ba2-explorer-empty">${escapeHtml(window._t('inspector_no_matches'))}</div>`;
    }

    const inspectedName = inspectedPath.split(/[\\/]/).pop();

    if (isFolderMode) {
        const folderKey = manager.getArchiveSelectionKey();
        const folderHasFilter = Boolean(manager.inspectorFilter);
        const folderTotal = folderHasFilter
            ? manager.getFilteredEntryCount()
            : (manager.ba2TotalPaths.get(folderKey) || entries.length);
        const folderSelected = folderHasFilter
            ? manager.getFilteredSelectedCount(folderKey)
            : manager.getSelectedCount(folderKey);
        const folderRootState = getFolderCheckStateFromCounts(folderTotal, folderSelected);
        return `
            <div class="ba2-file-entry ba2-file-entry--unified ba2-file-entry--folder">
                <div class="ba2-tree-row ba2-tree-archive-root" style="padding-left: 8px">
                    <input type="checkbox" class="ba2-tree-check ba2-tree-archive-root-check"
                        ${folderRootState.checked ? 'checked' : ''}
                        ${folderRootState.indeterminate ? 'data-indeterminate="true"' : ''}>
                    <i data-lucide="folder" class="ba2-tree-archive-icon"></i>
                    <span class="ba2-tree-archive-name">${escapeHtml(inspectedName)}</span>
                </div>
                <div class="ba2-tree-children">${body}</div>
            </div>
        `;
    }

    const archiveKey = manager.getArchiveSelectionKey();
    const hasFilter = Boolean(manager.inspectorFilter);
    const totalCount = hasFilter
        ? manager.getFilteredEntryCount()
        : (manager.ba2TotalPaths.get(archiveKey) || entries.length);
    const selectedCount = hasFilter
        ? manager.getFilteredSelectedCount(archiveKey)
        : manager.getSelectedCount(archiveKey);
    const rootState = getFolderCheckStateFromCounts(totalCount, selectedCount);

    if (unified) {
        return `
            <div class="ba2-file-entry ba2-file-entry--unified">
                <div class="ba2-tree-row ba2-tree-archive-root" style="padding-left: 8px">
                    <input type="checkbox" class="ba2-tree-check ba2-tree-archive-root-check"
                        ${rootState.checked ? 'checked' : ''}
                        ${rootState.indeterminate ? 'data-indeterminate="true"' : ''}>
                    <i data-lucide="file-archive" class="ba2-tree-archive-icon"></i>
                    <span class="ba2-tree-archive-name">${escapeHtml(inspectedName)}</span>
                </div>
                <div class="ba2-tree-children">${body}</div>
            </div>
        `;
    }

    return `
        <div class="ba2-file-entry">
            <div class="ba2-tree-row ba2-tree-archive-root" style="padding-left: 8px">
                <input type="checkbox" class="ba2-tree-check ba2-tree-archive-root-check"
                    ${rootState.checked ? 'checked' : ''}
                    ${rootState.indeterminate ? 'data-indeterminate="true"' : ''}>
                <i data-lucide="file-archive" class="ba2-tree-archive-icon"></i>
                <span class="ba2-tree-archive-name">${escapeHtml(inspectedName)}</span>
            </div>
            <div class="ba2-tree-children">${body}</div>
        </div>
    `;
}

function renderInspectorContent(manager) {
    const totalStaged = manager.selectedMods.size + manager.externalFiles.size + manager.selectedFolders.size;
    const stagedBa2s = manager.getStagedBa2Sources();

    if (totalStaged === 0) {
        return `
            <div class="file-entry-unified file-entry-unified--empty">
                <div class="file-entry-empty">
                    <i data-lucide="package-plus"></i>
                    <div class="file-entry-empty-title">${window._t('workspace_empty')}</div>
                    <div class="file-entry-empty-hint">${window._t('file_entry_drop_hint')}</div>
                </div>
            </div>
        `;
    }

    const isInspecting = Boolean(manager.inspectedBa2Path || manager.inspectedFolderPath);

    let inspectBlock = '';
    if (isInspecting) {
        inspectBlock = `
            <div class="ba2-inspector-split">
                <div class="ba2-explorer-tree">${buildBa2ExplorerTreeHtml(manager, { unified: true })}</div>
                ${renderPreviewPaneHtml(manager)}
            </div>
            <div id="ba2-audio-dock-slot">${renderAudioDockHtml(manager)}</div>
        `;
    } else if (stagedBa2s.length > 0) {
        inspectBlock = `
            <div class="file-entry-hint">${escapeHtml(window._t('file_entry_expand_ba2'))}</div>
        `;
    } else if (manager.selectedFolders.size > 0) {
        inspectBlock = `
            <div class="file-entry-hint">${escapeHtml(window._t('file_entry_expand_folder'))}</div>
        `;
    } else {
        inspectBlock = `
            <div class="file-entry-hint">${escapeHtml(window._t('file_entry_folders_only'))}</div>
        `;
    }

    const stagedRootsHtml = renderStagedRootsHtml(manager);

    return `
        <div class="file-entry-unified">
            ${stagedRootsHtml ? `<div class="file-entry-staged-roots">${stagedRootsHtml}</div>` : ''}
            <div class="file-entry-inspect">${inspectBlock}</div>
        </div>
    `;
}

function previewFileName(path) {
    const parts = String(path || '').split('/');
    return parts[parts.length - 1] || path;
}

const AUDIO_EXTENSIONS = new Set(['mp3', 'wav', 'ogg', 'flac', 'xwm', 'fuz']);

function fileIconName(fileName) {
    const ext = String(fileName || '').split('.').pop().toLowerCase();
    return AUDIO_EXTENSIONS.has(ext) ? 'file-audio' : 'file';
}

function formatAudioDuration(seconds) {
    const total = Math.max(0, Math.floor(Number(seconds) || 0));
    const m = Math.floor(total / 60);
    const s = total % 60;
    return `${m}:${String(s).padStart(2, '0')}`;
}

function formatSampleRate(hz) {
    if (!hz) return '';
    return hz >= 1000 ? `${(hz / 1000).toFixed(hz % 1000 === 0 ? 0 : 1)} kHz` : `${hz} Hz`;
}

function renderPreviewPaneHtml(manager) {
    const canExtract = Boolean(manager.inspectedBa2Path && manager.previewPath);
    const isAudioResult = manager.previewResult?.kind === 'audio';
    const extractBtn = canExtract && !isAudioResult
        ? `<button type="button" class="bundle-text-btn ba2-preview-extract" id="btn-preview-extract">${escapeHtml(window._t('preview_extract_file'))}</button>`
        : '';

    let body;
    if (!manager.previewPath) {
        body = `<div class="ba2-preview-empty">${escapeHtml(window._t('preview_hint'))}</div>`;
    } else if (manager.previewLoading) {
        body = `
            <div class="ba2-preview-header">
                <div class="ba2-preview-name">${escapeHtml(previewFileName(manager.previewPath))}</div>
            </div>
            <div class="ba2-preview-empty">${escapeHtml(window._t('preview_loading'))}</div>
        `;
    } else if (manager.previewResult?.kind === 'image' && manager.previewResult.dataUrl) {
        const result = manager.previewResult;
        const meta = [];
        if (result.width && result.height) meta.push(`${result.width}×${result.height}`);
        if (result.format) meta.push(result.format);
        if (result.mips) meta.push(window._t('preview_mips', result.mips));
        if (result.size) meta.push(formatBa2Size(result.size));
        const small = result.width && result.height && Math.max(result.width, result.height) <= 256;
        body = `
            <div class="ba2-preview-header">
                <div class="ba2-preview-name">${escapeHtml(previewFileName(manager.previewPath))}</div>
                <div class="ba2-preview-meta">${escapeHtml(meta.join(' · '))}</div>
            </div>
            <div class="ba2-preview-stage${small ? ' ba2-preview-stage--small' : ''}"><img alt="" src="${escapeAttr(result.dataUrl)}"></div>
        `;
    } else if (manager.previewResult?.kind === 'audio') {
        body = renderAudioPreview(manager, manager.previewResult);
    } else if (manager.previewResult?.kind === 'material') {
        body = renderMaterialPreview(manager, manager.previewResult);
    } else if (manager.previewResult?.kind === 'hex') {
        body = renderHexPreview(manager, manager.previewResult);
    } else if (manager.previewResult?.kind === 'mesh') {
        body = renderMeshPreview(manager, manager.previewResult);
    } else if (manager.previewResult?.kind === 'text') {
        const result = manager.previewResult;
        body = renderBa2TextView({
            idPrefix: 'ba2-text-font',
            name: previewFileName(manager.previewPath),
            meta: formatBa2Size(result.size),
            text: result.text,
            truncated: result.truncated,
            showPopout: true
        });
    } else {
        const result = manager.previewResult;
        const meta = [];
        if (result?.format) meta.push(result.format);
        if (result?.width && result?.height) meta.push(`${result.width}×${result.height}`);
        if (result?.size) meta.push(formatBa2Size(result.size));
        const message = previewNoneMessage(result?.error);
        body = `
            <div class="ba2-preview-header">
                <div class="ba2-preview-name">${escapeHtml(previewFileName(manager.previewPath))}</div>
                ${meta.length ? `<div class="ba2-preview-meta">${escapeHtml(meta.join(' · '))}</div>` : ''}
            </div>
            <div class="ba2-preview-empty">${escapeHtml(message)}</div>
        `;
    }

    const isAudioPreview = isAudioResult || (manager.previewLoading && isAudioPath(manager.previewPath));
    const hidden = !manager.previewPath || (isAudioPreview && !manager._audioInfoOpen)
        ? ' ba2-preview-pane--hidden'
        : '';
    const popoutKind = !manager.previewLoading && ['mesh', 'image'].includes(manager.previewResult?.kind);
    const popoutBtn = popoutKind
        ? `<button type="button" class="bundle-text-btn ba2-preview-popout" data-preview-popout>${escapeHtml(window._t('bundle_preview_popout'))}</button>`
        : '';
    return `
        <aside class="ba2-preview-pane${hidden}">
            ${body}
            ${popoutBtn || extractBtn ? `<div class="ba2-preview-footer">${popoutBtn}${extractBtn}</div>` : ''}
        </aside>
    `;
}

function renderAudioPreview(manager, result) {
    const audio = result.audio || {};
    const meta = [];
    if (audio.codec) meta.push(audio.codec);
    if (audio.sampleRate) meta.push(formatSampleRate(audio.sampleRate));
    if (audio.channels === 1) meta.push(window._t('preview_audio_mono'));
    else if (audio.channels === 2) meta.push(window._t('preview_audio_stereo'));
    else if (audio.channels) meta.push(window._t('preview_audio_channels', audio.channels));
    if (audio.duration) meta.push(formatAudioDuration(audio.duration));
    if (result.size) meta.push(formatBa2Size(result.size));

    const rows = [];
    if (audio.codec) rows.push([window._t('preview_audio_codec'), audio.codec]);
    if (audio.sampleRate) rows.push([window._t('preview_audio_sample_rate'), formatSampleRate(audio.sampleRate)]);
    if (audio.channels) rows.push([window._t('preview_audio_channels_label'),
        audio.channels === 1 ? window._t('preview_audio_mono')
            : audio.channels === 2 ? window._t('preview_audio_stereo')
            : String(audio.channels)]);
    if (audio.duration) rows.push([window._t('preview_audio_duration'), formatAudioDuration(audio.duration)]);
    if (result.size) rows.push([window._t('preview_audio_size'), formatBa2Size(result.size)]);
    const rowsHtml = rows.map(([label, value]) => `
            <div class="ba2-audio-row">
                <span class="ba2-audio-label">${escapeHtml(label)}</span>
                <span class="ba2-audio-value">${escapeHtml(String(value))}</span>
            </div>`).join('');
    const lip = audio.lipBytes
        ? `<div class="ba2-preview-note">${escapeHtml(window._t('preview_audio_lip', formatBa2Size(audio.lipBytes)))}</div>`
        : '';
    return `
        ${previewHeader(manager, meta.join(' · '))}
        ${rowsHtml ? `<div class="ba2-audio-info">${rowsHtml}</div>` : ''}
        ${lip}
        <div class="ba2-preview-audio-hint">${escapeHtml(window._t('preview_audio_space_hint'))}</div>
    `;
}

function isAudioPath(path) {
    const ext = String(path || '').split('.').pop().toLowerCase();
    return AUDIO_EXTENSIONS.has(ext);
}

function audioDockSubtitle(manager, audio) {
    const parts = [];
    const segments = String(manager.previewPath || '').split('/').filter(Boolean);
    if (segments.length > 1) parts.push(segments[segments.length - 2]);
    if (audio.codec) parts.push(audio.codec);
    if (audio.sampleRate) parts.push(formatSampleRate(audio.sampleRate));
    if (audio.channels === 1) parts.push(window._t('preview_audio_mono'));
    else if (audio.channels === 2) parts.push(window._t('preview_audio_stereo'));
    else if (audio.channels) parts.push(window._t('preview_audio_channels', audio.channels));
    if (audio.silent) parts.push(window._t('preview_audio_silent_label'));
    return parts.join(' • ');
}

function renderAudioDockHtml(manager) {
    const isAudio = manager.previewResult?.kind === 'audio';
    const isLoading = !isAudio && manager.previewLoading && isAudioPath(manager.previewPath);
    if (!isAudio && !isLoading) return '';

    const result = isAudio ? manager.previewResult : {};
    const audio = result.audio || {};
    const audioUrl = result.audioUrl ? `https://f76manager.app/${result.audioUrl}` : '';
    const totalLabel = audio.duration ? formatAudioDuration(audio.duration) : '0:00';
    const volumePercent = Math.round((manager._audioVolume ?? 1) * 100);
    const playLabel = window._t('preview_audio_play');
    const subtitle = isLoading ? window._t('preview_loading') : audioDockSubtitle(manager, audio);
    const disabled = isLoading ? 'disabled' : '';
    return `
        <div class="ba2-audio-dock${isLoading ? ' ba2-audio-dock--loading' : ''}">
            ${isAudio ? `<audio preload="metadata" class="ba2-preview-audio-hidden" id="ba2-preview-audio"
                data-audio-url="${escapeAttr(audioUrl)}"></audio>` : ''}
            <input type="range" class="ba2-audio-slider ba2-audio-seek" id="ba2-audio-seek"
                min="0" max="10000" value="0" step="1" style="--value: 0%" ${disabled}
                title="${escapeAttr(window._t('preview_audio_seek'))}" aria-label="${escapeAttr(window._t('preview_audio_seek'))}">
            <div class="ba2-audio-player" id="ba2-audio-player">
                <div class="ba2-audio-dock-info">
                    <div class="ba2-audio-dock-art"><i data-lucide="audio-lines"></i></div>
                    <div class="ba2-audio-dock-text">
                        <div class="ba2-audio-dock-name" title="${escapeAttr(manager.previewPath || '')}">${escapeHtml(previewFileName(manager.previewPath))}</div>
                        <div class="ba2-audio-dock-sub">${escapeHtml(subtitle)}</div>
                    </div>
                </div>
                <div class="ba2-audio-dock-controls">
                    <button type="button" class="ba2-audio-icon-btn" id="ba2-audio-prev"
                        title="${escapeAttr(window._t('preview_audio_prev'))}" aria-label="${escapeAttr(window._t('preview_audio_prev'))}">
                        <i data-lucide="skip-back"></i>
                    </button>
                    <button type="button" class="ba2-audio-play" id="ba2-audio-play" ${disabled}
                        title="${escapeAttr(window._t('preview_shortcut_space', playLabel))}" aria-label="${escapeAttr(playLabel)}">
                        <span id="ba2-audio-icon-play"><i data-lucide="play"></i></span>
                        <span id="ba2-audio-icon-pause" hidden><i data-lucide="pause"></i></span>
                    </button>
                    <button type="button" class="ba2-audio-icon-btn" id="ba2-audio-next"
                        title="${escapeAttr(window._t('preview_audio_next'))}" aria-label="${escapeAttr(window._t('preview_audio_next'))}">
                        <i data-lucide="skip-forward"></i>
                    </button>
                </div>
                <div class="ba2-audio-dock-right">
                    ${manager.inspectedBa2Path ? `<button type="button" class="ba2-audio-extract" id="ba2-audio-extract" ${disabled}
                        title="${escapeAttr(window._t('preview_extract_file'))}" aria-label="${escapeAttr(window._t('preview_extract_file'))}">
                        <i data-lucide="download"></i><span class="ba2-audio-extract-label">${escapeHtml(window._t('preview_extract_file'))}</span>
                    </button>` : ''}
                    <span class="ba2-audio-time" id="ba2-audio-time">0:00 / ${escapeHtml(totalLabel)}</span>
                    <button type="button" class="ba2-audio-icon-btn" id="ba2-audio-loop" ${disabled}
                        title="${escapeAttr(window._t('preview_audio_loop'))}" aria-label="${escapeAttr(window._t('preview_audio_loop'))}" aria-pressed="false">
                        <i data-lucide="repeat"></i>
                    </button>
                    <button type="button" class="ba2-audio-icon-btn${manager._audioInfoOpen ? ' ba2-audio-icon-btn--on' : ''}" id="ba2-audio-info"
                        title="${escapeAttr(window._t('preview_audio_info'))}" aria-label="${escapeAttr(window._t('preview_audio_info'))}" aria-pressed="${manager._audioInfoOpen ? 'true' : 'false'}">
                        <i data-lucide="info"></i>
                    </button>
                    <div class="ba2-audio-volume-group">
                        <button type="button" class="ba2-audio-icon-btn" id="ba2-audio-mute"
                            title="${escapeAttr(window._t('preview_audio_mute'))}" aria-label="${escapeAttr(window._t('preview_audio_mute'))}">
                            <span id="ba2-audio-icon-volume"><i data-lucide="volume-2"></i></span>
                            <span id="ba2-audio-icon-muted" hidden><i data-lucide="volume-x"></i></span>
                        </button>
                        <input type="range" class="ba2-audio-slider ba2-audio-volume" id="ba2-audio-volume"
                            min="0" max="100" value="${volumePercent}" step="1" style="--value: ${volumePercent}%"
                            title="${escapeAttr(window._t('preview_audio_volume'))}" aria-label="${escapeAttr(window._t('preview_audio_volume'))}">
                    </div>
                </div>
            </div>
        </div>
    `;
}

function previewHeader(manager, meta) {
    return `
        <div class="ba2-preview-header">
            <div class="ba2-preview-name">${escapeHtml(previewFileName(manager.previewPath))}</div>
            ${meta ? `<div class="ba2-preview-meta">${escapeHtml(meta)}</div>` : ''}
        </div>
    `;
}

function roleLabel(role) {
    const key = `preview_role_${role || 'texture'}`;
    const text = window._t(key);
    return text === key ? window._t('preview_role_texture') : text;
}

function renderMaterialPreview(manager, result) {
    const material = result.material || {};
    const textures = Array.isArray(material.textures) ? material.textures : [];
    const zoom = manager.previewMaterialZoom;
    if (zoom != null && textures[zoom]?.dataUrl) {
        const tex = textures[zoom];
        return `
            ${previewHeader(manager, roleLabel(tex.role))}
            <button type="button" class="bundle-text-btn" id="btn-preview-material-back">${escapeHtml(window._t('preview_material_back'))}</button>
            <div class="ba2-preview-stage"><img alt="" src="${escapeAttr(tex.dataUrl)}"></div>
            <div class="ba2-preview-note">${escapeHtml(tex.path || '')}</div>
        `;
    }

    const meta = [material.signature, material.version ? `v${material.version}` : ''].filter(Boolean).join(' ');
    const rows = textures.map((tex, index) => {
        const thumb = tex.dataUrl
            ? `<button type="button" class="ba2-material-thumb" data-material-texture="${index}"><img alt="" src="${escapeAttr(tex.dataUrl)}"></button>`
            : `<div class="ba2-material-thumb ba2-material-thumb--missing">${escapeHtml(window._t('preview_texture_missing'))}</div>`;
        const where = tex.found ? (tex.archive || '') : window._t('preview_texture_missing');
        return `
            <div class="ba2-material-row">
                ${thumb}
                <div class="ba2-material-copy">
                    <div class="ba2-material-role">${escapeHtml(roleLabel(tex.role))}</div>
                    <div class="ba2-material-path" title="${escapeAttr(tex.path || '')}">${escapeHtml(tex.path || '')}</div>
                    <div class="ba2-material-where">${escapeHtml(where)}</div>
                </div>
            </div>
        `;
    }).join('');
    const template = material.template
        ? `<div class="ba2-preview-note">${escapeHtml(material.template)}</div>`
        : '';
    return `
        ${previewHeader(manager, meta)}
        ${template}
        <div class="ba2-material-list">${rows || `<div class="ba2-preview-empty">${escapeHtml(window._t('preview_unavailable'))}</div>`}</div>
    `;
}

function renderHexPreview(manager, result) {
    const shown = Math.min(result.size || 0, 4096);
    const note = result.error === 'mesh_too_large'
        ? window._t('preview_mesh_too_large')
        : result.error === 'audio_decode_failed'
            ? window._t('preview_audio_decode_failed')
            : result.error === 'audio_codec_unsupported'
                ? window._t('preview_audio_codec_unsupported')
                : result.error === 'hkx_unsupported'
                    ? window._t('preview_hkx_unsupported')
                    : window._t('preview_hex_note', formatBa2Size(shown), formatBa2Size(result.size));
    return `
        ${previewHeader(manager, formatBa2Size(result.size))}
        <div class="ba2-preview-note">${escapeHtml(note)}</div>
        <pre class="ba2-preview-text ba2-preview-hex">${escapeHtml(formatHexDump(result.hex))}</pre>
    `;
}

function formatHexDump(base64) {
    let bin = '';
    try { bin = atob(base64 || ''); } catch (_) { return ''; }
    const lines = [];
    for (let i = 0; i < bin.length; i += 16) {
        const hex = [];
        let ascii = '';
        for (let j = 0; j < 16; j++) {
            if (i + j < bin.length) {
                const code = bin.charCodeAt(i + j);
                hex.push(code.toString(16).padStart(2, '0'));
                ascii += code >= 32 && code < 127 ? bin[i + j] : '.';
            } else {
                hex.push('  ');
            }
        }
        lines.push(`${i.toString(16).padStart(8, '0')}  ${hex.join(' ')}  ${ascii}`);
    }
    return lines.join('\n');
}

function textureReason(shape) {
    const status = shape.textureStatus || 'ok';
    if (status === 'ok') return '';
    const path = shape.texturePath || shape.material || '';
    if (status === 'material_not_found') return window._t('preview_tex_material_not_found', path);
    if (status === 'no_diffuse') return window._t('preview_tex_no_diffuse', path);
    if (status === 'texture_not_found') return window._t('preview_tex_not_found', path);
    if (status === 'decode_failed') return window._t('preview_tex_decode_failed', path, shape.textureFormat || '');
    return window._t('preview_tex_no_material');
}

function renderMeshPreview(manager, result) {
    const mesh = result.mesh || {};
    const allShapes = Array.isArray(mesh.shapes) ? mesh.shapes : [];
    const showModel = Boolean(manager._meshModelVisible) && allShapes.length > 0;
    const shapes = showModel ? allShapes : [];
    const boneCount = mesh.bones && Array.isArray(mesh.bones.names) ? mesh.bones.names.length : 0;
    const anim = mesh.animation || null;
    const hasAnim = anim && Number(anim.frameCount) > 1 && Number(anim.duration) > 0;
    const isHkx = Boolean(anim) && boneCount > 0;
    const skeleton = boneCount > 0 && shapes.length === 0;
    const materials = [...new Set(shapes.map(shape => shape.material).filter(Boolean))];
    const meta = skeleton
        ? (hasAnim
            ? window._t('preview_animation', boneCount, formatAudioDuration(anim.duration))
            : window._t('preview_skeleton', boneCount))
        : window._t('preview_mesh_stats', shapes.length, mesh.vertexCount || 0, mesh.triangleCount || 0);
    const skipped = mesh.skipped
        ? `<div class="ba2-preview-note">${escapeHtml(window._t('preview_shapes_skipped', mesh.skipped))}</div>`
        : '';
    const trimmed = hasAnim && anim.trimmed
        ? `<div class="ba2-preview-note">${escapeHtml(window._t('preview_hkx_trimmed'))}</div>`
        : '';
    const modelStatus = mesh.modelStatus || '';
    const modelNote = modelStatus === 'not_found'
        ? `<div class="ba2-preview-note">${escapeHtml(window._t('preview_hkx_model_not_found'))}</div>`
        : modelStatus === 'no_match'
            ? `<div class="ba2-preview-note">${escapeHtml(window._t('preview_hkx_model_no_match'))}</div>`
            : (showModel && mesh.modelPath
                ? `<div class="ba2-preview-note" title="${escapeAttr(mesh.modelPath)}">${escapeHtml(mesh.modelPath.split('/').pop())}</div>`
                : '');
    const materialNote = materials.length
        ? `<div class="ba2-preview-note" title="${escapeAttr(materials.join('\n'))}">${escapeHtml(materials[0])}${materials.length > 1 ? ` +${materials.length - 1}` : ''}</div>`
        : '';
    const reasons = shapes
        .map(shape => textureReason(shape))
        .filter(Boolean);
    const reasonHtml = reasons.length
        ? `<ul class="ba2-preview-reasons">${reasons.map(reason => `<li>${escapeHtml(reason)}</li>`).join('')}</ul>`
        : '';
    const wire = skeleton
        ? ''
        : `<button type="button" class="bundle-text-btn" id="btn-mesh-wire">${escapeHtml(window._t('preview_wireframe'))}</button>`;
    const modelLoading = Boolean(manager._meshModelLoading);
    const modelBtn = isHkx
        ? `<button type="button" class="bundle-text-btn${showModel ? ' ba2-preview-mesh-tool--on' : ''}" id="btn-mesh-model" ${modelLoading ? 'disabled' : ''}>${escapeHtml(
            modelLoading
                ? window._t('preview_hkx_model_loading')
                : (showModel ? window._t('preview_hkx_hide_model') : window._t('preview_hkx_show_model'))
        )}</button>`
        : '';
    const transport = hasAnim ? renderMeshAnimTransport(anim) : '';
    return `
        ${previewHeader(manager, meta)}
        ${materialNote}
        ${modelNote}
        ${reasonHtml}
        ${skipped}
        ${trimmed}
        <div class="ba2-preview-stage ba2-preview-stage--mesh" id="ba2-mesh-stage"></div>
        ${transport}
        <div class="ba2-preview-mesh-tools">
            ${modelBtn}
            ${wire}
            <button type="button" class="bundle-text-btn" id="btn-mesh-reset">${escapeHtml(window._t('preview_reset_view'))}</button>
            <span class="ba2-preview-mesh-hint">${escapeHtml(window._t('preview_mesh_controls'))}</span>
        </div>
    `;
}

function renderMeshAnimTransport(anim) {
    const duration = Number(anim.duration) || 0;
    const totalLabel = formatAudioDuration(duration);
    const playLabel = window._t('preview_audio_play');
    return `
        <div class="ba2-mesh-anim" id="ba2-mesh-anim">
            <button type="button" class="ba2-audio-play" id="ba2-mesh-anim-play"
                title="${escapeAttr(window._t('preview_shortcut_space', playLabel))}" aria-label="${escapeAttr(playLabel)}">
                <span id="ba2-mesh-anim-icon-play" hidden><i data-lucide="play"></i></span>
                <span id="ba2-mesh-anim-icon-pause"><i data-lucide="pause"></i></span>
            </button>
            <input type="range" class="ba2-audio-slider ba2-mesh-anim-seek" id="ba2-mesh-anim-seek"
                min="0" max="1000" value="0" step="1" style="--value: 0%"
                aria-label="${escapeAttr(window._t('preview_audio_play'))}">
            <span class="ba2-audio-time" id="ba2-mesh-anim-time">0:00 / ${escapeHtml(totalLabel)}</span>
            <button type="button" class="ba2-audio-icon-btn ba2-audio-icon-btn--on" id="ba2-mesh-anim-loop"
                title="${escapeAttr(window._t('preview_audio_loop'))}" aria-label="${escapeAttr(window._t('preview_audio_loop'))}" aria-pressed="true">
                <i data-lucide="repeat"></i>
            </button>
        </div>
    `;
}

function getFilteredInspectorEntries(manager) {
    const filter = manager.inspectorFilter || '';
    let entries = manager.ba2Contents || [];
    if (filter) {
        entries = entries.filter(e => pathMatchesFilter(String(e.path || ''), filter));
    }
    return entries;
}

function findTreeNodeAtPath(root, folderPath) {
    if (!folderPath) return root;
    const parts = folderPath.split('/').filter(Boolean);
    let node = root;
    for (const part of parts) {
        if (!node.folders[part]) return null;
        node = node.folders[part];
    }
    return node;
}

function collectFolderPaths(manager, { foldersWithSubfoldersOnly = false } = {}) {
    const entries = getFilteredInspectorEntries(manager);
    if (!entries.length) return [];
    const tree = buildTreeFromEntries(entries);
    const out = [];
    const walk = (node, folderPath) => {
        for (const key of Object.keys(node.folders).sort((a, b) => a.localeCompare(b))) {
            const child = node.folders[key];
            const childPath = folderPath ? `${folderPath}/${key}` : key;
            const hasSubfolders = Object.keys(child.folders).length > 0;
            if (!foldersWithSubfoldersOnly || hasSubfolders) out.push(childPath);
            walk(child, childPath);
        }
    };
    walk(tree, '');
    return out;
}

function renderFolderChildrenHtml(manager, folderPath) {
    const entries = getFilteredInspectorEntries(manager);
    if (!entries.length) return '';
    const tree = buildTreeFromEntries(entries);
    const node = findTreeNodeAtPath(tree, folderPath);
    if (!node) return '';
    const depth = folderPath.split('/').filter(Boolean).length + 1;
    return renderTreeNode(node, folderPath, manager, depth, false);
}

export const BundleRenderer = {
    renderInspectorContent,
    renderInspectorToolbar,
    renderInspectorPathFooter,
    renderMeshPreview,
    formatBa2Size,
    previewNoneMessage,
    renderExplorerTreeHtml,
    getInspectorCountLabel,
    renderFolderChildrenHtml,
    collectFolderPaths,
    renderPreviewPaneHtml,
    renderAudioDockHtml,

    render(manager, data) {
        if (manager.isCreating) {
            return this.renderWizard(manager, data);
        }

        const bundles = (data?.mods ?? []).filter(m => m.isBundle);

        if (bundles.length === 0) {
            return `
                <div class="bundle-page animate-fade" style="padding: 24px; display: flex; flex-direction: column; gap: 24px; height: 100%;">
                    <div class="mods-toolbar" style="background: var(--bg-surface); border: 1px solid var(--border-color); padding: 16px 24px; border-radius: 12px; display: flex; justify-content: space-between; align-items: center;">
                        <div class="toolbar-left" style="display: flex; align-items: center; gap: 16px;">
                            <div class="header-with-icon" style="display: flex; align-items: center; gap: 12px;">
                                <i data-lucide="archive" style="width: 24px; height: 24px; color: var(--primary-green);"></i>
                                <h2 style="margin: 0; font-size: 1.25rem; font-weight: 700;">${window._t('bundle_creations')}</h2>
                            </div>
                        </div>
                        <div class="tool-buttons">
                            <button class="btn-secondary" id="btn-show-wizard">
                                <i data-lucide="plus"></i> ${window._t('create_new_bundle')}
                            </button>
                        </div>
                    </div>

                    <div class="empty-state-container polished" style="flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; background: rgba(255, 255, 255, 0.02); border: 1px dashed rgba(255, 255, 255, 0.1); border-radius: 16px; text-align: center; padding: 48px;">
                        <div class="empty-state-icon" style="width: 80px; height: 80px; background: rgba(var(--primary-rgb), 0.12); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin-bottom: 24px;">
                            <i data-lucide="layers" style="width: 40px; height: 40px; color: var(--primary-green);"></i>
                        </div>
                        <h3>${window._t('no_bundles_found')}</h3>
                        <p style="color: var(--text-muted); max-width: 400px; line-height: 1.6; margin-bottom: 32px;">${window._t('no_bundles_desc')}</p>
                        <button class="btn primary" id="btn-create-bundle" style="padding: 12px 24px; font-size: 1rem;">
                            <i data-lucide="plus"></i> ${window._t('create_first_bundle')}
                        </button>
                    </div>
                </div>`;
        }

        const getStatusText = (status) => status === 'enabled' ? window._t('in_mod_list') : window._t('storage_only');

        return `
            <div class="bundle-page animate-fade" style="padding: 24px; display: flex; flex-direction: column; gap: 24px; height: 100%;" onclick="document.querySelectorAll('.bundle-menu-dropdown').forEach(e => e.remove())">
                <style>
                    .staged-item, .picker-item {
                        transition: transform 0.2s, background 0.2s;
                    }
                    .staged-item:hover, .picker-item:hover {
                        background: rgba(255,255,255,0.06) !important;
                        transform: translateX(4px);
                    }
                    .picker-item.selected {
                        border: 1px solid var(--primary-green) !important;
                        background: rgba(var(--primary-rgb), 0.12) !important;
                    }
                    #staged-drop-zone.drag-active {
                        background: rgba(var(--primary-rgb), 0.12) !important;
                        border: 2px dashed var(--primary-green);
                    }
                    .workspace-view .widget {
                        border: 1px solid rgba(255,255,255,0.05);
                        box-shadow: 0 8px 32px rgba(0,0,0,0.3);
                        backdrop-filter: blur(10px);
                    }
                </style>
                <div class="mods-toolbar" style="background: var(--bg-surface); border: 1px solid var(--border-color); padding: 16px 24px; border-radius: 12px; display: flex; justify-content: space-between; align-items: center;">
                    <div class="toolbar-left" style="display: flex; align-items: center; gap: 16px;">
                        <div class="header-with-icon" style="display: flex; align-items: center; gap: 12px;">
                            <i data-lucide="archive" style="width: 24px; height: 24px; color: var(--primary-green);"></i>
                            <h2 style="margin: 0; font-size: 1.25rem; font-weight: 700;">${window._t('bundle_creations')}</h2>
                            <span class="badge" style="background: rgba(255,255,255,0.1);">${bundles.length}</span>
                        </div>
                    </div>
                    <div class="tool-buttons">
                        <button class="btn-secondary" id="btn-show-wizard">
                            <i data-lucide="plus"></i> ${window._t('create_new_bundle')}
                        </button>
                    </div>
                </div>

                <div class="bundles-grid" style="display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 16px;">
                    ${bundles.map(b => `
                        <div class="bundle-card widget" style="padding: 20px; display: flex; flex-direction: column; gap: 16px; position: relative;">
                            <div style="display: flex; justify-content: space-between; align-items: flex-start;">
                                <div style="display: flex; gap: 12px; align-items: center;">
                                    <div style="width: 48px; height: 48px; background: rgba(var(--primary-rgb), 0.12); border-radius: 8px; display: flex; align-items: center; justify-content: center;">
                                        <i data-lucide="package" style="color: var(--primary-green);"></i>
                                    </div>
                                    <div>
                                        <h3 style="margin: 0; font-size: 1.1rem;">${b.name}</h3>
                                        <div style="font-size: 0.8rem; color: var(--text-muted);">${b.originalName}</div>
                                    </div>
                                </div>
                                <div style="display: flex; align-items: center; gap: 8px;">
                                     <button class="btn-icon small bundle-menu-trigger" data-name="${b.originalName.replace(/"/g, "&quot;")}" data-status="${b.status}" onclick="window.nuclearOpenBundleMenu(this); event.stopPropagation();" onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();" style="width: 28px; height: 28px; cursor: pointer; pointer-events: auto; z-index: 50; position: relative;">
                                        <i data-lucide="more-vertical" style="width: 16px;"></i>
                                     </button>
                                </div>
                            </div>
                            
                            <div style="display: flex; gap: 8px; margin-top: auto; padding-top: 12px; border-top: 1px solid rgba(255,255,255,0.05); font-size: 0.8rem; color: var(--text-muted);">
                                <span>${getStatusText(b.status)}</span>
                                <span style="margin-left: auto;">${window._t('ba2_archive')}</span>
                            </div>
                        </div>
                    `).join('')}
                </div>
            </div>
        `;
    },

    renderWizard(manager, data) {
        const stagedInternal = Array.from(manager.selectedMods);
        const stagedExternal = Array.from(manager.externalFiles);
        const stagedFolders = Array.from(manager.selectedFolders || []);
        const totalStaged = stagedInternal.length + stagedExternal.length + stagedFolders.length;

        if (manager.isPickingInternal) {
            return this.renderInternalPicker(manager, data);
        }

        const formatLabel = window._t('bundle_format_auto');
        const editing = manager.editingBundle;
        const canCreate = manager.canCreateBundle();
        const canSelectArchive = manager.canUseArchiveSelection();
        const canExtract = manager.canExtractSelection();
        const createInfoLabel = escapeAttr(window._t('create_ba2_info_label'));
        const createInfoBody = escapeHtml(window._t('create_ba2_info'));

        return `
            <div class="bundle-wizard workspace-view animate-fade">
                <div class="bundle-wizard-toolbar-wrap">
                    <div class="mods-toolbar bundle-wizard-toolbar bundle-wizard-toolbar-main">
                        <div class="toolbar-left bundle-wizard-nav">
                            <button class="btn-icon" id="btn-cancel-wizard" title="${window._t('back')}">
                                <i data-lucide="arrow-left"></i>
                            </button>
                            <div class="header-with-icon">
                                <h2 class="bundle-wizard-title">${editing ? escapeHtml(window._t('editing_bundle_title', editing.name)) : window._t('bundling_workspace')}</h2>
                            </div>
                        </div>
                        <div class="tool-buttons mods-toolbar-actions">
                            <button type="button" class="btn-secondary btn-deploy-primary btn-start-bundling" ${canCreate ? '' : 'disabled'}>
                                <i data-lucide="${editing ? 'save' : 'zap'}"></i>
                                <span>${window._t(editing ? 'save_bundle' : 'create_bundle')}</span>
                            </button>
                            <div class="bundle-create-info-wrap">
                                <button type="button" class="btn-secondary bundle-create-info-btn" id="btn-create-ba2-info" aria-expanded="false" aria-controls="create-ba2-info-popover" aria-describedby="create-ba2-info-popover" aria-label="${createInfoLabel}">
                                    <i data-lucide="info"></i>
                                </button>
                                <div class="bundle-create-info-popover" id="create-ba2-info-popover" role="tooltip" hidden>${createInfoBody}</div>
                            </div>
                            <div class="mods-actions-menu-wrap">
                                <button type="button" class="btn-secondary mods-actions-trigger" id="btn-bundle-actions" aria-haspopup="true" aria-expanded="false" title="${window._t('actions')}">
                                    <i data-lucide="more-horizontal"></i>
                                </button>
                                <div class="mods-actions-dropdown" id="bundle-actions-dropdown" hidden>
                                    <button type="button" class="mods-actions-item" id="btn-add-external">
                                        <i data-lucide="plus"></i>
                                        <span>${window._t('add_external_ba2')}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-add-internal">
                                        <i data-lucide="list-plus"></i>
                                        <span>${window._t('add_mod_from_list')}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-add-folder">
                                        <i data-lucide="folder-plus"></i>
                                        <span>${window._t('add_folder')}</span>
                                    </button>
                                    <div class="mods-actions-divider" role="separator"></div>
                                    <button type="button" class="mods-actions-item" id="btn-menu-select-all" ${canSelectArchive ? '' : 'disabled'}>
                                        <i data-lucide="list-checks"></i>
                                        <span>${window._t('inspector_select_all')}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-menu-deselect-all" ${canSelectArchive ? '' : 'disabled'}>
                                        <i data-lucide="list-minus"></i>
                                        <span>${window._t('inspector_deselect_all')}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-menu-isolate" ${canExtract ? '' : 'disabled'}>
                                        <i data-lucide="package-plus"></i>
                                        <span>${window._t('bundle_add_to_bundle')}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-menu-extract" ${canExtract ? '' : 'disabled'}>
                                        <i data-lucide="folder-open"></i>
                                        <span>${window._t('bundle_extract_to_folder')}</span>
                                    </button>
                                    <div class="mods-actions-divider" role="separator"></div>
                                    <button type="button" class="mods-actions-item danger" id="btn-clear-workspace">
                                        <i data-lucide="trash-2"></i>
                                        <span>${window._t('clear_workspace')}</span>
                                    </button>
                                </div>
                            </div>
                        </div>
                    </div>
                    <div class="bundle-create-panel">
                        <div class="bundle-create-grid">
                            <label class="bundle-field-group">
                                <span class="bundle-field-label">${window._t('bundle_format')}</span>
                                <select id="bundle-format-select" class="bundle-field">
                                    <option value="Auto" selected>${window._t('bundle_format_auto')}</option>
                                    <option value="General">${window._t('bundle_format_general')}</option>
                                    <option value="DDS">${window._t('bundle_format_dds')}</option>
                                </select>
                            </label>
                            <div class="bundle-field-group bundle-field-group--grow">
                                <span class="bundle-field-label">${window._t('output_name')}</span>
                                <div class="bundle-input-clear-wrap">
                                    <input type="text" id="bundle-name-input" placeholder="MyAmazingBundle" class="bundle-field" value="${escapeAttr(manager.bundleName)}" title="${escapeAttr(window._t(editing ? 'bundle_edit_name_locked' : 'created_in_bundles_dir'))}" ${editing ? 'disabled' : ''}>
                                    <button type="button" class="search-clear" id="bundle-name-clear" title="${escapeAttr(window._t('clear_text'))}" aria-label="${escapeAttr(window._t('clear_text'))}" ${manager.bundleName && !editing ? '' : 'hidden'}>
                                        <i data-lucide="x"></i>
                                    </button>
                                </div>
                            </div>
                            <label class="bundle-field-group">
                                <span class="bundle-field-label">${window._t('compression_level')}</span>
                                <select id="bundle-compression-select" class="bundle-field">
                                    <option value="Default" selected>${window._t('compression_default')}</option>
                                    <option value="1">${window._t('compression_level_1')}</option>
                                    <option value="2">${window._t('compression_level_2')}</option>
                                    <option value="3">${window._t('compression_level_3')}</option>
                                    <option value="4">${window._t('compression_level_4')}</option>
                                    <option value="5">${window._t('compression_level_5')}</option>
                                    <option value="6">${window._t('compression_level_6')}</option>
                                    <option value="7">${window._t('compression_level_7')}</option>
                                    <option value="8">${window._t('compression_level_8')}</option>
                                    <option value="9">${window._t('compression_level_9')}</option>
                                    <option value="0">${window._t('compression_level_0')}</option>
                                </select>
                            </label>
                        </div>
                        <div class="bundle-create-summary">
                            <span>${window._t('total_items')}: <strong>${totalStaged}</strong></span>
                            <span class="bundle-summary-sep">·</span>
                            <span>${window._t('type')}: <strong id="bundle-type-summary">${formatLabel}</strong></span>
                        </div>
                    </div>
                </div>

                <div class="workspace-content">
                    <div class="bundle-file-entry-panel widget bundle-wizard-panel">
                        <div class="widget-header bundle-panel-header">
                            <h3>${window._t('file_entry')}</h3>
                            <div id="file-entry-header-toolbar" class="file-entry-header-toolbar">
                                ${(manager.inspectedBa2Path || manager.inspectedFolderPath) ? renderInspectorToolbar(manager) : ''}
                            </div>
                            <span class="bundle-workspace-count">${window._t('total_items')}: ${totalStaged}</span>
                        </div>
                        <div class="widget-content bundle-file-entry-body">
                            <div class="staged-list file-entry-drop" id="staged-drop-zone">
                                <div id="ba2-inspector-root">
                                    ${renderInspectorContent(manager)}
                                </div>
                            </div>
                            <div id="file-entry-path-footer" class="file-entry-path-footer">${renderInspectorPathFooter(manager)}</div>
                        </div>
                    </div>
                </div>
            </div>
        `;
    },

    renderInternalPicker(manager, data) {
        const availableMods = (data?.mods ?? []).filter(m => m.type === 'ba2' && !m.isBundle);
        const selectedCount = manager.getStagedModCount();

        return `
            <div class="bundle-wizard workspace-view animate-fade" style="padding: 24px; display: flex; flex-direction: column; gap: 24px; height: 100%;">
                <div class="mods-toolbar" style="background: var(--bg-surface); border: 1px solid var(--border-color); padding: 16px 24px; border-radius: 12px; display: flex; justify-content: space-between; align-items: center;">
                    <div class="toolbar-left" style="display: flex; align-items: center; gap: 16px;">
                        <button class="btn-icon" id="btn-close-picker" title="${window._t('finish_selecting')}">
                            <i data-lucide="arrow-left"></i>
                        </button>
                        <div class="header-with-icon" style="display: flex; align-items: center; gap: 12px;">
                            <h2 style="margin: 0; font-size: 1.25rem; font-weight: 700;">${window._t('select_internal_mods')}</h2>
                            <span class="badge" id="picker-count-badge" style="background: var(--primary-green); color: #121212;">${window._t('n_selected_badge', selectedCount)}</span>
                        </div>
                    </div>
                    <button class="btn primary small" id="btn-picker-done">${window._t('done')}</button>
                </div>

                <div class="picker-panel widget" style="flex: 1; display: flex; flex-direction: column; overflow: hidden;">
                    <div class="picker-list" style="flex: 1; overflow-y: auto; padding: 20px; display: grid; grid-template-columns: repeat(auto-fill, minmax(350px, 1fr)); gap: 12px; align-content: start;">
                        ${availableMods.length === 0 ? `
                            <div style="grid-column: 1/-1; padding: 80px; text-align: center; opacity: 0.5;">
                                <i data-lucide="package-search" style="width: 48px; height: 48px; margin-bottom: 16px;"></i>
                                <h3>${window._t('no_ba2_mods_found')}</h3>
                                <p>${window._t('ensure_internal_mods')}</p>
                            </div>
                        ` : availableMods.map(m => {
                            const isSelected = manager.isModStaged(m.originalName);
                            return `
                                <div class="picker-item ${isSelected ? 'selected' : ''}" data-name="${m.originalName}" style="padding: 16px; background: rgba(255,255,255,0.03); border-radius: 10px; border: 1px solid rgba(255,255,255,0.05); cursor: pointer; display: flex; align-items: center; gap: 16px;">
                                    <div class="picker-checkbox" style="width: 20px; height: 20px; border-radius: 4px; border: 2px solid ${isSelected ? 'var(--primary-green)' : 'rgba(255,255,255,0.2)'}; background: ${isSelected ? 'var(--primary-green)' : 'transparent'}; display: flex; align-items: center; justify-content: center;">
                                        ${isSelected ? '<i data-lucide="check" style="width: 14px; color: #121212;"></i>' : ''}
                                    </div>
                                    <div style="flex: 1; min-width: 0;">
                                        <div style="font-weight: 600; font-size: 1rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;">${m.name}</div>
                                        <div style="font-size: 0.75rem; color: var(--text-muted); opacity: 0.7;">${m.originalName}</div>
                                    </div>
                                </div>
                            `;
                        }).join('')}
                    </div>
                </div>
            </div>
        `;
    }
};
