import { escapeHtml, escapeAttr } from '../utils/htmlSafe.js';

function formatBytes(n) {
    const num = Number(n) || 0;
    if (num < 1024) return `${num} B`;
    if (num < 1024 * 1024) return `${(num / 1024).toFixed(1)} KB`;
    return `${(num / (1024 * 1024)).toFixed(1)} MB`;
}

function statusBadge(status) {
    const s = (status || 'differs').toLowerCase();
    if (s === 'identical') {
        return `<span class="conflict-status-badge identical">${escapeHtml(window._t('conflict_status_identical') || 'Identical')}</span>`;
    }
    if (s === 'unknown') {
        return `<span class="conflict-status-badge unknown">${escapeHtml(window._t('conflict_status_unknown') || 'Unknown')}</span>`;
    }
    return `<span class="conflict-status-badge differs">${escapeHtml(window._t('conflict_status_differs') || 'Differs')}</span>`;
}

function fileNameFromPath(filePath) {
    const p = String(filePath || '').replace(/\\/g, '/');
    const parts = p.split('/').filter(Boolean);
    return parts.length ? parts[parts.length - 1] : p;
}

function renderConflictGroup(c) {
    const providers = Array.isArray(c.providers) && c.providers.length
        ? c.providers
        : (c.modNames || []).map(m => ({ modName: m }));
    const status = c.status || 'differs';
    const fullPath = c.filePath || '';
    const shortName = fileNameFromPath(fullPath);
    const modCount = providers.length;
    const countLabel = window._t('conflict_mod_count', modCount) || `${modCount} mods`;

    return `
        <div class="conflict-card" data-status="${escapeAttr(status)}">
            <div class="conflict-card-head">
                ${statusBadge(status)}
                <span class="conflict-card-name" title="${escapeAttr(shortName)}">${escapeHtml(shortName)}</span>
                <span class="conflict-card-count">${escapeHtml(countLabel)}</span>
            </div>
            <div class="conflict-card-path" title="${escapeAttr(fullPath)}">${escapeHtml(fullPath)}</div>
            <div class="conflict-card-providers">
                ${providers.map(p => `
                    <span class="conflict-chip">
                        <i data-lucide="package"></i>
                        <span>${escapeHtml(p.modName || p)}</span>
                        ${p.size != null ? `<em>${escapeHtml(formatBytes(p.size))}</em>` : ''}
                    </span>
                `).join('')}
            </div>
        </div>
    `;
}

export class ConflictModal {
    constructor(app) {
        this.app = app;
        this.isOpen = false;
        this.data = null;
        this._tab = 'differs';
    }

    _split() {
        const all = Array.isArray(this.data?.conflicts) ? this.data.conflicts : [];
        return {
            real: all.filter(c => (c.status || 'differs') !== 'identical'),
            identical: all.filter(c => c.status === 'identical')
        };
    }

    render() {
        if (!this.isOpen || !this.data) return '';

        const { real, identical } = this._split();
        const identicalCount = identical.length || this.data.identicalDuplicates || 0;
        const tab = this._tab;
        const differsLabel = window._t('conflict_tab_differs') || 'Differs';
        const harmlessLabel = window._t('conflict_tab_harmless') || 'Harmless';
        const infoLabel = window._t('conflict_info_label');

        return `
            <div id="conflict-popup-overlay" class="conflict-modal-overlay">
                <div class="conflict-modal-content polished conflict-modal-tabbed">
                    <div class="conflict-modal-header">
                        <div class="conflict-modal-title">
                            <i data-lucide="shield-alert" class="warning-icon"></i>
                            <div class="conflict-modal-titles">
                                <h3>${window._t('conflict_title')}</h3>
                                <span class="conflict-modal-subtitle">${window._t('conflict_desc')}</span>
                            </div>
                        </div>
                        <button class="close-status" id="conflict-cancel-top">&times;</button>
                    </div>

                    <div class="conflict-modal-body">
                        <div class="conflict-tabs" role="tablist">
                            <button type="button" class="conflict-tab ${tab === 'differs' ? 'active' : ''}" data-tab="differs" role="tab" aria-selected="${tab === 'differs'}">
                                <span class="conflict-tab-dot differs"></span>
                                <span>${escapeHtml(differsLabel)}</span>
                                <span class="conflict-tab-count">${real.length}</span>
                            </button>
                            <button type="button" class="conflict-tab ${tab === 'identical' ? 'active' : ''}" data-tab="identical" role="tab" aria-selected="${tab === 'identical'}">
                                <span class="conflict-tab-dot identical"></span>
                                <span>${escapeHtml(harmlessLabel)}</span>
                                <span class="conflict-tab-count">${identicalCount}</span>
                            </button>
                            <div class="conflict-info-wrap">
                                <button type="button" class="conflict-info-btn" id="conflict-info-btn" aria-expanded="false" aria-controls="conflict-info-popover" aria-label="${escapeAttr(infoLabel)}" title="${escapeAttr(infoLabel)}">
                                    <i data-lucide="info"></i>
                                </button>
                                <div class="conflict-info-popover" id="conflict-info-popover" role="tooltip" hidden>
                                    <div class="conflict-info-row">
                                        <span class="conflict-status-badge differs">${escapeHtml(differsLabel)}</span>
                                        <p>${escapeHtml(window._t('conflict_info_differs'))}</p>
                                    </div>
                                    <div class="conflict-info-row">
                                        <span class="conflict-status-badge identical">${escapeHtml(harmlessLabel)}</span>
                                        <p>${escapeHtml(window._t('conflict_info_harmless'))}</p>
                                    </div>
                                </div>
                            </div>
                        </div>

                        <div class="conflict-tab-panel scroll-styled" data-panel="differs" ${tab === 'differs' ? '' : 'hidden'}>
                            ${real.length
                                ? real.map(c => renderConflictGroup(c)).join('')
                                : `<p class="conflict-empty">${escapeHtml(window._t('conflict_no_real') || 'No differing file contents found.')}</p>`}
                        </div>
                        <div class="conflict-tab-panel scroll-styled" data-panel="identical" ${tab === 'identical' ? '' : 'hidden'}>
                            <p class="conflict-panel-hint">${escapeHtml(window._t('conflict_harmless_hint') || 'These files are byte-identical across mods, so load order does not matter.')}</p>
                            ${identical.length
                                ? identical.map(c => renderConflictGroup(c)).join('')
                                : `<p class="conflict-empty">${escapeHtml(window._t('conflict_no_harmless') || 'No identical duplicates.')}</p>`}
                        </div>
                    </div>

                    <div class="conflict-modal-footer">
                        <label class="conflict-footer-override" title="${escapeAttr(window._t('conflict_auto_override_desc') || '')}">
                            <input type="checkbox" id="conflict-auto-override">
                            <span>${window._t('conflict_auto_override')}</span>
                        </label>
                        <div class="conflict-footer-spacer"></div>
                        <button class="btn-popup secondary" id="conflict-cancel">${window._t('conflict_cancel')}</button>
                        <button class="btn-popup primary" id="conflict-confirm">
                            <i data-lucide="zap"></i> ${window._t('conflict_force')}
                        </button>
                    </div>
                </div>
            </div>
        `;
    }

    show(data) {
        console.log('[ConflictModal] show() called. requestedMods:', data.requestedMods, 'count:', data.requestedMods?.length);
        this.data = data;
        this.isOpen = true;
        const { real, identical } = this._split();
        this._tab = real.length || !identical.length ? 'differs' : 'identical';
        this.injectAndMount();
    }

    hide() {
        this.isOpen = false;
        const overlay = document.getElementById('conflict-popup-overlay');
        if (overlay) {
            overlay.style.opacity = '0';
            overlay.style.transition = 'opacity 0.2s ease-out';
            setTimeout(() => overlay.remove(), 200);
        }
    }

    injectAndMount() {
        const existing = document.getElementById('conflict-popup-overlay');
        if (existing) existing.remove();

        const container = document.createElement('div');
        container.innerHTML = this.render();
        const overlayElement = container.firstElementChild;
        document.body.appendChild(overlayElement);

        if (window.lucide) lucide.createIcons();

        const confirmBtn = document.getElementById('conflict-confirm');
        const cancelBtn = document.getElementById('conflict-cancel');
        const closeTopBtn = document.getElementById('conflict-cancel-top');
        const autoOverrideCheckbox = document.getElementById('conflict-auto-override');
        const tabs = overlayElement.querySelectorAll('.conflict-tab');
        const panels = overlayElement.querySelectorAll('.conflict-tab-panel');
        tabs.forEach(btn => {
            btn.onclick = () => {
                this._tab = btn.dataset.tab;
                tabs.forEach(t => {
                    const on = t === btn;
                    t.classList.toggle('active', on);
                    t.setAttribute('aria-selected', String(on));
                });
                panels.forEach(p => {
                    p.hidden = p.dataset.panel !== this._tab;
                    if (!p.hidden) p.scrollTop = 0;
                });
            };
        });

        const infoBtn = document.getElementById('conflict-info-btn');
        const infoPopover = document.getElementById('conflict-info-popover');
        const setInfoOpen = (open) => {
            if (!infoBtn || !infoPopover) return;
            infoPopover.hidden = !open;
            infoBtn.setAttribute('aria-expanded', String(open));
            infoBtn.classList.toggle('active', open);
        };
        if (infoBtn) {
            infoBtn.onclick = (e) => {
                e.stopPropagation();
                setInfoOpen(infoPopover.hidden);
            };
        }
        overlayElement.addEventListener('click', (e) => {
            if (infoPopover && !infoPopover.hidden && !e.target.closest('.conflict-info-wrap')) setInfoOpen(false);
        });
        overlayElement.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && infoPopover && !infoPopover.hidden) {
                e.stopPropagation();
                setInfoOpen(false);
                infoBtn?.focus();
            }
        });

        if (confirmBtn) {
            confirmBtn.onclick = () => {
                const autoOverride = autoOverrideCheckbox ? autoOverrideCheckbox.checked : false;

                if (autoOverride) {
                    const newSettings = Object.assign({}, this.app.realData.managerSettings, { autoForceDeploy: true });
                    window.chrome.webview.postMessage({
                        type: 'SAVE_MANAGER_SETTINGS',
                        settings: newSettings
                    });
                }

                const modsToSend = this.data.requestedMods;
                if (!modsToSend || modsToSend.length === 0) {
                    this.hide();
                    window.chrome.webview.postMessage({ type: 'DEPLOY_ALL', force: true, mods: [], presetScoped: true });
                    return;
                }
                console.log('[ConflictModal] Force Deploy clicked. Sending DEPLOY_MODS with', modsToSend.length, 'mods:', modsToSend);
                this.hide();
                window.chrome.webview.postMessage({
                    type: 'DEPLOY_MODS',
                    mods: modsToSend,
                    force: true
                });
            };
        }

        const closeAction = () => this.hide();
        if (cancelBtn) cancelBtn.onclick = closeAction;
        if (closeTopBtn) closeTopBtn.onclick = closeAction;

        overlayElement.onclick = (e) => {
            if (e.target === overlayElement) closeAction();
        };
    }
}
