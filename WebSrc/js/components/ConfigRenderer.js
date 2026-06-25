import { escapeAttr, escapeHtml, escapeJsSingleQuoted } from '../utils/htmlSafe.js';

export const ConfigRenderer = {
    render(manager, data) {
        const mods = data?.mods ?? [];
        let filtered = mods.filter(m => {
            const original = String(m?.originalName || '').toLowerCase();
            return original.endsWith('.ini') || original.endsWith('.json') || original.endsWith('.txt');
        });

        const tConfigPlaceholder = String(window._t?.('search_config_placeholder') ?? '');
        const tModsPlaceholder = String(window._t?.('search_mods_placeholder') ?? '');
        const searchPlaceholder = (tConfigPlaceholder && tConfigPlaceholder !== 'search_config_placeholder')
            ? tConfigPlaceholder
            : (tModsPlaceholder && tModsPlaceholder !== 'search_mods_placeholder')
                ? tModsPlaceholder
                : 'Search...';

        const customOrderIndex = new Map();
        if (Array.isArray(manager.customOrder)) {
            manager.customOrder.forEach((key, idx) => {
                const k = String(key || '');
                if (k) customOrderIndex.set(k, idx);
            });
        }

        filtered.sort((a, b) => {
            let valA, valB;
            if (manager.sortField === 'index' || !manager.sortField) {
                const aKey = String(a?.originalName || '');
                const bKey = String(b?.originalName || '');
                const ai = customOrderIndex.has(aKey) ? customOrderIndex.get(aKey) : Number.POSITIVE_INFINITY;
                const bi = customOrderIndex.has(bKey) ? customOrderIndex.get(bKey) : Number.POSITIVE_INFINITY;
                if (ai !== bi) return ai - bi;
                const an = String(a.name || a.originalName || '').toLowerCase();
                const bn = String(b.name || b.originalName || '').toLowerCase();
                if (an < bn) return -1;
                if (an > bn) return 1;
                return 0;
            }

            if (manager.sortField === 'name') {
                valA = a.name || a.originalName || '';
                valB = b.name || b.originalName || '';
            } else {
                valA = a[manager.sortField];
                valB = b[manager.sortField];
                if (valA === undefined || valA === null) valA = '';
                if (valB === undefined || valB === null) valB = '';
            }

            if (typeof valA === 'string') valA = valA.toLowerCase();
            if (typeof valB === 'string') valB = valB.toLowerCase();

            if (valA < valB) return manager.sortOrder === 'asc' ? -1 : 1;
            if (valA > valB) return manager.sortOrder === 'asc' ? 1 : -1;
            return 0;
        });

        return `
            <div class="mods-page config-page animate-fade">
                <div class="mods-toolbar config-toolbar">
                    <div class="toolbar-left">
                        <div class="search-box">
                            <i data-lucide="search"></i>
                            <input type="text" id="config-search" placeholder="${escapeAttr(searchPlaceholder)}" value="${escapeAttr(manager.lastSearchTerm)}">
                            <button type="button" class="search-clear" id="config-search-clear" title="${escapeAttr(window._t('clear_search'))}" aria-label="${escapeAttr(window._t('clear_search'))}">
                                <i data-lucide="x"></i>
                            </button>
                        </div>
                    </div>
                    <div class="tool-buttons mods-toolbar-actions">
                        <button type="button" class="btn-secondary btn-deploy-primary config-toolbar-deploy-spacer" tabindex="-1" disabled aria-hidden="true">
                            <i data-lucide="zap"></i>
                            <span>${escapeHtml(window._t?.('deploy_mods') ?? 'Deploy mods')}</span>
                        </button>
                        <div class="mods-actions-menu-wrap">
                            <button type="button" class="btn-secondary mods-actions-trigger" id="btn-config-actions" aria-haspopup="true" aria-expanded="false" title="${escapeAttr(window._t?.('actions') ?? 'Actions')}">
                                <i data-lucide="more-horizontal"></i>
                            </button>
                            <div class="mods-actions-dropdown" id="config-actions-dropdown" hidden>
                                <button type="button" class="mods-actions-item" id="config-action-backup">
                                    <i data-lucide="archive"></i>
                                    <span>${escapeHtml(window._t?.('backup_configs') ?? 'Backup Configs')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="config-action-transfer-to-other">
                                    <i data-lucide="arrow-right-left"></i>
                                    <span>${escapeHtml(window._t?.('transfer_configs_to_other') ?? 'Copy Configs To Other Platform')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="config-action-transfer-from-other">
                                    <i data-lucide="arrow-right-left"></i>
                                    <span>${escapeHtml(window._t?.('transfer_configs_from_other') ?? 'Import Configs From Other Platform')}</span>
                                </button>
                                <button type="button" class="mods-actions-item" id="config-action-badge-color">
                                    <i data-lucide="palette"></i>
                                    <span>${escapeHtml(window._t?.('edit_badge_color') ?? 'Edit Badge Color')}</span>
                                </button>
                                <button type="button" class="mods-actions-item danger" id="config-action-delete-all">
                                    <i data-lucide="trash-2"></i>
                                    <span>${escapeHtml(window._t?.('delete_all_configs') ?? 'Delete all Configs')}</span>
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
                <div class="mods-list-container">
                    <div class="mods-table-container">
                        ${filtered.length === 0 ? this.renderEmpty() : `
                            <table class="mods-table">
                                <colgroup>
                                    <col class="col-drag-handle">
                                    <col class="col-checkbox">
                                    <col class="col-mod-name">
                                    <col class="col-type">
                                    <col class="col-actions">
                                </colgroup>
                                <thead>
                                    <tr>
                                        <th style="width: 44px"></th>
                                        <th style="width: 40px"></th>
                                        <th class="sortable" data-sort="name">${window._t('name')} ${this.renderSortIcon(manager, 'name')}</th>
                                        <th class="sortable" data-sort="type">${window._t('type')} ${this.renderSortIcon(manager, 'type')}</th>
                                        <th class="actions-col">${window._t('actions')}</th>
                                    </tr>
                                </thead>
                                <tbody id="config-list-body">
                                    ${filtered.map(m => this.renderConfigRow(m)).join('')}
                                </tbody>
                            </table>
                        `}
                    </div>
                </div>
            </div>
        `;
    },

    renderConfigRow(mod) {
        const origPath = mod.originalName || '';
        const originalName = mod.originalName || '';
        const isCoreIni = /^CoreIni\//i.test(originalName);
        const displayName = (mod.name || origPath || '')
            .replace(/^Disabled\//, '')
            .replace(/^CoreIni\//i, '');
        const searchText = (displayName + ' ' + (mod.originalName || '')).toLowerCase();
        const safeOriginalNameAttr = escapeAttr(originalName);
        const safeDisplayName = escapeHtml(displayName);
        const safeDisplayTitle = escapeAttr(displayName);
        const safeFileName = escapeHtml(originalName);
        const safeOriginalTitle = escapeAttr(originalName);
        const safeType = escapeHtml((mod.type || '').toUpperCase());
        const safeTypeClass = escapeAttr(mod.type || 'unknown');
        const safeToggleArg = escapeJsSingleQuoted(originalName);
        return `
            <tr class="mod-row" data-name="${safeOriginalNameAttr}" data-search-text="${escapeAttr(searchText)}" draggable="false">
                <td class="drag-handle-cell">
                    <div class="drag-handle-wrapper" draggable="true" data-name="${safeOriginalNameAttr}">
                        <i data-lucide="grip-vertical"></i>
                    </div>
                </td>
                <td class="checkbox-cell" onclick="event.stopPropagation();">
                    <input type="checkbox" class="mod-select" checked disabled onclick="window.handleModToggle(this, '${safeToggleArg}'); event.stopPropagation();">
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
                <td class="actions-cell">
                    <div class="actions-cell-inner">
                        <button class="btn-icon btn-edit-mod" title="Edit" data-name="${safeOriginalNameAttr}" onclick="window.nuclearEdit(this); event.stopPropagation();" onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();">
                            <i data-lucide="edit-3"></i>
                        </button>
                        ${isCoreIni ? '' : `
                            <button class="btn-icon btn-delete-mod" title="Delete" data-name="${safeOriginalNameAttr}" onclick="window.nuclearDelete(this); event.stopPropagation();" onpointerdown="event.stopPropagation();" onmousedown="event.stopPropagation();" style="position: relative; z-index: 50; color: #ef4444;">
                                <i data-lucide="trash-2"></i>
                            </button>
                        `}
                    </div>
                </td>
            </tr>
        `;
    },

    renderEmpty() {
        return `
            <div class="empty-state-container polished" style="flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; background: rgba(255, 255, 255, 0.02); border: 1px dashed rgba(255, 255, 255, 0.1); border-radius: 16px; text-align: center; padding: 48px;">
                <div class="empty-state-icon" style="width: 80px; height: 80px; background: rgba(184, 197, 164, 0.1); border-radius: 50%; display: flex; align-items: center; justify-content: center; margin-bottom: 24px;">
                    <i data-lucide="file-text" style="width: 40px; height: 40px; color: var(--primary-green);"></i>
                </div>
                <h3>No config files found</h3>
                <p style="color: var(--text-muted); max-width: 420px; line-height: 1.6; margin-bottom: 0;">.ini, .json, and .txt files will show up here when they exist in your mod list.</p>
            </div>
        `;
    },

    renderSortIcon(manager, field) {
        if (manager.sortField !== field) return '<i data-lucide="chevrons-up-down" class="sort-icon-muted"></i>';
        return manager.sortOrder === 'asc' ? '<i data-lucide="chevron-up" class="sort-icon-active"></i>' : '<i data-lucide="chevron-down" class="sort-icon-active"></i>';
    }
};

