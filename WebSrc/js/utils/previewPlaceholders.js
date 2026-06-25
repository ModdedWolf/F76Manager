
export const PREVIEW_PLACEHOLDER_SECTIONS = new Set(['mods', 'config', 'bundle']);

export function isPreviewPlaceholderSection(sectionId) {
    return PREVIEW_PLACEHOLDER_SECTIONS.has(sectionId);
}

function t(key, fallback) {
    const v = typeof window._t === 'function' ? window._t(key) : key;
    return v && v !== key ? v : fallback;
}

function previewNotice() {
    return `<p class="preview-placeholder-notice" style="margin:0;padding:8px 16px;font-size:0.75rem;color:var(--text-muted);background:rgba(var(--primary-rgb),0.08);border-bottom:1px solid var(--border-color);text-align:center;">Theme preview — sample layout only (not connected to your mod list)</p>`;
}

function placeholderModRow({ name, file, type, version, enabled = true }) {
    const disabledClass = enabled ? '' : ' mod-disabled';
    return `
        <tr class="mod-row${disabledClass}" draggable="false">
            <td class="drag-handle-cell"><div class="drag-handle-wrapper"><i data-lucide="grip-vertical"></i></div></td>
            <td class="checkbox-cell"><input type="checkbox" class="mod-select" ${enabled ? 'checked' : ''} disabled></td>
            <td class="mod-name-cell">
                <div class="mod-name-wrapper">
                    <span class="mod-name">${name}</span>
                    <span class="mod-filename" style="opacity:0.5;font-size:0.85em">${file}</span>
                </div>
            </td>
            <td class="type-cell"><span class="badge badge-${type.toLowerCase()}">${type}</span></td>
            <td class="version-col">${version}</td>
            <td class="actions-cell">
                <div class="actions-cell-inner">
                    <button type="button" class="btn-icon" disabled><i data-lucide="edit-3"></i></button>
                    <button type="button" class="btn-icon btn-mod-state-off" disabled><i data-lucide="arrow-down"></i></button>
                    <button type="button" class="btn-icon btn-mod-state-off" disabled><i data-lucide="link-2"></i></button>
                    <button type="button" class="btn-icon" disabled><i data-lucide="trash-2"></i></button>
                </div>
            </td>
        </tr>`;
}

function renderModsPlaceholder() {
    return `
        <div class="mods-page animate-fade">
            ${previewNotice()}
            <div class="mods-toolbar">
                <div class="toolbar-left">
                    <a class="mods-nexus-link" href="#" onclick="return false;" tabindex="-1" aria-hidden="true">
                        <img src="assets/Nexus.png" alt="">
                    </a>
                    <div class="search-box">
                        <i data-lucide="search"></i>
                        <input type="text" placeholder="${t('search_mods_placeholder', 'Search mods…')}" disabled>
                    </div>
                    <div class="preset-controls">
                        <div class="preset-select-wrapper">
                            <select class="preset-select" disabled>
                                <option>${t('all_mods', 'All mods')}</option>
                            </select>
                        </div>
                    </div>
                </div>
                <div class="tool-buttons mods-toolbar-actions">
                    <button type="button" class="btn-secondary btn-deploy-primary" disabled>
                        <i data-lucide="zap"></i>
                        <span>${t('deploy_mods', 'Deploy mods')}</span>
                    </button>
                    <button type="button" class="btn-secondary" disabled><i data-lucide="more-horizontal"></i></button>
                </div>
            </div>
            <div class="mods-list-container">
                <div class="mods-table-container">
                    <table class="mods-table">
                        <colgroup>
                            <col class="col-drag-handle"><col class="col-checkbox"><col class="col-mod-name">
                            <col class="col-type"><col class="col-version"><col class="col-actions">
                        </colgroup>
                        <thead>
                            <tr>
                                <th style="width:44px"></th>
                                <th style="width:40px"><input type="checkbox" checked disabled></th>
                                <th>${t('name', 'Name')}</th>
                                <th>${t('type', 'Type')}</th>
                                <th>${t('version', 'Version')}</th>
                                <th class="actions-col">${t('actions', 'Actions')}</th>
                            </tr>
                        </thead>
                        <tbody>
                            ${placeholderModRow({ name: 'Example Texture Pack', file: 'ExampleMod.ba2', type: 'BA2', version: '1.2.0' })}
                            ${placeholderModRow({ name: 'Weapon Retexture', file: 'WeaponRetexture.esp', type: 'ESP', version: '2.1' })}
                            ${placeholderModRow({ name: 'Old World Mod', file: 'Disabled/OldMod.esm', type: 'ESM', version: '0.9', enabled: false })}
                        </tbody>
                    </table>
                </div>
            </div>
        </div>`;
}

function placeholderConfigRow({ name, file, type }) {
    return `
        <tr class="mod-row" draggable="false">
            <td class="drag-handle-cell"><div class="drag-handle-wrapper"><i data-lucide="grip-vertical"></i></div></td>
            <td class="checkbox-cell"><input type="checkbox" class="mod-select" checked disabled></td>
            <td class="mod-name-cell">
                <div class="mod-name-wrapper"><span class="mod-name">${name}</span></div>
            </td>
            <td class="type-cell"><span class="badge badge-${type.toLowerCase()}">${type}</span></td>
            <td class="actions-cell">
                <div class="actions-cell-inner">
                    <button type="button" class="btn-icon" disabled><i data-lucide="edit-3"></i></button>
                    <button type="button" class="btn-icon" disabled><i data-lucide="trash-2"></i></button>
                </div>
            </td>
        </tr>`;
}

function renderConfigPlaceholder() {
    return `
        <div class="mods-page config-page animate-fade">
            ${previewNotice()}
            <div class="mods-toolbar config-toolbar">
                <div class="toolbar-left">
                    <div class="search-box">
                        <i data-lucide="search"></i>
                        <input type="text" id="config-search" placeholder="${t('search_config_placeholder', 'Search configs…')}" disabled>
                    </div>
                </div>
                <div class="tool-buttons mods-toolbar-actions">
                    <button type="button" class="btn-secondary" disabled><i data-lucide="more-horizontal"></i></button>
                </div>
            </div>
            <div class="mods-list-container">
                <div class="mods-table-container">
                    <table class="mods-table">
                        <colgroup>
                            <col class="col-drag-handle"><col class="col-checkbox"><col class="col-mod-name">
                            <col class="col-type"><col class="col-actions">
                        </colgroup>
                        <thead>
                            <tr>
                                <th style="width:44px"></th>
                                <th style="width:40px"></th>
                                <th>${t('name', 'Name')}</th>
                                <th>${t('type', 'Type')}</th>
                                <th class="actions-col">${t('actions', 'Actions')}</th>
                            </tr>
                        </thead>
                        <tbody>
                            ${placeholderConfigRow({ name: 'Fallout76Custom.ini', file: 'Fallout76Custom.ini', type: 'INI' })}
                            ${placeholderConfigRow({ name: 'Fallout76Prefs.ini', file: 'Fallout76Prefs.ini', type: 'INI' })}
                        </tbody>
                    </table>
                </div>
            </div>
        </div>`;
}

function previewNoticeFooter() {
    return `<p class="preview-placeholder-notice" style="margin:12px 24px 0;font-size:0.7rem;color:var(--text-muted);opacity:0.65;text-align:center;">Sample layout for theme preview only</p>`;
}

function renderBundlePlaceholder() {
    return `
        <div class="bundle-page animate-fade" style="padding:24px;display:flex;flex-direction:column;gap:24px;height:100%;">
            <div class="mods-toolbar" style="background:var(--bg-surface);border:1px solid var(--border-color);padding:16px 24px;border-radius:12px;display:flex;justify-content:space-between;align-items:center;">
                <div class="toolbar-left" style="display:flex;align-items:center;gap:16px;">
                    <div class="header-with-icon" style="display:flex;align-items:center;gap:12px;">
                        <i data-lucide="archive" style="width:24px;height:24px;color:var(--primary-green);"></i>
                        <h2 style="margin:0;font-size:1.25rem;font-weight:700;">${t('mod_bundles', 'Mod Bundles')}</h2>
                        <span class="badge" style="background:rgba(255,255,255,0.1);">1</span>
                    </div>
                </div>
                <div class="tool-buttons">
                    <button type="button" class="btn-secondary" disabled>
                        <i data-lucide="plus"></i> ${t('create_new_bundle', 'Create New Bundle')}
                    </button>
                </div>
            </div>
            <div class="bundles-grid" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:16px;flex:1;min-height:0;align-content:start;">
                <div class="bundle-card widget" style="padding:20px;display:flex;flex-direction:column;gap:16px;position:relative;">
                    <div style="display:flex;justify-content:space-between;align-items:flex-start;">
                        <div style="display:flex;gap:12px;align-items:center;">
                            <div style="width:48px;height:48px;background:rgba(var(--primary-rgb),0.12);border-radius:8px;display:flex;align-items:center;justify-content:center;">
                                <i data-lucide="package" style="color:var(--primary-green);"></i>
                            </div>
                            <div>
                                <h3 style="margin:0;font-size:1.1rem;">Preview Texture Bundle</h3>
                                <div style="font-size:0.8rem;color:var(--text-muted);">PreviewBundle.ba2</div>
                            </div>
                        </div>
                        <button type="button" class="btn-icon small" disabled style="width:28px;height:28px;opacity:0.5;">
                            <i data-lucide="more-vertical" style="width:16px;"></i>
                        </button>
                    </div>
                    <div style="display:flex;gap:8px;margin-top:auto;padding-top:12px;border-top:1px solid rgba(255,255,255,0.05);font-size:0.8rem;color:var(--text-muted);">
                        <span>${t('in_mod_list', 'In mod list')}</span>
                        <span style="margin-left:auto;">${t('ba2_archive', 'BA2 archive')}</span>
                    </div>
                </div>
            </div>
            ${previewNoticeFooter()}
        </div>`;
}

export function renderPreviewPlaceholder(sectionId) {
    switch (sectionId) {
        case 'mods':
            return renderModsPlaceholder();
        case 'config':
            return renderConfigPlaceholder();
        case 'bundle':
            return renderBundlePlaceholder();
        default:
            return `<div class="empty-section" style="padding:48px;text-align:center;opacity:0.5;"><p>Preview placeholder unavailable.</p></div>`;
    }
}

export function mountPreviewPlaceholder(contentArea, sectionId) {
    if (!contentArea) return;
    contentArea.classList.add('content-refreshing');
    contentArea.innerHTML = `<div class="section-content">${renderPreviewPlaceholder(sectionId)}</div>`;
    if (window.lucide) window.lucide.createIcons();
    requestAnimationFrame(() => {
        requestAnimationFrame(() => contentArea.classList.remove('content-refreshing'));
    });
}
