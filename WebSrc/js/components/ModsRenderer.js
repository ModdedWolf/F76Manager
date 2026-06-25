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
        <button type="button" class="btn-icon btn-mod-update btn-mod-state-off" title="No Nexus update" disabled>
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
            if (original.endsWith('.ini') || original.endsWith('.json') || original.endsWith('.txt')) return false;

            if (m.isBundle && m.status === 'disabled') return false;

            if (manager.currentPreset === 'uncategorized') {
                return !m.group;
            } else if (manager.currentPreset !== 'all') {
                return m.group === manager.currentPreset;
            }
            return true;
        });

        filteredMods.sort((a, b) => {
            const loA = typeof a.loadOrder === 'number' ? a.loadOrder : 9999;
            const loB = typeof b.loadOrder === 'number' ? b.loadOrder : 9999;
            return loA - loB;
        });

        const enabledCount = mods.filter(m => m.status === 'enabled').length;

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
                        
                        <div class="preset-controls">
                            <div class="preset-select-wrapper">
                                <select id="preset-select" class="preset-select">
                                    <option value="all" ${manager.currentPreset === 'all' ? 'selected' : ''}>${window._t('all_mods')}</option>
                                    <option value="uncategorized" ${manager.currentPreset === 'uncategorized' ? 'selected' : ''}>${window._t('uncategorized')}</option>
                                    ${Object.keys(data?.modGroups ?? {}).map(g => `
                                        <option value="${escapeAttr(g)}" ${manager.currentPreset === g ? 'selected' : ''}>${escapeHtml(g)}</option>
                                    `).join('')}
                                    <option value="__NEW__">${window._t('new_preset')}</option>
                                </select>
                            </div>
                            ${manager.currentPreset !== 'all' && manager.currentPreset !== 'uncategorized' ? `
                                <button id="btn-delete-preset" class="btn-icon danger" style="width: 38px; height: 38px; display: flex; align-items: center; justify-content: center; border-radius: 8px; margin-left: 8px;" title="${window._t('delete_preset')}">
                                    <i data-lucide="x"></i>
                                </button>
                            ` : ''}
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
                                <button type="button" class="mods-actions-item" id="mods-action-transfer-to-other">
                                    <i data-lucide="arrow-right-left"></i>
                                    <span>${window._t('transfer_mods_to_other')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="mods-action-transfer-from-other">
                                    <i data-lucide="arrow-right-left"></i>
                                    <span>${window._t('transfer_mods_from_other')}</span>
                                </button>
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
                        ${filteredMods.length === 0 ? this.renderNoMods(data) : `
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
                                    ${filteredMods.map(m => this.renderModRow(m)).join('')}
                                </tbody>
                            </table>
                        `}
                    </div>
                </div>
            </div>
        `;
    },



    renderModRow(mod) {
        const isEnabled = mod.status === 'enabled';
        const origPath = mod.originalName || '';
        let displayName = (mod.name || origPath || '').replace(/^Disabled\//, '');
        if (/GameRoot\//i.test(origPath) && !(mod.name || '').trim()) {
            displayName = origPath.split('/').filter(Boolean).pop() || displayName;
        }
        const searchText = (displayName + ' ' + (mod.originalName || '')).toLowerCase();
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
        return `
            <tr class="mod-row ${isEnabled ? '' : 'mod-disabled'}" data-name="${safeOriginalNameAttr}" data-search-text="${escapeAttr(searchText)}" draggable="false">
                <td class="drag-handle-cell">
                    <div class="drag-handle-wrapper" draggable="true" data-name="${safeOriginalNameAttr}">
                        <i data-lucide="grip-vertical"></i>
                    </div>
                </td>
                <td class="checkbox-cell" onclick="event.stopPropagation();">
                    <input type="checkbox" class="mod-select" ${isEnabled ? 'checked' : ''} onclick="window.handleModToggle(this, '${safeToggleArg}'); event.stopPropagation();">
                </td>
                <td class="mod-name-cell">
                    <div class="mod-name-wrapper">
                        <span class="mod-name" title="${safeDisplayTitle}">${safeDisplayName}</span>
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
                            <button type="button" class="btn-icon btn-delete-mod" title="Delete" data-name="${safeOriginalNameAttr}" onclick="window.nuclearDelete(this); event.stopPropagation();" onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();" style="position: relative; z-index: 50; color: #ef4444;">
                                <i data-lucide="trash-2"></i>
                            </button>
                        </span>
                    </div>
                </td>
            </tr>
        `;
    },


    renderNoMods(data) {
        return `
            <div class="empty-state-container polished" style="flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; background: rgba(255, 255, 255, 0.02); border: 1px dashed rgba(255, 255, 255, 0.1); border-radius: 16px; text-align: center; padding: 48px;">
                <div class="empty-state-icon" style="width: 80px; height: 80px; background: rgba(184, 197, 164, 0.1); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin-bottom: 24px;">
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