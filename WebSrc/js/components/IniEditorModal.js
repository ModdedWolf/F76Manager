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

        return `
            <div id="ini-editor-overlay" class="modal-overlay active">
                <div class="custom-modal polished ini-modal" style="width: 90vw; height: 85vh; max-width: 1200px; max-height: 800px; display: flex; flex-direction: column;">
                    <div class="modal-header" style="display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid var(--border-color);">
                        <div style="display: flex; align-items: center; gap: 10px;">
                            <i data-lucide="file-json" style="color: var(--primary-green);"></i>
                            <h3 style="margin: 0; color: var(--text-main);">${translator.t('ini_editor_title')}</h3>
                        </div>
                        <button class="close-status" id="ini-modal-close-top">&times;</button>
                    </div>
                    
                    <div class="logs-tabs" style="padding: 0; border-bottom: 1px solid var(--border-color); background: rgba(0,0,0,0.2);">
                        <button class="log-tab ${this.activeTab === 'custom' ? 'active' : ''}" id="tab-custom" style="flex: 1; border-radius: 0; justify-content: center; padding: 12px;">
                            ${translator.t('ini_tab_custom')}
                        </button>
                        <button class="log-tab ${this.activeTab === 'prefs' ? 'active' : ''}" id="tab-prefs" style="flex: 1; border-radius: 0; justify-content: center; padding: 12px;">
                            ${translator.t('ini_tab_prefs')}
                        </button>
                    </div>

                    <div class="modal-body" style="flex: 1; padding: 0; display: flex; flex-direction: column; overflow: hidden; position: relative; background: #0b0b0b;">
                        <div id="ini-highlight-backdrop" 
                            style="position: absolute; top: 0; left: 0; right: 0; bottom: 0; padding: 15px; font-family: 'JetBrains Mono', monospace; font-size: 13px; line-height: 1.5; color: #d4d4d4; white-space: pre-wrap; word-wrap: break-word; pointer-events: none; overflow-y: auto;"></div>
                        <textarea id="ini-editor-textarea" spellcheck="false" ${isLoading ? 'disabled' : ''} 
                            style="flex: 1; width: 100%; resize: none; border: none; background: transparent; color: transparent; caret-color: #fff; padding: 15px; font-family: 'JetBrains Mono', monospace; font-size: 13px; line-height: 1.5; outline: none; opacity: ${isLoading ? 0.5 : 1}; z-index: 1; overflow-y: auto;"></textarea>
                    </div>

                    <div class="modal-footer" style="display: flex; justify-content: flex-end; gap: 10px; border-top: 1px solid var(--border-color); background: var(--bg-card);">
                        <button class="btn-popup secondary" id="ini-modal-cancel">${translator.t('discard_changes')}</button>
                        <button class="btn-popup primary ${isLoading ? 'disabled' : ''}" id="ini-modal-save" ${isLoading ? 'disabled' : ''}>
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

        this.injectAndMount();
    }

    hide() {
        this.isOpen = false;
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

        let content = textarea.value;
        
        content = content.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

        const highlighted = content.replace(/^([^=\s\n][^=\n]*=)/gm, (match) => {
            const parts = match.split('=');
            if (parts.length > 1) {
                return `<span style="color: #6ed58a; font-weight: 500;">${parts[0]}</span>=`;
            }
            return match;
        });

        backdrop.innerHTML = highlighted + '\n';
    }

    switchTab(tab) {
        if (this.activeTab === tab) return;
        this.activeTab = tab;
        this.injectAndMount();
    }
}
