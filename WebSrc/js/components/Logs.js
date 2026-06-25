export class Logs {
    constructor() {
        this.activeTab = 'activity';
        this.showDebugLogs = false;
    }

    render(data) {
        const logs = data ? (data.logs || { activity: [], errors: [] }) : { activity: [], errors: [] };
        const errorBadgeCount = typeof logs.errorCount === 'number' ? logs.errorCount : (logs.errors || []).length;
        const currentLogs = this.activeTab === 'activity' ? logs.activity : logs.errors;
        
        const filteredLogs = currentLogs;

        return `
            <div class="logs-page">
                <div class="logs-container">
                    <div class="logs-toolbar">
                        <div class="logs-tabs">
                            <button class="log-tab ${this.activeTab === 'activity' ? 'active' : ''}" data-tab="activity">
                                <i data-lucide="activity"></i>
                                <span>${window._t('activity_log')}</span>
                            </button>
                            <button class="log-tab ${this.activeTab === 'errors' ? 'active' : ''}" data-tab="errors">
                                <i data-lucide="alert-circle"></i>
                                <span>${window._t('error_log')}</span>
                                ${errorBadgeCount > 0 ? `<span class="error-badge">${errorBadgeCount}</span>` : ''}
                            </button>
                        </div>
                        
                        <div class="logs-actions">
                            <button class="btn secondary" id="clear-error-log" style="padding: 4px 10px; font-size: 13px; height: auto;">
                                <i data-lucide="trash-2" style="width: 14px; height: 14px; margin-right: 6px;"></i>
                                <span>${window._t('clear_error_log')}</span>
                            </button>
                            <button class="btn secondary" id="refresh-logs" style="padding: 4px 10px; font-size: 13px; height: auto;">
                                <i data-lucide="refresh-cw" style="width: 14px; height: 14px; margin-right: 6px;"></i>
                                <span>${window._t('refresh')}</span>
                            </button>
                        </div>
                    </div>

                    <div class="terminal-window">
                        <div class="terminal-header-static">
                            <span class="h-ts">TIME</span>
                            <span class="h-tag">TYPE</span>
                            <span class="h-content">LOG MESSAGE</span>
                        </div>
                        <div class="terminal-body" id="log-content">
                            ${filteredLogs.length > 0 ?
                filteredLogs.map(line => this.formatLogLine(line)).join('') :
                `<div class="empty-terminal">
                                    <span>${window._t('no_log_entries')}</span>
                                </div>`
            }
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

        return `
            <div class="log-line ${className}">
                <span class="log-ts">${timestamp}</span>
                ${tag ? `<span class="log-tag">${tag}</span>` : ''}
                <span class="log-content">${this.escapeHtml(content)}</span>
            </div>
        `;
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    updateValues() {
        if (window.app && typeof window.app.replaceCurrentSectionContent === 'function') {
            window.app.replaceCurrentSectionContent();
        }
    }

    onMount() {
        document.querySelectorAll('.log-tab').forEach(tab => {
            tab.addEventListener('click', () => {
                this.activeTab = tab.getAttribute('data-tab');

                if (window.app && typeof window.app.replaceCurrentSectionContent === 'function') {
                    window.app.replaceCurrentSectionContent();
                } else if (window.app && typeof window.app.refreshCurrentSection === 'function') {
                    window.app.refreshCurrentSection();
                }

                window.chrome.webview.postMessage({ type: 'GET_DATA' });
            });
        });

        const clearErrBtn = document.getElementById('clear-error-log');
        if (clearErrBtn) {
            clearErrBtn.addEventListener('click', () => {
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage({ type: 'CLEAR_ERROR_LOG' });
                }
            });
        }

        const refreshBtn = document.getElementById('refresh-logs');
        if (refreshBtn) {
            refreshBtn.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'GET_DATA' });
            });
        }

        const logContent = document.getElementById('log-content');
        if (logContent) {
            logContent.scrollTop = logContent.scrollHeight;
        }

        if (window.lucide) {
            window.lucide.createIcons();
        }
    }
}
