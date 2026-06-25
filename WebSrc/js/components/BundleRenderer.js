import { escapeHtml, escapeAttr } from '../utils/htmlSafe.js';

function formatBa2Size(bytes) {
    const n = Number(bytes) || 0;
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    return `${(n / (1024 * 1024)).toFixed(1)} MB`;
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
    let total = node.files.length;
    for (const key of Object.keys(node.folders)) {
        total += annotateTreeFileCounts(node.folders[key]);
    }
    node.totalFiles = total;
    return total;
}

function countSelectedInNode(node, selectedSet) {
    let count = 0;
    for (const file of node.files) {
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

function getStagedPartialBadge(manager, archivePath) {
    const key = manager.normalizeArchiveKey(archivePath);
    const total = manager.ba2TotalPaths.get(key);
    if (!total) return '';
    const selected = manager.getSelectedCount(key);
    if (selected >= total) return '';
    return `<div class="staged-partial-badge">${escapeHtml(window._t('staged_partial_files', selected, total))}</div>`;
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
            fullPath
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

    for (const file of files) {
        if (!pathMatchesFilter(file.fullPath, filter)) continue;
        const normPath = normalizeBa2InternalPath(file.fullPath);
        const isChecked = selectedSet.has(normPath);
        const fileCheckHtml = readOnly ? '' : `
                <input type="checkbox" class="ba2-tree-check ba2-tree-file-check"
                    data-check-type="file"
                    data-file-path="${escapeAttr(normPath)}"
                    ${isChecked ? 'checked' : ''}>`;
        html += `
            <div class="ba2-tree-row ba2-tree-file${readOnly ? ' ba2-tree-row--readonly' : ''}" style="padding-left: ${pad}px">
                ${fileCheckHtml}
                <i data-lucide="file" class="ba2-tree-file-icon"></i>
                <span class="ba2-tree-file-name">${escapeHtml(file.name)}</span>
                <span class="ba2-tree-file-size">${escapeHtml(formatBa2Size(file.size))}</span>
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

function renderInspectorToolbar(manager) {
    if (manager.inspectedFolderPath) {
        const pathDisplay = manager.inspectedFolderPath;
        const pathTooltip = escapeAttr(pathDisplay);
        const pathText = escapeHtml(pathDisplay);
        const filterValue = escapeAttr(manager.inspectorFilter || '');
        const folderKey = manager.getArchiveSelectionKey();
        const hasFilter = Boolean(manager.inspectorFilter);
        const selectedCount = folderKey
            ? (hasFilter ? manager.getFilteredSelectedCount(folderKey) : manager.getSelectedCount(folderKey))
            : 0;
        const totalCount = folderKey
            ? (hasFilter ? manager.getFilteredEntryCount() : (manager.ba2TotalPaths.get(folderKey) || manager.inspectorFileCount || 0))
            : 0;
        const countLabel = totalCount
            ? (hasFilter
                ? window._t('inspector_selected_count_filtered', selectedCount, totalCount)
                : window._t('inspector_selected_count', selectedCount, totalCount))
            : '';

        return `
        <div class="bundle-inspector-pathbar">
            <div class="bundle-path-bar" title="${pathTooltip}">${pathText}</div>
            <div class="search-box bundle-inspector-search-wrap">
                <i data-lucide="search"></i>
                <input type="search" id="ba2-inspector-filter" class="bundle-field" placeholder="${escapeHtml(window._t('inspector_search_placeholder'))}" value="${filterValue}">
            </div>
            <div class="bundle-inspector-pathbar-meta">
                ${countLabel ? `<span>${escapeHtml(countLabel)}</span>` : ''}
            </div>
            <div class="bundle-inspector-pathbar-actions">
                <button type="button" class="bundle-text-btn" id="btn-inspector-select-all">${window._t('inspector_select_all')}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-deselect-all">${window._t('inspector_deselect_all')}</button>
            </div>
        </div>
    `;
    }

    const pathDisplay = manager.resolveArchivePath(manager.inspectedBa2Path);
    const pathTooltip = escapeAttr(pathDisplay);
    const pathText = escapeHtml(pathDisplay);
    const stagedSources = manager.getStagedBa2Sources();
    const stagedOptions = stagedSources.map(src => `
        <option value="${escapeAttr(src.inspectPath)}" ${manager.normalizeArchiveKey(src.inspectPath) === manager.normalizeArchiveKey(manager.inspectedBa2Path) ? 'selected' : ''}>${escapeHtml(src.name)}</option>
    `).join('');
    const filterValue = escapeAttr(manager.inspectorFilter || '');
    const archiveKey = manager.getArchiveSelectionKey();
    const hasFilter = Boolean(manager.inspectorFilter);
    const selectedCount = archiveKey
        ? (hasFilter ? manager.getFilteredSelectedCount(archiveKey) : manager.getSelectedCount(archiveKey))
        : 0;
    const totalCount = archiveKey
        ? (hasFilter ? manager.getFilteredEntryCount() : (manager.ba2TotalPaths.get(archiveKey) || manager.inspectorFileCount || 0))
        : 0;
    const canExtract = archiveKey && manager.getEffectiveSelectionPaths(archiveKey).length > 0;
    const countLabel = totalCount
        ? (hasFilter
            ? window._t('inspector_selected_count_filtered', selectedCount, totalCount)
            : window._t('inspector_selected_count', selectedCount, totalCount))
        : '';
    const showStagedSwitcher = stagedSources.length >= 2;

    return `
        <div class="bundle-inspector-pathbar">
            ${showStagedSwitcher ? `
                <select id="bundle-staged-ba2-select" class="bundle-field bundle-staged-ba2-select" title="${escapeAttr(window._t('staged_items', stagedSources.length))}">
                    ${stagedOptions}
                </select>
            ` : ''}
            <div class="bundle-path-bar" title="${pathTooltip}">${pathText}</div>
            <div class="search-box bundle-inspector-search-wrap">
                <i data-lucide="search"></i>
                <input type="search" id="ba2-inspector-filter" class="bundle-field" placeholder="${escapeHtml(window._t('inspector_search_placeholder'))}" value="${filterValue}">
            </div>
            <div class="bundle-inspector-pathbar-meta">
                ${manager.inspectorArchiveType ? `<span>${escapeHtml(manager.inspectorArchiveType)}</span>` : ''}
                ${totalCount ? `<span>${escapeHtml(countLabel)}</span>` : ''}
            </div>
            <div class="bundle-inspector-pathbar-actions">
                <button type="button" class="bundle-text-btn" id="btn-inspector-select-all">${window._t('inspector_select_all')}</button>
                <button type="button" class="bundle-text-btn" id="btn-inspector-deselect-all">${window._t('inspector_deselect_all')}</button>
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
    if (!manager.ba2Contents || manager.ba2Contents.length === 0) {
        const emptyMsg = isFolderMode ? window._t('inspector_folder_empty') : window._t('no_ba2_selected');
        return `<div class="ba2-explorer-empty">${escapeHtml(emptyMsg)}</div>`;
    }

    const filter = manager.inspectorFilter || '';
    let entries = manager.ba2Contents;
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
    if (manager.inspectorLoading && isInspecting) {
        inspectBlock = `
            <div class="ba2-explorer-tree">${buildBa2ExplorerTreeHtml(manager, { unified: true })}</div>
        `;
    } else if (isInspecting) {
        inspectBlock = `
            <div class="ba2-explorer-tree">${buildBa2ExplorerTreeHtml(manager, { unified: true })}</div>
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
    renderFolderChildrenHtml,

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
                                <h2 style="margin: 0; font-size: 1.25rem; font-weight: 700;">${window._t('mod_bundles')}</h2>
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
                            <h2 style="margin: 0; font-size: 1.25rem; font-weight: 700;">${window._t('mod_bundles')}</h2>
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

        const formatLabel = window._t('bundle_format_general');
        const canCreate = manager.canCreateBundle();
        const canSelectArchive = manager.canUseArchiveSelection();
        const canExtract = manager.canExtractSelection();

        return `
            <div class="bundle-wizard workspace-view animate-fade">
                <div class="bundle-wizard-toolbar-wrap">
                    <div class="mods-toolbar bundle-wizard-toolbar bundle-wizard-toolbar-main">
                        <div class="toolbar-left bundle-wizard-nav">
                            <button class="btn-icon" id="btn-cancel-wizard" title="${window._t('back')}">
                                <i data-lucide="arrow-left"></i>
                            </button>
                            <div class="header-with-icon">
                                <h2 class="bundle-wizard-title">${window._t('bundling_workspace')}</h2>
                            </div>
                        </div>
                        <div class="tool-buttons mods-toolbar-actions">
                            <button type="button" class="btn-secondary btn-deploy-primary btn-start-bundling" ${canCreate ? '' : 'disabled'}>
                                <i data-lucide="zap"></i>
                                <span>${window._t('create_bundle')}</span>
                            </button>
                            <div class="mods-actions-menu-wrap">
                                <button type="button" class="btn-secondary mods-actions-trigger" id="btn-bundle-actions" aria-haspopup="true" aria-expanded="false" title="${window._t('actions')}">
                                    <i data-lucide="more-horizontal"></i>
                                </button>
                                <div class="mods-actions-dropdown" id="bundle-actions-dropdown" hidden>
                                    <button type="button" class="mods-actions-item" id="btn-add-internal">
                                        <i data-lucide="list-plus"></i>
                                        <span>${window._t('add_mod_from_list')}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-add-external">
                                        <i data-lucide="plus"></i>
                                        <span>${window._t('add_external_ba2')}</span>
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
                                    <option value="General" selected>${window._t('bundle_format_general')}</option>
                                    <option value="DDS">${window._t('bundle_format_dds')}</option>
                                </select>
                            </label>
                            <label class="bundle-field-group bundle-field-group--grow">
                                <span class="bundle-field-label">${window._t('output_name')}</span>
                                <input type="text" id="bundle-name-input" placeholder="MyAmazingBundle" class="bundle-field" value="${escapeAttr(manager.bundleName)}" title="${escapeAttr(window._t('created_in_bundles_dir'))}">
                            </label>
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
                        </div>
                    </div>
                </div>
            </div>
        `;
    },

    renderInternalPicker(manager, data) {
        const availableMods = (data?.mods ?? []).filter(m => m.type === 'ba2' && !m.isBundle);
        const selectedCount = manager.selectedMods.size;

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
                            const isSelected = manager.selectedMods.has(m.originalName);
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
