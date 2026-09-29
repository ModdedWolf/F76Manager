import { escapeAttr, escapeHtml, escapeJsSingleQuoted } from '../utils/htmlSafe.js';

export function getModNexusActionState(mod) {
    if (!mod?.nexusModId) return { hasLink: false, hasUpdate: false, isUnverified: false };
    const fileId = mod.nexusFileId;
    const lacksFileId = fileId == null || fileId === '' || Number(fileId) <= 0;
    const isUnverified = !!(mod.isUnverifiedLink || lacksFileId);
    const hasUpdate = !!(mod.hasUpdate && !isUnverified);
    return { hasLink: true, hasUpdate, isUnverified };
}

export function buildModUpdateButtonHtml(mod) {
    const { hasUpdate } = getModNexusActionState(mod);
    const safeOriginalNameAttr = escapeAttr(mod.originalName || '');
    const safeNexusModId = escapeAttr(mod.nexusModId != null ? String(mod.nexusModId) : '');

    if (hasUpdate) {
        const safeLatestFileId = escapeAttr(mod.latestFileId != null ? String(mod.latestFileId) : '');
        const safeLatestVersion = escapeAttr(mod.latestVersion || '');
        const safeLatestFileName = escapeAttr(mod.latestFileName || '');
        const safeLatestUploaded = escapeAttr(mod.latestUploaded != null ? String(mod.latestUploaded) : '');
        return `
            <button type="button" class="btn-icon btn-mod-update" title="${escapeAttr(window._t('mod_update_available'))}"
                data-name="${safeOriginalNameAttr}"
                data-mod-id="${safeNexusModId}"
                data-file-id="${safeLatestFileId}"
                data-file-name="${safeLatestFileName}"
                data-file-version="${safeLatestVersion}"
                data-file-uploaded="${safeLatestUploaded}"
                onclick="window.nuclearModUpdate(this); event.stopPropagation();"
                onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();">
                <i data-lucide="arrow-down"></i>
            </button>`;
    }

    return `
        <button type="button" class="btn-icon btn-mod-update-none btn-mod-state-off" title="${escapeAttr(window._t('mod_already_up_to_date') || 'Already up to date')}" disabled>
            <i data-lucide="arrow-down"></i>
        </button>`;
}

export function buildModUnverifiedButtonHtml(mod) {
    const { isUnverified } = getModNexusActionState(mod);
    const safeOriginalNameAttr = escapeAttr(mod.originalName || '');
    if (isUnverified) {
        return `
            <button type="button" class="btn-icon btn-mod-nexus-unverified" title="${escapeAttr(window._t('mod_nexus_link_unverified'))}"
                data-name="${safeOriginalNameAttr}"
                data-open-tab="nexus"
                onclick="window.nuclearEdit(this); event.stopPropagation();"
                onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();">
                <i data-lucide="link-2"></i>
            </button>`;
    }

    return `
        <button type="button" class="btn-icon btn-mod-nexus-unverified btn-mod-state-off" title="Nexus link is verified" disabled>
            <i data-lucide="link-2"></i>
        </button>`;
}

export const ModsRenderer = {
    render(manager, data) {
        const mods = data?.mods ?? [];

        let filteredMods = mods.filter(m => {
            const original = String(m?.originalName || '').toLowerCase();
            if (original.endsWith('.ini') || original.endsWith('.json') || original.endsWith('.txt') || original.endsWith('.toml')) return false;

            if (m.isBundle && m.status === 'disabled') return false;

            const baseName = original.split('/').pop() || '';
            const isStringMod = /\.(strings|dlstrings|ilstrings)$/i.test(baseName);
            if (!isStringMod && manager.modGroupsManager && !manager.modGroupsManager.shouldShowMod(m.originalName, m.files)) {
                return false;
            }
            return true;
        });

        filteredMods.sort((a, b) => {
            const loA = typeof a.loadOrder === 'number' ? a.loadOrder : 9999;
            const loB = typeof b.loadOrder === 'number' ? b.loadOrder : 9999;
            return loA - loB;
        });

        const enabledCount = filteredMods.filter(m => m.status === 'enabled').length;
        const presetNames = manager.modGroupsManager?.listNames?.()
            ?? Object.keys(data?.modPresets ?? data?.modGroups ?? {});
        const activePreset = manager.modGroupsManager?.getActiveName?.() ?? manager.currentPreset;
        const isDefaultPreset = manager.modGroupsManager?.isDefaultPreset?.(activePreset)
            ?? String(activePreset || '').localeCompare('Default', undefined, { sensitivity: 'accent' }) === 0;
        const updateModsInAllPresets = !!(data?.managerSettings?.updateModsInAllPresets);
        const receiveUpdatesOn = updateModsInAllPresets
            || !!(manager.modGroupsManager?.getReceiveUpdates?.(activePreset));
        const receiveUpdatesDisabled = updateModsInAllPresets;
        const receiveUpdatesTitle = receiveUpdatesDisabled
            ? (window._t('preset_receive_updates_global_hint') || "Controlled by 'Update mods in all presets' in Settings")
            : (window._t('preset_receive_updates') || 'Receive mod updates');

        return `
            <div class="mods-page animate-fade">
                <div class="mods-toolbar">
                    <div class="toolbar-left">
                        <a href="https://www.nexusmods.com/games/fallout76" 
                           class="mods-nexus-link" 
                           target="_blank" 
                           rel="noopener noreferrer"
                           title="Nexus Mods - Fallout 76">
                            <img src="assets/Nexus.png" alt="Nexus Mods Fallout 76">
                        </a>
                        <div class="search-box">
                            <i data-lucide="search"></i>
                            <input type="text" id="mods-search" placeholder="${escapeAttr(window._t('search_mods_placeholder'))}" value="${escapeAttr(manager.lastSearchTerm)}">
                            <button type="button" class="search-clear" id="mods-search-clear" title="${escapeAttr(window._t('clear_search'))}" aria-label="${escapeAttr(window._t('clear_search'))}">
                                <i data-lucide="x"></i>
                            </button>
                        </div>
                        
                        <div class="preset-menu-wrap">
                            <button type="button" class="preset-menu-trigger" id="btn-preset-menu" aria-haspopup="true" aria-expanded="false" title="${escapeAttr(window._t('presets_label') || 'Presets')}">
                                <span id="preset-menu-label">${escapeHtml(activePreset)}</span>
                                <i data-lucide="chevron-down"></i>
                            </button>
                            <div class="preset-menu-dropdown" id="preset-menu-dropdown" hidden>
                                ${presetNames.map(g => `
                                    <button type="button" class="preset-menu-item ${activePreset === g ? 'active' : ''}" data-preset="${escapeAttr(g)}">
                                        <span>${escapeHtml(g)}</span>
                                        ${activePreset === g ? '<i data-lucide="check"></i>' : ''}
                                    </button>
                                `).join('')}
                                <div class="preset-menu-sep"></div>
                                <button type="button" class="preset-menu-item" data-preset-action="new">
                                    <span>${window._t('new_preset')}</span>
                                    <i data-lucide="plus"></i>
                                </button>
                                <button type="button" class="preset-menu-item${receiveUpdatesOn ? ' active' : ''}" data-preset-action="receive-updates"
                                    ${receiveUpdatesDisabled ? 'disabled' : ''}
                                    title="${escapeAttr(receiveUpdatesTitle)}"
                                    aria-checked="${receiveUpdatesOn ? 'true' : 'false'}">
                                    <span>${window._t('preset_receive_updates') || 'Receive mod updates'}</span>
                                    ${receiveUpdatesOn ? '<i data-lucide="check"></i>' : '<i data-lucide="download-cloud"></i>'}
                                </button>
                                ${isDefaultPreset ? '' : `
                                <button type="button" class="preset-menu-item danger" data-preset-action="delete">
                                    <span>${window._t('delete_preset')}</span>
                                    <i data-lucide="trash-2"></i>
                                </button>`}
                            </div>
                        </div>
                    </div>
                    <div class="tool-buttons mods-toolbar-actions">
                        <button type="button" class="btn-secondary btn-deploy-primary" id="btn-deploy-all">
                            <i data-lucide="zap"></i>
                            <span id="btn-deploy-all-label">${enabledCount > 0 ? window._t('deploy_n_mods', enabledCount) : window._t('deploy_mods')}</span>
                        </button>
                        <div class="mods-actions-menu-wrap">
                            <button type="button" class="btn-secondary mods-actions-trigger" id="btn-mods-actions" aria-haspopup="true" aria-expanded="false" title="${escapeAttr(window._t('actions'))}">
                                <i data-lucide="more-horizontal"></i>
                            </button>
                            <div class="mods-actions-dropdown" id="mods-actions-dropdown" hidden>
                                <button type="button" class="mods-actions-item" id="mods-action-add-mod">
                                    <i data-lucide="plus"></i>
                                    <span>${window._t('add_mod')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="mods-action-bulk-update">
                                    <i data-lucide="download-cloud"></i>
                                    <span id="mods-action-bulk-update-label">${window._t('bulk_update_mods')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="mods-action-backup-mods">
                                    <i data-lucide="archive"></i>
                                    <span>${window._t('backup_mods')}</span>
                                </button>
                                <div class="mods-actions-divider" role="separator"></div>
                                <button type="button" class="mods-actions-item" id="mods-action-preset-rename">
                                    <i data-lucide="pencil"></i>
                                    <span>${window._t('preset_rename')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="mods-action-preset-new">
                                    <i data-lucide="plus"></i>
                                    <span>${window._t('new_preset')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="mods-action-preset-duplicate">
                                    <i data-lucide="copy"></i>
                                    <span>${window._t('preset_duplicate')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="mods-action-preset-export">
                                    <i data-lucide="upload"></i>
                                    <span>${window._t('preset_export')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="mods-action-preset-import">
                                    <i data-lucide="download"></i>
                                    <span>${window._t('preset_import')}</span>
                                </button>
                                ${isDefaultPreset ? '' : `
                                <button type="button" class="mods-actions-item danger" id="mods-action-preset-delete">
                                    <i data-lucide="trash-2"></i>
                                    <span>${window._t('delete_preset')}</span>
                                </button>`}
                                <div class="mods-actions-divider" role="separator"></div>
                                <button type="button" class="mods-actions-item" id="mods-action-transfer-to-other">
                                    <i data-lucide="arrow-right-left"></i>
                                    <span>${window._t('transfer_mods_to_other')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="mods-action-transfer-from-other">
                                    <i data-lucide="arrow-right-left"></i>
                                    <span>${window._t('transfer_mods_from_other')}</span>
                                </button>
                                <div class="mods-actions-divider" role="separator"></div>
                                <button type="button" class="mods-actions-item" id="mods-action-badge-color">
                                    <i data-lucide="palette"></i>
                                    <span>${window._t('edit_badge_color')}</span>
                                </button>
                                <button type="button" class="mods-actions-item danger" id="mods-action-delete-all">
                                    <i data-lucide="trash-2"></i>
                                    <span>${window._t('delete_all_mods')}</span>
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
                <div class="mods-list-container">
                    <div class="mods-table-container">
                        ${filteredMods.length === 0 ? this.renderNoMods(data, manager) : `
                            <table class="mods-table">
                                <colgroup>
                                    <col class="col-drag-handle">
                                    <col class="col-checkbox">
                                    <col class="col-mod-name">
                                    <col class="col-type">
                                    <col class="col-version">
                                    <col class="col-actions">
                                </colgroup>
                                <thead>
                                    <tr>
                                        <th style="width: 44px"></th>
                                        <th style="width: 40px">
                                            <input type="checkbox" id="select-all-mods" ${filteredMods.length > 0 && filteredMods.every(m => m.status === 'enabled') ? 'checked' : ''}>
                                        </th>
                                        <th>${window._t('name')}</th>
                                        <th class="type-col-header">${window._t('type')}</th>
                                        <th class="version-col-header">${window._t('version')}</th>
                                        <th class="actions-col">${window._t('actions')}</th>
                                    </tr>
                                </thead>
                                <tbody id="mods-list-body">
                                    ${filteredMods.map(m => this.renderModRow(m, manager)).join('')}
                                </tbody>
                            </table>
                        `}
                    </div>
                </div>
            </div>
        `;
    },



    renderModRow(mod, manager) {
        const isEnabled = mod.status === 'enabled';
        const isLoose = !!(mod.isLoose || mod.type === 'loose');
        const origPath = mod.originalName || '';
        let displayName = (mod.name || origPath || '').replace(/^Disabled\//, '').replace(/^Loose\//, '');
        if (/GameRoot\//i.test(origPath) && !(mod.name || '').trim()) {
            displayName = origPath.split('/').filter(Boolean).pop() || displayName;
        }
        const files = Array.isArray(mod.files) ? mod.files.filter(Boolean) : [];
        const fileCount = files.length;
        const searchText = (
            displayName + ' ' + (mod.originalName || '') + ' ' + files.join(' ')
        ).toLowerCase();
        const originalName = mod.originalName || '';
        const safeOriginalNameAttr = escapeAttr(originalName);
        const safeDisplayName = escapeHtml(displayName);
        const safeDisplayTitle = escapeAttr(displayName);
        const safeFileName = escapeHtml(originalName);
        const safeOriginalTitle = escapeAttr(originalName);
        const safeType = escapeHtml((mod.type || '').toUpperCase());
        const safeTypeClass = escapeAttr(mod.type || 'unknown');
        const safeToggleArg = escapeJsSingleQuoted(originalName);
        const displayVersion = (mod.version && String(mod.version).trim()) ? String(mod.version).trim() : '-';
        const safeVersion = escapeHtml(displayVersion);
        const updateSlotHtml = buildModUpdateButtonHtml(mod);
        const unverifiedSlotHtml = buildModUnverifiedButtonHtml(mod);
        const expanded = isLoose && manager?.expandedLooseKeys?.has?.(originalName);
        const fileCountLabel = fileCount === 1
            ? (window._t('loose_mod_file_count_one') || '1 file')
            : (window._t('loose_mod_file_count', fileCount) || `${fileCount} files`);

        const expandBtn = isLoose ? `
            <button type="button"
                    class="mod-loose-expand ${expanded ? 'is-expanded' : ''}"
                    data-name="${safeOriginalNameAttr}"
                    title="${escapeAttr(expanded ? (window._t('loose_mod_collapse') || 'Collapse files') : (window._t('loose_mod_expand') || 'Show files'))}"
                    aria-expanded="${expanded ? 'true' : 'false'}"
                    onclick="window.toggleLooseModExpand && window.toggleLooseModExpand(this); event.stopPropagation();"
                    onpointerdown="event.stopPropagation();"
                    onmousedown="event.stopPropagation();">
                <i data-lucide="chevron-right"></i>
            </button>` : '';

        const nameMeta = isLoose ? `
            <span class="mod-loose-file-count" aria-hidden="true">· ${escapeHtml(fileCountLabel)}</span>
        ` : '';

        const childRow = (isLoose && expanded) ? `
            <tr class="mod-loose-files-row ${isEnabled ? '' : 'mod-disabled'}" data-parent="${safeOriginalNameAttr}">
                <td colspan="6">
                    <ul class="mod-loose-files-list">
                        ${files.length === 0
                            ? `<li class="mod-loose-files-empty">${escapeHtml(window._t('loose_mod_no_files') || 'No files listed')}</li>`
                            : files.map(f => `<li title="${escapeAttr(f)}">${escapeHtml(f)}</li>`).join('')}
                    </ul>
                </td>
            </tr>
        ` : '';

        return `
            <tr class="mod-row ${isEnabled ? '' : 'mod-disabled'}${isLoose ? ' mod-row-loose' : ''}" data-name="${safeOriginalNameAttr}" data-search-text="${escapeAttr(searchText)}" data-is-loose="${isLoose ? '1' : '0'}" draggable="false">
                <td class="drag-handle-cell">
                    <div class="drag-handle-wrapper" draggable="true" data-name="${safeOriginalNameAttr}">
                        <i data-lucide="grip-vertical"></i>
                    </div>
                </td>
                <td class="checkbox-cell" onclick="event.stopPropagation();">
                    <input type="checkbox" class="mod-select" ${isEnabled ? 'checked' : ''} onclick="window.handleModToggle(this, '${safeToggleArg}'); event.stopPropagation();">
                </td>
                <td class="mod-name-cell">
                    <div class="mod-name-wrapper${isLoose ? ' mod-name-wrapper--loose' : ''}">
                        ${expandBtn}
                        <span class="mod-name" title="${safeDisplayTitle}">${safeDisplayName}</span>
                        ${nameMeta}
                        <span class="mod-sep" style="display:none">/</span>
                        <span class="mod-filename" title="${safeOriginalTitle}" style="display:none">${safeFileName}</span>
                    </div>
                </td>
                <td class="type-cell">
                    <span class="badge badge-${safeTypeClass}">${safeType}</span>
                </td>
                <td class="version-cell" title="${safeVersion}">
                    <span class="mod-version-text">${safeVersion}</span>
                </td>
                <td class="actions-cell">
                    <div class="actions-cell-inner">
                        <span class="action-slot action-slot-update">${updateSlotHtml}</span>
                        <span class="action-slot action-slot-unverified">${unverifiedSlotHtml}</span>
                        <span class="action-slot action-slot-edit">
                            <button type="button" class="btn-icon btn-edit-mod" title="Edit" data-name="${safeOriginalNameAttr}" onclick="window.nuclearEdit(this); event.stopPropagation();" onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();">
                                <i data-lucide="edit-3"></i>
                            </button>
                        </span>
                        <span class="action-slot action-slot-delete">
                            <button type="button" class="btn-icon btn-delete-mod" title="Delete" data-name="${safeOriginalNameAttr}" onclick="window.nuclearDelete(this); event.stopPropagation();" onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();" style="position: relative; z-index: 50; color: var(--danger-red);">
                                <i data-lucide="trash-2"></i>
                            </button>
                        </span>
                    </div>
                </td>
            </tr>
            ${childRow}
        `;
    },


    renderNoMods(data, manager) {
        const allMods = data?.mods || [];
        const hasLibraryMods = allMods.some(m => {
            const o = String(m?.originalName || '').toLowerCase();
            return !o.endsWith('.ini') && !o.endsWith('.json') && !o.endsWith('.txt') && !o.endsWith('.toml');
        });
        const activeMembers = manager?.modGroupsManager?.getActive()?.mods || [];
        const membershipEmpty = activeMembers.length === 0;
        const membershipMatchesLibrary = activeMembers.some(name =>
            allMods.some(m => String(m?.originalName || '') === String(name || ''))
        );
        const presetEmpty = !!(manager?.modGroupsManager
            && hasLibraryMods
            && (membershipEmpty || !membershipMatchesLibrary));

        if (presetEmpty) {
            const name = manager.modGroupsManager.getActiveName();
            return `
                <div class="empty-state-container polished" style="flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; background: rgba(255, 255, 255, 0.02); border: 1px dashed rgba(255, 255, 255, 0.1); border-radius: 16px; text-align: center; padding: 48px;">
                    <div class="empty-state-icon" style="width: 80px; height: 80px; background: rgba(var(--primary-rgb), 0.1); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin-bottom: 24px;">
                        <i data-lucide="folder-open" style="width: 40px; height: 40px; color: var(--primary-green);"></i>
                    </div>
                    <h3>${escapeHtml(window._t('preset_empty_title') || 'Preset is empty')}</h3>
                    <p style="color: var(--text-muted); max-width: 400px; line-height: 1.6; margin-bottom: 32px;">${escapeHtml(window._t('preset_empty_hint_files', name) || `No mods in ${name} yet. Add files from Explorer to build this loadout.`)}</p>
                    <button type="button" class="btn primary" id="btn-preset-add-from-files" style="padding: 12px 24px; font-size: 1rem;">
                        <i data-lucide="folder-plus"></i>
                        ${escapeHtml(window._t('preset_add_from_files') || 'Add mods from files…')}
                    </button>
                </div>
            `;
        }

        return `
            <div class="empty-state-container polished" style="flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; background: rgba(255, 255, 255, 0.02); border: 1px dashed rgba(255, 255, 255, 0.1); border-radius: 16px; text-align: center; padding: 48px;">
                <div class="empty-state-icon" style="width: 80px; height: 80px; background: rgba(var(--primary-rgb), 0.1); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin-bottom: 24px;">
                    <i data-lucide="package-open" style="width: 40px; height: 40px; color: var(--primary-green);"></i>
                </div>
                <h3>${window._t('no_mods_found')}</h3>
                <p style="color: var(--text-muted); max-width: 400px; line-height: 1.6; margin-bottom: 32px;">${window._t('no_mods_hint')}</p>
                <button class="btn primary" style="padding: 12px 24px; font-size: 1rem;" onclick="window.chrome.webview.postMessage({type: 'ADD_MOD'})">
                    <i data-lucide="plus"></i>
                    ${window._t('add_first_mod')}
                </button>
            </div>
        `;
    }
};