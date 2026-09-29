import { translator } from '../Translations.js';

export class IniEditorModal {
    constructor(app) {
        this.app = app;
        this.isOpen = false;
        this.activeTab = 'custom';
        this.contents = {
            custom: '',
            prefs: ''
        };
        this.loading = {
            custom: true,
            prefs: true
        };
    }
    render() {
        if (!this.isOpen) return '';

        const isLoading = this.loading[this.activeTab];
        const activeContent = isLoading ? 'Loading...' : this.contents[this.activeTab];

        const customActive = this.activeTab === 'custom';

        return `
            <div id="ini-editor-overlay" class="modal-overlay active">
                <div class="custom-modal ini-modal" role="dialog" aria-label="${translator.t('ini_editor_title')}">
                    <div class="ini-editor-toprow">
                        <div class="ini-editor-title">
                            <i data-lucide="file-json"></i>
                            <span>${translator.t('ini_editor_title')}</span>
                        </div>
                        <div class="config-view-toggle" role="tablist">
                            <button type="button" class="config-view-btn ${customActive ? 'active' : ''}" id="tab-custom" role="tab" aria-selected="${customActive}">${translator.t('ini_tab_custom')}</button>
                            <button type="button" class="config-view-btn ${customActive ? '' : 'active'}" id="tab-prefs" role="tab" aria-selected="${!customActive}">${translator.t('ini_tab_prefs')}</button>
                        </div>
                        <div class="ini-editor-spacer"></div>
                        <button type="button" class="close-status" id="ini-modal-close-top" aria-label="${translator.t('cancel')}">&times;</button>
                    </div>

                    <div class="ini-editor-wrap ${isLoading ? 'is-loading' : ''}">
                        <div id="ini-highlight-backdrop"></div>
                        <textarea id="ini-editor-textarea" spellcheck="false" ${isLoading ? 'disabled' : ''}></textarea>
                    </div>

                    <div class="ini-editor-footer">
                        <button type="button" class="btn-popup secondary" id="ini-modal-cancel">${translator.t('discard_changes')}</button>
                        <button type="button" class="btn-popup primary ${isLoading ? 'disabled' : ''}" id="ini-modal-save" ${isLoading ? 'disabled' : ''}>
                            <i data-lucide="save"></i> ${translator.t('tweak_save_btn')}
                        </button>
                    </div>
                </div>
            </div>
        `;
    }

    show() {
        this.isOpen = true;
        this.loading.custom = true;
        this.loading.prefs = true;
        this.contents.custom = '';
        this.contents.prefs = '';
        
        window.chrome.webview.postMessage({ type: 'GET_INI_CONTENT', iniType: 'custom' });
        window.chrome.webview.postMessage({ type: 'GET_INI_CONTENT', iniType: 'prefs' });

        if (!this.keyHandler) {
            this.keyHandler = (e) => {
                if (!this.isOpen) return;
                if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && String(e.key).toLowerCase() === 's') {
                    e.preventDefault();
                    const saveBtn = document.getElementById('ini-modal-save');
                    if (saveBtn && !saveBtn.disabled) saveBtn.click();
                }
            };
            document.addEventListener('keydown', this.keyHandler);
        }

        this.injectAndMount();
    }

    hide() {
        this.isOpen = false;
        if (this.keyHandler) {
            document.removeEventListener('keydown', this.keyHandler);
            this.keyHandler = null;
        }
        const overlay = document.getElementById('ini-editor-overlay');
        if (overlay) {
            overlay.remove();
        }
    }

    updateContent(type, content) {
        this.contents[type] = content;
        this.loading[type] = false;
        
        if (this.activeTab === type && this.isOpen) {
            this.injectAndMount();
        }
    }

    injectAndMount() {
        const existing = document.getElementById('ini-editor-overlay');
        if (existing) existing.remove();

        const container = document.createElement('div');
        container.innerHTML = this.render();
        document.body.appendChild(container.firstElementChild);

        if (window.lucide) lucide.createIcons();

        this.bindEvents();
    }

    bindEvents() {
        const overlay = document.getElementById('ini-editor-overlay');
        if (!overlay) return;

        const closeTop = document.getElementById('ini-modal-close-top');
        const cancelBtn = document.getElementById('ini-modal-cancel');
        const saveBtn = document.getElementById('ini-modal-save');
        const tabCustom = document.getElementById('tab-custom');
        const tabPrefs = document.getElementById('tab-prefs');
        const textarea = document.getElementById('ini-editor-textarea');

        const closeAction = () => this.hide();

        if (closeTop) closeTop.onclick = closeAction;
        if (cancelBtn) cancelBtn.onclick = closeAction;

        if (saveBtn) {
            saveBtn.onclick = () => {
                const currentContent = textarea.value;
                if (currentContent === 'Loading...' || this.loading[this.activeTab]) return;

                this.contents[this.activeTab] = currentContent;
                
                window.chrome.webview.postMessage({ 
                    type: 'SAVE_INI_CONTENT', 
                    iniType: this.activeTab,
                    content: currentContent 
                });
            };
        }

        if (tabCustom) {
            tabCustom.onclick = () => {
                this.switchTab('custom');
            };
        }

        if (tabPrefs) {
            tabPrefs.onclick = () => {
                this.switchTab('prefs');
            };
        }
        
        if (textarea) {
            textarea.value = this.contents[this.activeTab] || (this.loading[this.activeTab] ? 'Loading...' : '');
            this.updateHighlighting();

            textarea.oninput = (e) => {
                this.contents[this.activeTab] = e.target.value;
                this.updateHighlighting();
            };

            textarea.onscroll = () => {
                const backdrop = document.getElementById('ini-highlight-backdrop');
                if (backdrop) backdrop.scrollTop = textarea.scrollTop;
            };
        }
    }

    updateHighlighting() {
        const backdrop = document.getElementById('ini-highlight-backdrop');
        const textarea = document.getElementById('ini-editor-textarea');
        if (!backdrop || !textarea) return;

        const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

        const highlighted = textarea.value.split('\n').map((line) => {
            const trimmed = line.trimStart();
            if (trimmed.startsWith(';') || trimmed.startsWith('#')) {
                return `<span class="cfg-tok-comment">${esc(line)}</span>`;
            }
            const section = line.match(/^(\s*)(\[[^\]]*\])(.*)$/);
            if (section) {
                return `${esc(section[1])}<span class="ini-tok-section">${esc(section[2])}</span>${esc(section[3])}`;
            }
            const eq = line.indexOf('=');
            if (eq > 0) {
                const key = line.slice(0, eq);
                const value = line.slice(eq + 1);
                const valueCls = /^\s*-?\d+(\.\d+)?\s*$/.test(value) ? 'cfg-tok-num' : 'cfg-tok-str';
                return `<span class="cfg-tok-key">${esc(key)}</span><span class="cfg-tok-punct">=</span><span class="${valueCls}">${esc(value)}</span>`;
            }
            return esc(line);
        }).join('\n');

        backdrop.innerHTML = highlighted + '\n';
    }

    switchTab(tab) {
        if (this.activeTab === tab) return;
        this.activeTab = tab;
        this.injectAndMount();
    }
}
