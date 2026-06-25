import { escapeAttr, escapeHtml, sanitizeCssUrl } from '../utils/htmlSafe.js';

export class NexusRenderer {
    constructor() {
        this.searchResults = [];
        this.isSearching = false;
        this.currentQuery = "";
        this._pendingDownloadMeta = {};
    }

    render(data) {
        const ms = data && data.managerSettings ? data.managerSettings : {};
        const loggedIn = !!ms.nexusLoggedIn;

        if (!loggedIn) {
            const t = (k, fb) => window._t(k, fb);
            return `
                <div class="nexus-page animate-fade" style="display: flex; flex-direction: column; align-items: center; justify-content: center; height: 100%; text-align: center; padding: 40px;">
                    <i data-lucide="log-in" style="width: 64px; height: 64px; color: #555; margin-bottom: 20px;"></i>
                    <h2 style="margin-bottom: 10px;">${escapeHtml(t('nexus_login_required_title', 'Sign in with Nexus Mods'))}</h2>
                    <button class="btn primary" id="nexus-login-hero-btn" style="background: #da8e35; border-color: #da8e35; color: #fff; padding: 12px 32px; margin-top: 8px;">
                        <i data-lucide="log-in"></i>
                        <span>${escapeHtml(t('nexus_login', 'Sign in with Nexus Mods'))}</span>
                    </button>
                    <p style="font-size: 0.8em; margin-top: 15px; color: #666; max-width: 420px;">
                        ${escapeHtml(t('nexus_login_sso_hint', 'Opens your browser to authorize this application with your Nexus account.'))}
                    </p>
                </div>
            `;
        }

        return `
            <div class="nexus-page animate-fade">
                <div class="section-header" style="display: flex; justify-content: space-between; align-items: center;">
                    <div class="header-with-icon">
                        <i data-lucide="cloud-download" class="primary-icon"></i>
                        <div>
                            <h2>Nexus Mods</h2>
                            <p class="text-muted">Search and download mods directly from Nexus Mods.</p>
                        </div>
                    </div>
                </div>

                <div class="nexus-search-bar" style="margin-bottom: 20px; display: flex; gap: 10px;">
                    <div class="search-input-wrapper" style="flex: 1; position: relative;">
                        <i data-lucide="search" style="position: absolute; left: 12px; top: 50%; transform: translateY(-50%); color: #888;"></i>
                        <input type="text" id="nexus-search-input" placeholder="Search for mods..." value="${escapeAttr(this.currentQuery)}" 
                               style="width: 100%; padding: 10px 10px 10px 36px; border-radius: 6px; border: 1px solid #333; background: #1a1a1a; color: #fff;">
                    </div>
                    <button class="btn primary" id="nexus-search-btn">
                        <span>Search</span>
                    </button>
                </div>

                <div id="nexus-results-area" class="nexus-results-grid" style="display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 16px;">
                    ${this.renderResults()}
                </div>
            </div>
        `;
    }

    renderResults() {
        if (this.isSearching) {
            return `<div style="grid-column: 1/-1; text-align: center; padding: 40px; color: #888;">
                        <i data-lucide="loader-2" class="spin" style="width: 32px; height: 32px; margin-bottom: 10px;"></i>
                        <p>Searching Nexus Mods...</p>
                    </div>`;
        }

        if (!this.searchResults || this.searchResults.length === 0) {
            return `<div style="grid-column: 1/-1; text-align: center; padding: 40px; color: #555;">
                        <i data-lucide="search-x" style="width: 48px; height: 48px; margin-bottom: 10px; opacity: 0.5;"></i>
                        <p>No results found via API search. Try a different query.</p>
                        <p style="font-size: 0.8em; margin-top: 8px;">Note: API Search uses strict term matching.</p>
                    </div>`;
        }

        return this.searchResults.map(mod => {
            
            const name = mod.name || "Unknown Mod";
            const author = mod.author || "Unknown";
            const summary = mod.summary || "No description available.";
            const modId = mod.mod_id || mod.id;
            const category = mod.category || "General";
            const version = mod.version || "?";
            const pictureUrl = sanitizeCssUrl(mod.picture_url || "");
            const safeName = escapeHtml(name);
            const safeAuthor = escapeHtml(author);
            const safeSummary = escapeHtml(summary);
            const safeVersion = escapeHtml(version);
            const safeModId = escapeHtml(modId);
            const safeModIdAttr = escapeAttr(modId);
            const safeNameAttr = escapeAttr(name);

            return `
                <div class="mod-card" style="background: #222; border: 1px solid #333; border-radius: 8px; overflow: hidden; display: flex; flex-direction: column;">
                    ${pictureUrl ? `<div style="height: 140px; background: url('${pictureUrl}') center/cover;"></div>` : ''}
                    <div style="padding: 16px; flex: 1;">
                        <div style="display: flex; justify-content: space-between; align-items: start; margin-bottom: 8px;">
                            <h3 style="margin: 0; font-size: 1.1em; color: #fff;">${safeName}</h3>
                            <span class="badge" style="font-size: 0.7em; background: #333; padding: 2px 6px; border-radius: 4px;">v${safeVersion}</span>
                        </div>
                        <p style="font-size: 0.85em; color: #aaa; margin-bottom: 4px;">by ${safeAuthor}</p>
                        <p style="font-size: 0.9em; color: #ccc; line-height: 1.4; display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden;">${safeSummary}</p>
                    </div>
                    <div style="padding: 12px 16px; background: #1a1a1a; border-top: 1px solid #333; display: flex; justify-content: space-between; align-items: center;">
                        <span style="font-size: 0.8em; color: #666;">ID: ${safeModId}</span>
                        <button class="btn primary btn-sm download-mod-btn" data-id="${safeModIdAttr}" data-name="${safeNameAttr}">
                            <i data-lucide="download"></i> Download
                        </button>
                    </div>
                </div>
            `;
        }).join('');
    }

    onMount() {
        const input = document.getElementById('nexus-search-input');
        const btn = document.getElementById('nexus-search-btn');

        if (btn) {
            btn.addEventListener('click', () => {
                const query = input.value.trim();
                if (query) this.performSearch(query);
            });
        }

        const loginHeroBtn = document.getElementById('nexus-login-hero-btn');
        if (loginHeroBtn) {
            loginHeroBtn.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'NEXUS_LOGIN' });
            });
        }

        if (input) {
            input.addEventListener('keypress', (e) => {
                if (e.key === 'Enter') {
                    const query = input.value.trim();
                    if (query) this.performSearch(query);
                }
            });
        }

        const resultsArea = document.getElementById('nexus-results-area');
        if (resultsArea) {
            resultsArea.addEventListener('click', (e) => {
                const btn = e.target.closest('.download-mod-btn');
                if (btn) {
                    const modId = btn.getAttribute('data-id');
                    const name = btn.getAttribute('data-name');
                    this.initiateDownload(modId, name);
                }
            });
        }

    }

    performSearch(query) {
        this.isSearching = true;
        this.currentQuery = query;
        this.reRender();
        
        window.chrome.webview.postMessage({
            type: 'NEXUS_SEARCH',
            query: query
        });
    }

    handleSearchResult(data) {
        this.isSearching = false;
        try {
            const parsed = JSON.parse(data);
            if (Array.isArray(parsed)) {
                this.searchResults = parsed;
            } else if (parsed.results && Array.isArray(parsed.results)) {
                this.searchResults = parsed.results;
            } else {
                console.warn("Nexus Search: Unexpected format", parsed);
                this.searchResults = [];
            }
        } catch (e) {
            console.error("Failed to parse Nexus search results", e);
            this.searchResults = [];
        }
        this.reRender();
    }

    handleFilesResult(modId, data) {
        try {
            const parsed = JSON.parse(data);
            const files = parsed.files || parsed;
            
            if (files && Array.isArray(files) && files.length > 0) {
                
                files.sort((a, b) => b.uploaded_timestamp - a.uploaded_timestamp);
                
                const mainFile = files.find(f => f.category_id === 1) || files[0];
                
                const fileId = mainFile.file_id || mainFile.id;
                const fileName = mainFile.file_name || mainFile.name || "mod_file";
                const fileVersion = mainFile.version || '';
                const fileUploaded = mainFile.uploaded_timestamp ?? null;
                const meta = this._pendingDownloadMeta[modId] || {};
                delete this._pendingDownloadMeta[modId];
                
                console.log(`[NEXUS] Selected file for download: ${fileName} (ID: ${fileId})`);
                window.app.showBanner({ type: 'info', text: `Starting download: ${fileName}...` });
                
                window.chrome.webview.postMessage({
                    type: 'NEXUS_DOWNLOAD',
                    modId: parseInt(modId),
                    fileId: parseInt(fileId),
                    fileName: fileName,
                    fileVersion: fileVersion,
                    fileUploaded: fileUploaded,
                    modName: meta.modName || '',
                    author: meta.author || '',
                    details: meta.details || '',
                    category: meta.category || ''
                });
            } else {
                window.app.showBanner({ type: 'error', text: 'No downloadable files found for this mod.' });
            }
        } catch (e) {
            console.error("Failed to parse files list", e);
            window.app.showBanner({ type: 'error', text: 'Failed to retrieve file list.' });
        }
    }

    initiateDownload(modId, modName) {
        const mod = (this.searchResults || []).find(m => String(m.mod_id || m.id) === String(modId));
        this._pendingDownloadMeta[modId] = {
            modName: (mod && mod.name) ? mod.name : (modName || ''),
            author: (mod && mod.author) ? mod.author : '',
            details: (mod && mod.summary) ? mod.summary : '',
            category: (mod && mod.category) ? mod.category : 'General'
        };

        window.app.showBanner({ type: 'info', text: `Fetching file list for ${modName}...` });
        window.chrome.webview.postMessage({
            type: 'NEXUS_GET_FILES',
            modId: parseInt(modId)
        });
    }

    reRender() {
        const content = document.getElementById('nexus-results-area');
        if (content) {
            content.innerHTML = this.renderResults();
            lucide.createIcons();
        }
    }
}
