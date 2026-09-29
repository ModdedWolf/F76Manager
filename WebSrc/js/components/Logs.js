export class Logs {
    static _delegationBound = false;

    constructor(options = {}) {
        this.activeTab = 'activity';
        this.showDebugLogs = false;
        this.isPopout = !!options.isPopout;
        this._actionsMenuAbort = null;

        if (!Logs._delegationBound) {
            Logs._delegationBound = true;
            document.addEventListener('click', (event) => {
                const tab = event.target.closest('.logs-page .log-tab');
                if (!tab) return;
                const page = tab.closest('.logs-page');
                const logs = page?._logsInstance || window.app?.sections?.logs;
                if (!logs) return;
                const tabName = tab.getAttribute('data-tab');
                if (tabName) logs.switchTab(tabName);
            });
        }
    }

    render(data) {
        const logs = data ? (data.logs || { activity: [], errors: [] }) : { activity: [], errors: [] };
        const errorBadgeCount = typeof logs.errorCount === 'number' ? logs.errorCount : (logs.errors || []).length;
        const currentLogs = this.activeTab === 'activity' ? logs.activity : logs.errors;
        const shareLabel = this.activeTab === 'errors'
            ? (window._t('logs_share_errors') || 'Share Error Log')
            : (window._t('logs_share_activity') || 'Share Activity Log');

        return `
            <div class="logs-page${this.isPopout ? ' logs-page--popout' : ''}">
                <div class="logs-container">
                    <div class="logs-toolbar">
                        <div class="logs-tabs">
                            <button type="button" class="log-tab ${this.activeTab === 'activity' ? 'active' : ''}" data-tab="activity">
                                <i data-lucide="activity"></i>
                                <span>${window._t('activity_log')}</span>
                            </button>
                            <button type="button" class="log-tab ${this.activeTab === 'errors' ? 'active' : ''}" data-tab="errors">
                                <i data-lucide="alert-circle"></i>
                                <span>${window._t('error_log')}</span>
                                ${errorBadgeCount > 0 ? `<span class="error-badge">${errorBadgeCount}</span>` : ''}
                            </button>
                        </div>

                        <div class="logs-actions">
                            <button type="button" class="btn secondary logs-refresh-btn" id="refresh-logs" title="${this.escapeHtml(window._t('refresh') || 'Refresh')}">
                                <i data-lucide="refresh-cw"></i>
                            </button>
                            <div class="logs-actions-wrap">
                                <button type="button" class="btn-secondary mods-actions-trigger" id="btn-logs-actions" aria-haspopup="true" aria-expanded="false" title="${this.escapeHtml(window._t('actions') || 'Actions')}">
                                    <i data-lucide="more-horizontal"></i>
                                </button>
                                <div class="mods-actions-dropdown" id="logs-actions-dropdown" hidden>
                                    <button type="button" class="mods-actions-item" id="btn-logs-share">
                                        <i data-lucide="share-2"></i>
                                        <span id="btn-logs-share-label">${this.escapeHtml(shareLabel)}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item" id="btn-logs-open-folder">
                                        <i data-lucide="folder-open"></i>
                                        <span>${this.escapeHtml(window._t('logs_open_folder') || 'Open Logs Folder')}</span>
                                    </button>
                                    ${this.isPopout ? '' : `
                                    <button type="button" class="mods-actions-item" id="btn-logs-popout">
                                        <i data-lucide="panel-top-open"></i>
                                        <span>${this.escapeHtml(window._t('logs_popout') || 'Pop Out Window')}</span>
                                    </button>`}
                                    <div class="mods-actions-divider" role="separator"></div>
                                    <button type="button" class="mods-actions-item danger" id="btn-logs-clear-errors">
                                        <i data-lucide="trash-2"></i>
                                        <span>${this.escapeHtml(window._t('clear_error_log') || 'Clear Errors')}</span>
                                    </button>
                                    <button type="button" class="mods-actions-item danger" id="btn-logs-clear-activity">
                                        <i data-lucide="eraser"></i>
                                        <span>${this.escapeHtml(window._t('logs_clear_activity') || 'Clear Activity Log')}</span>
                                    </button>
                                </div>
                            </div>
                        </div>
                    </div>

                    <div class="terminal-window">
                        <div class="terminal-header-static">
                            <span class="h-ts">TIME</span>
                            <span class="h-tag">TYPE</span>
                            <span class="h-content">LOG MESSAGE</span>
                        </div>
                        <div class="terminal-body" id="log-content">
                            ${currentLogs.length > 0
                                ? currentLogs.map(line => this.formatLogLine(line)).join('')
                                : `<div class="empty-terminal"><span>${window._t('no_log_entries')}</span></div>`}
                        </div>
                    </div>
                </div>
            </div>
        `;
    }

    formatLogLine(line) {
        const trimmed = (line || '').trim();

        if (!trimmed) return '';
        if (/^Actual value was/i.test(trimmed)) return '';

        let className = '';
        if (trimmed.includes('[ERROR]') || trimmed.includes('ERROR:')) className = 'log-error';
        else if (trimmed.includes('[WARN]') || trimmed.includes('[WARNING]')) className = 'log-warn';
        else if (trimmed.includes('[SUCCESS]')) className = 'log-success';
        else if (trimmed.includes('[DEBUG]')) className = 'log-debug';
        else if (trimmed.includes('[MODS]') || trimmed.includes('[IMPORT]') || trimmed.includes('[NEXUS]') || trimmed.includes('[DEPLOY]') || trimmed.includes('[ORDER]')) className = 'log-info';
        else if (trimmed.includes('[DELETE]')) className = 'log-warn';
        else if (trimmed.includes('[INI]')) className = 'log-info';

        const tsMatch = trimmed.match(/^\[(.*?)\]/);
        let content = trimmed;
        let timestamp = '';

        if (tsMatch) {
            timestamp = tsMatch[0];
            content = line.substring(timestamp.length).trim();
        }

        const tagMatch = content.match(/^\[(.*?)\]/);
        let tag = '';
        if (tagMatch) {
            tag = tagMatch[0];
            content = content.substring(tag.length).trim();
        }

        content = content
            .replace(/KeyMatch=\w+/, '')
            .replace(/\(Order: \d+\)/, '')
            .replace(/result: NONE/i, 'No match found')
            .replace(/DataPath:.*$/, '(Game Folder)')
            .replace(/Lookup Mod:.*$/, '')
            .trim();

        if (!content && !tag) return '';

        const tagTitle = tag ? ` title="${this.escapeHtml(tag)}"` : '';
        return `
            <div class="log-line ${className}">
                <span class="log-ts">${this.escapeHtml(timestamp)}</span>
                <span class="log-tag"${tagTitle}>${this.escapeHtml(tag)}</span>
                <span class="log-content">${this.escapeHtml(content)}</span>
            </div>
        `;
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text ?? '';
        return div.innerHTML;
    }

    getLogsPayload(data) {
        return data ? (data.logs || { activity: [], errors: [] }) : { activity: [], errors: [] };
    }

    getCurrentLogLines(data) {
        const logs = this.getLogsPayload(data);
        return this.activeTab === 'activity' ? (logs.activity || []) : (logs.errors || []);
    }

    renderLogContentHtml(lines) {
        if (!lines.length) {
            return `<div class="empty-terminal"><span>${window._t('no_log_entries')}</span></div>`;
        }
        return lines.map(line => this.formatLogLine(line)).join('');
    }

    scrollLogContentToEnd(logContent) {
        if (!logContent) return;
        logContent.scrollTop = logContent.scrollHeight;
    }

    updateShareLabel() {
        const label = document.getElementById('btn-logs-share-label');
        if (!label) return;
        label.textContent = this.activeTab === 'errors'
            ? (window._t('logs_share_errors') || 'Share Error Log')
            : (window._t('logs_share_activity') || 'Share Activity Log');
    }

    refreshView(data = window.app?.realData, scrollToEnd = false) {
        const root = this.isPopout
            ? document.querySelector('.logs-page')
            : document.querySelector('.logs-page:not(.logs-page--popout)') || document.querySelector('.logs-page');
        if (!root) return false;

        const logs = this.getLogsPayload(data);
        const errorBadgeCount = typeof logs.errorCount === 'number' ? logs.errorCount : (logs.errors || []).length;

        root.querySelectorAll('.log-tab').forEach(tab => {
            tab.classList.toggle('active', tab.getAttribute('data-tab') === this.activeTab);
        });

        const errorsTab = root.querySelector('.log-tab[data-tab="errors"]');
        if (errorsTab) {
            const existingBadge = errorsTab.querySelector('.error-badge');
            if (errorBadgeCount > 0) {
                if (existingBadge) {
                    existingBadge.textContent = String(errorBadgeCount);
                } else {
                    errorsTab.insertAdjacentHTML('beforeend', `<span class="error-badge">${errorBadgeCount}</span>`);
                }
            } else if (existingBadge) {
                existingBadge.remove();
            }
        }

        this.updateShareLabel();

        const logContent = root.querySelector('#log-content');
        if (logContent) {
            logContent.innerHTML = this.renderLogContentHtml(this.getCurrentLogLines(data));
            if (scrollToEnd) this.scrollLogContentToEnd(logContent);
        }

        return true;
    }

    updateValues(data) {
        if (this.isPopout) {
            this.refreshView(data);
            return;
        }
        if (window.app?.currentSection === 'logs' && this.refreshView(data)) {
            return;
        }
        if (window.app && typeof window.app.replaceCurrentSectionContent === 'function') {
            window.app.replaceCurrentSectionContent();
        }
    }

    switchTab(tabName) {
        if (!tabName || tabName === this.activeTab) return;
        this.activeTab = tabName;
        this.refreshView(this.isPopout ? window.__logsPopoutData : window.app?.realData, true);
    }

    _closeActionsMenu() {
        const dropdown = document.getElementById('logs-actions-dropdown');
        const trigger = document.getElementById('btn-logs-actions');
        if (dropdown) dropdown.hidden = true;
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
    }

    _mountActionsMenu(root) {
        const trigger = root.querySelector('#btn-logs-actions');
        const dropdown = root.querySelector('#logs-actions-dropdown');
        if (!trigger || !dropdown) return;

        if (this._actionsMenuAbort) this._actionsMenuAbort.abort();
        this._actionsMenuAbort = new AbortController();
        const { signal } = this._actionsMenuAbort;

        dropdown.hidden = true;
        trigger.setAttribute('aria-expanded', 'false');

        trigger.addEventListener('click', (e) => {
            e.stopPropagation();
            const open = dropdown.hidden;
            dropdown.hidden = !open;
            trigger.setAttribute('aria-expanded', open ? 'true' : 'false');
            if (open && window.lucide) window.lucide.createIcons();
        }, { signal });

        document.addEventListener('click', (e) => {
            if (!root.isConnected) return;
            if (!e.target.closest('.logs-actions-wrap')) this._closeActionsMenu();
        }, { signal });

        root.querySelector('#btn-logs-share')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeActionsMenu();
            window.chrome?.webview?.postMessage({ type: 'SHARE_LOGS', tab: this.activeTab });
        }, { signal });

        root.querySelector('#btn-logs-open-folder')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeActionsMenu();
            window.chrome?.webview?.postMessage({ type: 'OPEN_LOGS_FOLDER' });
        }, { signal });

        root.querySelector('#btn-logs-popout')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeActionsMenu();
            window.chrome?.webview?.postMessage({ type: 'OPEN_LOGS_POPOUT' });
        }, { signal });

        root.querySelector('#btn-logs-clear-errors')?.addEventListener('click', (e) => {
            e.stopPropagation();
            this._closeActionsMenu();
            window.chrome?.webview?.postMessage({ type: 'CLEAR_ERROR_LOG' });
        }, { signal });

        root.querySelector('#btn-logs-clear-activity')?.addEventListener('click', async (e) => {
            e.stopPropagation();
            this._closeActionsMenu();
            const title = window._t('logs_clear_activity') || 'Clear Activity Log';
            const message = window._t('logs_clear_activity_confirm') || 'Clear the activity log? This cannot be undone.';
            const confirmFn = window.appConfirm;
            let ok = true;
            if (typeof confirmFn === 'function') {
                ok = await confirmFn({
                    title,
                    message,
                    okText: title,
                    cancelText: window._t('cancel') || 'Cancel',
                    danger: true
                });
            } else {
                ok = window.confirm(message);
            }
            if (!ok) return;
            window.chrome?.webview?.postMessage({ type: 'CLEAR_ACTIVITY_LOG' });
        }, { signal });
    }

    onMount() {
        const root = document.querySelector(this.isPopout ? '.logs-page' : '.logs-page:not(.logs-page--popout)')
            || document.querySelector('.logs-page');
        if (!root) return;

        root._logsInstance = this;

        const refreshBtn = root.querySelector('#refresh-logs');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', () => {
                if (this.isPopout) {
                    window.chrome?.webview?.postMessage({ type: 'GET_LOGS' });
                } else {
                    window.chrome?.webview?.postMessage({ type: 'GET_LOGS' });
                }
            });
        }

        this._mountActionsMenu(root);
        this.updateShareLabel();

        const logContent = root.querySelector('#log-content');
        if (logContent) this.scrollLogContentToEnd(logContent);

        if (window.lucide) window.lucide.createIcons();
    }
}
