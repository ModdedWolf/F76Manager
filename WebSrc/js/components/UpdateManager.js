import { escapeHtml } from '../utils/htmlSafe.js';

function changelogToText(raw) {
    let text = String(raw || '');
    if (!text.trim()) return '';
    text = text.replace(/<\s*br\s*\/?>/gi, '\n');
    text = text.replace(/<\/\s*(p|div|h[1-6]|tr)\s*>/gi, '\n');
    text = text.replace(/<\s*li[^>]*>/gi, '• ');
    text = text.replace(/<\/\s*li\s*>/gi, '\n');
    text = text.replace(/<[^>]+>/g, '');
    text = text
        .replace(/&nbsp;/gi, ' ')
        .replace(/&amp;/gi, '&')
        .replace(/&lt;/gi, '<')
        .replace(/&gt;/gi, '>')
        .replace(/&quot;/gi, '"')
        .replace(/&#39;|&apos;/gi, "'");
    return text.replace(/[ \t]+\n/g, '\n').replace(/\n{3,}/g, '\n\n').trim();
}

function versionLabel(value) {
    const text = String(value || '').trim();
    if (!text) return '—';
    return text.startsWith('v') || text.startsWith('V') ? text : `v${text}`;
}

function changelogItems(raw) {
    return changelogToText(raw)
        .split('\n')
        .map(line => line.trim().replace(/^[-•*]\s*/, ''))
        .filter(Boolean);
}

export class UpdateManager {
    constructor() {
        this._changelogItems = [];
        this._changelogVersion = '';
        this._changelogEscHandler = null;
    }

    render(data) {
        const status = window.app?.appUpdate || null;
        const loggedIn = !!(status?.loggedIn || data?.managerSettings?.nexusLoggedIn);
        const current = versionLabel(status?.currentVersion || data?.appVersion);
        const latest = versionLabel(status?.latestVersion);
        const phase = status?.phase || (status ? 'idle' : 'checking');
        const percent = typeof status?.percent === 'number' ? status.percent : 0;

        let detail = '';
        let actions = '';

        if (!loggedIn) {
            detail = window._t('update_login_required');
            actions = `<button type="button" class="btn primary" id="btn-app-update-settings">${escapeHtml(window._t('update_go_settings'))}</button>`;
        } else if (!status || phase === 'checking') {
            detail = window._t('update_checking');
        } else if (phase === 'downloading' || phase === 'preparing') {
            detail = phase === 'preparing' ? window._t('update_preparing') : window._t('update_installing', percent);
            if (window.app?._appUpdateCancelRequested) {
                detail = window._t('update_cancelling');
            } else {
                actions = `<button type="button" class="btn" id="btn-app-update-cancel">${escapeHtml(window._t('update_cancel'))}</button>`;
            }
        } else if (phase === 'error') {
            detail = window._t('update_failed', status?.message || status?.error || '');
        } else if (phase === 'needs_browser' || (status?.updateAvailable && status?.premium === false)) {
            detail = window._t('update_available', latest);
            actions = `<button type="button" class="btn primary" id="btn-app-update-nexus">${escapeHtml(window._t('update_open_nexus'))}</button>`;
        } else if (status?.error && !status?.latestVersion) {
            detail = window._t('update_failed', status.error);
        } else if (status?.updateAvailable) {
            detail = window._t('update_available', latest);
            actions = `<button type="button" class="btn primary" id="btn-app-update-install">${escapeHtml(window._t('update_install'))}</button>`;
        } else if (status) {
            detail = window._t('update_up_to_date');
        } else {
            detail = window._t('update_checking');
        }

        const items = changelogItems(status?.changelog);
        this._changelogItems = items;
        this._changelogVersion = status?.latestVersion ? latest : current;

        const changelogBlock = items.length
            ? `<div class="update-changelog">
                    <h4>${escapeHtml(window._t('update_changelog'))}</h4>
                    <div class="update-changelog-box" id="update-changelog-box">
                        <button type="button" class="icon-btn-sm update-changelog-expand" id="btn-update-changelog-full"
                                title="${escapeHtml(window._t('update_changelog_show_full'))}"
                                aria-label="${escapeHtml(window._t('update_changelog_show_full'))}" hidden>
                            <i data-lucide="maximize-2"></i>
                        </button>
                        <ul>${items.map(item => `<li>${escapeHtml(item)}</li>`).join('')}</ul>
                    </div>
               </div>`
            : '';

        return `
            <div class="update-page">
                <div class="widget update-card">
                    <div class="widget-header">
                        <i data-lucide="download"></i>
                        <h3>${escapeHtml(window._t('update_title'))}</h3>
                    </div>
                    <div class="widget-content">
                        <p class="update-desc">${escapeHtml(window._t('update_desc'))}</p>
                        <div class="update-versions">
                            <div>
                                <span class="update-k">${escapeHtml(window._t('update_installed'))}</span>
                                <span class="update-v">${escapeHtml(current)}</span>
                            </div>
                            <div>
                                <span class="update-k">${escapeHtml(window._t('update_latest'))}</span>
                                <span class="update-v">${escapeHtml(status?.latestVersion ? latest : '—')}</span>
                            </div>
                        </div>
                        <p class="update-status-line">${escapeHtml(detail)}</p>
                        <div class="update-actions">${actions}</div>
                        ${changelogBlock}
                    </div>
                </div>
            </div>
        `;
    }

    updateValues(data) {
        const host = document.querySelector('.section-content');
        if (!host || !document.querySelector('.update-page')) return;
        host.innerHTML = this.render(data);
        this.onMount();
        if (window.lucide) lucide.createIcons();
    }

    onMount() {
        const post = (type) => window.chrome?.webview?.postMessage({ type });
        document.getElementById('btn-app-update-install')?.addEventListener('click', () => post('INSTALL_APP_UPDATE'));
        document.getElementById('btn-app-update-nexus')?.addEventListener('click', () => post('OPEN_APP_UPDATE_PAGE'));
        document.getElementById('btn-app-update-cancel')?.addEventListener('click', () => window.app?.cancelAppUpdate());
        document.getElementById('btn-app-update-settings')?.addEventListener('click', () => {
            window.app?.navigateTo('settings');
        });

        const box = document.getElementById('update-changelog-box');
        const moreBtn = document.getElementById('btn-update-changelog-full');
        if (box && moreBtn) {
            const clipped = box.scrollHeight > box.clientHeight + 2;
            box.classList.toggle('is-clipped', clipped);
            moreBtn.hidden = !clipped;
            if (clipped) {
                moreBtn.addEventListener('click', () => this.openChangelogModal());
            }
        }

        if (!window.app?.appUpdate) post('REPLAY_APP_UPDATE');
    }

    closeChangelogModal() {
        if (this._changelogEscHandler) {
            document.removeEventListener('keydown', this._changelogEscHandler);
            this._changelogEscHandler = null;
        }
        document.getElementById('update-changelog-modal')?.remove();
    }

    openChangelogModal() {
        this.closeChangelogModal();

        const items = this._changelogItems || [];
        if (!items.length) return;

        const title = window._t('update_changelog_full_title', this._changelogVersion || '—');
        const listHtml = items.map(item => `<li>${escapeHtml(item)}</li>`).join('');

        const overlay = document.createElement('div');
        overlay.id = 'update-changelog-modal';
        overlay.className = 'modal-overlay active update-changelog-modal';
        overlay.setAttribute('role', 'presentation');
        overlay.innerHTML = `
            <div class="endorsement-modal-content" role="dialog" aria-modal="true" aria-labelledby="update-changelog-modal-title">
                <div class="endorsement-modal-header">
                    <div class="endorsement-modal-title">
                        <i data-lucide="scroll-text" aria-hidden="true"></i>
                        <h3 id="update-changelog-modal-title">${escapeHtml(title)}</h3>
                    </div>
                    <button type="button" class="close-status" id="update-changelog-modal-x" aria-label="${escapeHtml(window._t('update_changelog_close'))}">&times;</button>
                </div>
                <div class="endorsement-modal-body">
                    <ul class="update-changelog-modal-list">${listHtml}</ul>
                </div>
                <div class="endorsement-modal-footer">
                    <button type="button" class="btn-popup secondary" id="update-changelog-modal-close">${escapeHtml(window._t('update_changelog_close'))}</button>
                </div>
            </div>
        `;

        document.body.appendChild(overlay);
        if (window.lucide) lucide.createIcons();

        const close = () => this.closeChangelogModal();
        document.getElementById('update-changelog-modal-x')?.addEventListener('click', close);
        document.getElementById('update-changelog-modal-close')?.addEventListener('click', close);
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) close();
        });

        this._changelogEscHandler = (e) => {
            if (e.key === 'Escape') {
                e.preventDefault();
                close();
            }
        };
        document.addEventListener('keydown', this._changelogEscHandler);
    }
}
