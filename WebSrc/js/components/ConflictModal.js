export class ConflictModal {
    constructor(app) {
        this.app = app;
        this.isOpen = false;
        this.data = null;
    }

    render() {
        if (!this.isOpen || !this.data) return '';

        return `
            <div id="conflict-popup-overlay" class="conflict-modal-overlay">
                <div class="conflict-modal-content polished">
                    <div class="conflict-modal-header">
                        <div class="conflict-modal-title">
                            <i data-lucide="shield-alert" class="warning-icon"></i>
                            <h3>${window._t('conflict_title')}</h3>
                        </div>
                        <button class="close-status" id="conflict-cancel-top">&times;</button>
                    </div>
                    
                    <div class="conflict-modal-body">
                        <p class="conflict-modal-desc">
                            ${window._t('conflict_desc')}
                        </p>
                        
                        <div class="conflict-list-scrollable scroll-styled">
                            ${this.data.conflicts.map(c => `
                                <div class="conflict-group-node">
                                    <div class="file-path-row">
                                        <i data-lucide="file-code"></i>
                                        <code>${c.filePath}</code>
                                    </div>
                                    <div class="conflict-mod-stack">
                                        ${c.modNames.map(m => `
                                            <div class="conflict-mod-row">
                                                <div class="mod-info-inline">
                                                    <i data-lucide="package"></i>
                                                    <span>${m}</span>
                                                </div>
                                            </div>
                                        `).join('')}
                                    </div>
                                </div>
                            `).join('')}
                        </div>

                        <div class="conflict-override-option">
                            <label class="checkbox-container">
                                <input type="checkbox" id="conflict-auto-override">
                                <span class="checkmark"></span>
                                <div class="checkbox-label">
                                    <strong>${window._t('conflict_auto_override')}</strong>
                                    <span>${window._t('conflict_auto_override_desc')}</span>
                                </div>
                            </label>
                        </div>
                    </div>
                    <div class="conflict-modal-footer">
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

                let modsToSend = this.data.requestedMods;
                if (!modsToSend || modsToSend.length === 0) {
                    modsToSend = (this.app.realData?.mods || [])
                        .filter(m => m.status === 'enabled')
                        .map(m => m.originalName);
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
