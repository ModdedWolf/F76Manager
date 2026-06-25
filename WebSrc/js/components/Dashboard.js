import { QuickStats } from './QuickStats.js';
import { ConfigHealth } from './ConfigHealth.js';
import { escapeAttr, escapeHtml } from '../utils/htmlSafe.js';

const GITHUB_MARK_PATH = 'M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61C4.422 18.07 3.633 17.7 3.633 17.7c-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12';

function renderGitHubMark(size) {
    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="${size}" height="${size}" fill="currentColor" aria-hidden="true"><path d="${GITHUB_MARK_PATH}"/></svg>`;
}

export class Dashboard {
    constructor() {
        this.quickStats = new QuickStats();
        this.configHealth = new ConfigHealth();
    }

    render(data) {
        return `
            <div class="dashboard-page">
                <div class="hero-banner">
                    <img src="assets/fallout_hero_banner.png" alt="Hero Banner" class="hero-img">
                    <div class="hero-overlay">
                        <h1>${window._t('welcome_back_hero', 'Overseer')}</h1>
                        <p>${window._t('hero_desc')}</p>
                    </div>
                </div>

                ${this.renderNexusBanner(data)}

                <div class="quick-actions-grid">
                    <button class="action-card primary" id="btn-play">
                        <i data-lucide="play"></i>
                        <div class="action-info">
                            <span class="action-title">${window._t('play_fallout_76')}</span>
                            <span class="action-desc">${window._t('launch_game_with_mods')}</span>
                        </div>
                    </button>
                    <button class="action-card" id="btn-apply">
                        <i data-lucide="check-circle"></i>
                        <div class="action-info">
                            <span class="action-title">${window._t('apply_changes')}</span>
                            <span class="action-desc">${window._t('save_sync_ini')}</span>
                        </div>
                    </button>
                    <button class="action-card" id="btn-deploy">
                        <i data-lucide="refresh-cw"></i>
                        <div class="action-info">
                            <span class="action-title">${window._t('deploy_mods')}</span>
                            <span class="action-desc">${window._t('update_load_order')}</span>
                        </div>
                    </button>
                    <button class="action-card" id="btn-test">
                        <i data-lucide="flask-conical"></i>
                        <div class="action-info">
                            <span class="action-title">${window._t('test_config')}</span>
                            <span class="action-desc">${window._t('run_integrity_check')}</span>
                        </div>
                    </button>
                    ${this.renderCustomButtons()}
                </div>

                <div class="dashboard-widgets">
                    ${this.configHealth.render(data)}

                    ${this.quickStats.render(data)}
                </div>
            </div>
        `;
    }

    renderNexusBanner(data) {
        const isDismissed = localStorage.getItem('nexusBannerDismissed') === 'true';
        const ms = data && data.managerSettings ? data.managerSettings : {};
        const isLoggedIn = !!ms.nexusLoggedIn;
        
        if (isDismissed || isLoggedIn) {
            return '';
        }

        return `
            <div class="nexus-tip-banner" id="nexus-tip-banner">
                <div class="nexus-tip-content">
                    <i data-lucide="link" class="nexus-tip-icon"></i>
                    <div class="nexus-tip-text">
                        <strong>${window._t('nexus_tip_title', 'Tip: One-Click Downloads!')}</strong>
                        <span>${window._t('nexus_tip_desc', 'Log in to Nexus Mods to enable "Download with Manager" directly from the website.')}</span>
                    </div>
                </div>
                <div class="nexus-tip-actions">
                    <button class="btn-nexus-login" id="nexus-banner-login">
                        <i data-lucide="log-in"></i>
                        ${window._t('login_to_nexus', 'Sign in with Nexus Mods')}
                    </button>
                    <button class="btn-dismiss" id="nexus-banner-dismiss" title="${window._t('dismiss', 'Dismiss')}">
                        <i data-lucide="x"></i>
                    </button>
                </div>
            </div>
        `;
    }

    onMount() {
        const playBtn = document.getElementById('btn-play');
        const applyBtn = document.getElementById('btn-apply');
        const deployBtn = document.getElementById('btn-deploy');
        const testBtn = document.getElementById('btn-test');

        if (playBtn) {
            playBtn.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'LAUNCH_GAME' });
            });
        }

        if (applyBtn) {
            applyBtn.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'APPLY_CHANGES' });
            });
        }

        if (deployBtn) {
            deployBtn.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'DEPLOY_ALL' });
            });
        }

        if (testBtn) {
            testBtn.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'TEST_CONFIG' });
            });
        }

        const conflictBox = document.getElementById('conflict-status-box');
        if (conflictBox) {
            conflictBox.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'CHECK_CONFLICTS' });
            });
        }

        this.mountNexusBannerHandlers();

        this.mountCustomButtonHandlers();

        const container = document.querySelector('.dashboard-page');
        if (container) {
            container.addEventListener('click', (e) => {
                const link = e.target.closest('.external-link');
                if (link) {
                    e.preventDefault();
                    const url = link.getAttribute('data-url') || link.href;
                    console.log(`[DASHBOARD] External link clicked: ${url}`);
                    window.chrome.webview.postMessage({ 
                        type: 'JS_LOG', 
                        message: `[DASHBOARD] External link clicked: ${url}` 
                    });
                    window.chrome.webview.postMessage({ 
                        type: 'OPEN_IN_BROWSER', 
                        url: url 
                    });
                }
            });
        }
    }

    updateValues(data) {
        this.syncNexusBanner(data);
        const widgetsContainer = document.querySelector('.dashboard-widgets');
        if (widgetsContainer) {
            widgetsContainer.classList.add('content-refreshing');
            const healthHTML = this.configHealth.render(data);

            const statsHTML = this.quickStats.render(data);

            widgetsContainer.innerHTML = healthHTML + statsHTML;
            requestAnimationFrame(() => {
                requestAnimationFrame(() => widgetsContainer.classList.remove('content-refreshing'));
            });

            const conflictBox = document.getElementById('conflict-status-box');
            if (conflictBox) {
                conflictBox.onclick = () => window.chrome.webview.postMessage({ type: 'CHECK_CONFLICTS' });
            }

            if (window.lucide) lucide.createIcons();
        }
    }

    getAvailableButtons() {
        return [
            {
                id: 'discord',
                title: window._t('join_discord'),
                desc: window._t('join_discord_desc'),
                icon: 'assets/Discord.gif',
                iconType: 'img',
                url: 'https://discord.gg/T7T7NbCQxC',
                cardClass: 'discord-card'
            },
            {
                id: 'github',
                title: window._t('join_github'),
                desc: window._t('join_github_desc'),
                iconType: 'github',
                url: 'https://github.com/ModdedWolf/F76Manager',
                cardClass: 'github-card'
            }
        ];
    }

    renderCustomButtonIcon(btn, size = 40) {
        if (btn.iconType === 'img') {
            return `<img src="${escapeAttr(btn.icon)}" alt="${escapeAttr(btn.title)}" style="width: ${size}px; height: ${size}px;">`;
        }
        if (btn.iconType === 'github') {
            return renderGitHubMark(size);
        }
        return `<i data-lucide="${escapeAttr(btn.icon)}"></i>`;
    }

    getHiddenButtons() {
        try {
            return JSON.parse(localStorage.getItem('dashboardHiddenButtons') || '[]');
        } catch {
            return [];
        }
    }

    setHiddenButtons(ids) {
        localStorage.setItem('dashboardHiddenButtons', JSON.stringify(ids));
    }

    getButtonOrder() {
        const allIds = this.getAvailableButtons().map(b => b.id);
        try {
            const stored = JSON.parse(localStorage.getItem('dashboardCustomButtonOrder') || '[]');
            const order = stored.filter(id => allIds.includes(id));
            allIds.forEach(id => {
                if (!order.includes(id)) order.push(id);
            });
            return order;
        } catch {
            return allIds;
        }
    }

    setButtonOrder(ids) {
        localStorage.setItem('dashboardCustomButtonOrder', JSON.stringify(ids));
    }

    hideCustomButton(buttonId) {
        const hidden = this.getHiddenButtons();
        if (!hidden.includes(buttonId)) {
            hidden.push(buttonId);
            this.setHiddenButtons(hidden);
        }
    }

    showCustomButton(buttonId) {
        this.setHiddenButtons(this.getHiddenButtons().filter(id => id !== buttonId));
        const hidden = this.getHiddenButtons();
        const order = this.getButtonOrder()
            .filter(id => !hidden.includes(id) && id !== buttonId);
        order.push(buttonId);
        this.setButtonOrder(order);
    }

    getVisibleButtons() {
        const hidden = this.getHiddenButtons();
        return this.getButtonOrder().filter(id => !hidden.includes(id));
    }

    setVisibleButtons(ids) {
        localStorage.setItem('dashboardCustomButtons', JSON.stringify(ids));
    }

    renderCustomButtons() {
        const visibleIds = this.getVisibleButtons();
        const allButtons = this.getAvailableButtons();
        const buttonsById = Object.fromEntries(allButtons.map(b => [b.id, b]));
        const visibleButtons = visibleIds.map(id => buttonsById[id]).filter(Boolean);
        const hiddenButtons = allButtons.filter(b => !visibleIds.includes(b.id));

        let html = '';

        visibleButtons.forEach(btn => {
            const safeId = escapeAttr(btn.id);
            const safeCardClass = escapeAttr(btn.cardClass);
            const safeUrl = escapeAttr(btn.url);
            const safeTitle = escapeHtml(btn.title);
            const safeDesc = escapeHtml(btn.desc);
            html += `
                <div class="action-card-wrapper" data-button-id="${safeId}">
                    <a class="action-card ${safeCardClass}" href="${safeUrl}" target="_blank" style="text-decoration: none; position: relative;">
                        ${this.renderCustomButtonIcon(btn)}
                        <div class="action-info">
                            <span class="action-title">${safeTitle}</span>
                            <span class="action-desc">${safeDesc}</span>
                        </div>
                    </a>
                    <button class="action-card-remove" data-remove-id="${safeId}" title="${escapeAttr(window._t('remove_button'))}">
                        <i data-lucide="x"></i>
                    </button>
                </div>`;
        });

        if (hiddenButtons.length > 0) {
            html += `
                <button class="action-card action-card-add" id="btn-add-custom">
                    <i data-lucide="plus"></i>
                    <div class="action-info">
                        <span class="action-title">${window._t('add_button')}</span>
                        <span class="action-desc">${window._t('add_button_desc')}</span>
                    </div>
                </button>`;
        }

        return html;
    }

    renderAddButtonPopup() {
        const visibleIds = this.getVisibleButtons();
        const hiddenButtons = this.getAvailableButtons().filter(b => !visibleIds.includes(b.id));

        if (hiddenButtons.length === 0) return '';

        let buttonsHtml = hiddenButtons.map(btn => `
            <div class="add-button-option" data-add-id="${escapeAttr(btn.id)}">
                ${this.renderCustomButtonIcon(btn, 20)}
                <div class="add-button-info">
                    <span class="add-button-title">${escapeHtml(btn.title)}</span>
                    <span class="add-button-desc">${escapeHtml(btn.desc)}</span>
                </div>
            </div>
        `).join('');

        return `
            <div class="add-button-popup-overlay" id="add-button-popup">
                <div class="add-button-popup">
                    <div class="add-button-popup-header">
                        <h3>${window._t('add_quick_action')}</h3>
                        <button class="add-button-popup-close" id="add-button-popup-close">
                            <i data-lucide="x"></i>
                        </button>
                    </div>
                    <div class="add-button-popup-content">
                        ${buttonsHtml}
                    </div>
                </div>
            </div>
        `;
    }

    mountCustomButtonHandlers() {
        const grid = document.querySelector('.quick-actions-grid');
        if (!grid || grid.dataset.customButtonsBound) return;
        grid.dataset.customButtonsBound = '1';

        grid.addEventListener('click', (e) => {
            const removeBtn = e.target.closest('.action-card-remove');
            if (removeBtn) {
                e.preventDefault();
                e.stopPropagation();
                const buttonId = removeBtn.getAttribute('data-remove-id');
                if (buttonId) {
                    this.hideCustomButton(buttonId);
                    this.refreshCustomButtons();
                }
                return;
            }

            if (e.target.closest('#btn-add-custom')) {
                e.preventDefault();
                this.showAddButtonPopup();
            }
        });
    }

    showAddButtonPopup() {
        const existing = document.getElementById('add-button-popup');
        if (existing) existing.remove();

        document.body.insertAdjacentHTML('beforeend', this.renderAddButtonPopup());
        if (window.lucide) lucide.createIcons();

        const closeBtn = document.getElementById('add-button-popup-close');
        const overlay = document.getElementById('add-button-popup');
        
        if (closeBtn) {
            closeBtn.addEventListener('click', () => overlay.remove());
        }
        if (overlay) {
            overlay.addEventListener('click', (e) => {
                if (e.target === overlay) overlay.remove();
            });
        }

        document.querySelectorAll('.add-button-option').forEach(opt => {
            opt.addEventListener('click', () => {
                const buttonId = opt.getAttribute('data-add-id');
                if (buttonId) this.showCustomButton(buttonId);
                overlay.remove();
                this.refreshCustomButtons();
            });
        });
    }

    refreshCustomButtons() {
        const grid = document.querySelector('.quick-actions-grid');
        if (!grid) return;

        grid.querySelectorAll('.action-card-wrapper, .action-card-add').forEach(el => el.remove());

        grid.insertAdjacentHTML('beforeend', this.renderCustomButtons());
        if (window.lucide) lucide.createIcons();
    }

    mountNexusBannerHandlers() {
        const nexusLoginBtn = document.getElementById('nexus-banner-login');
        if (nexusLoginBtn && !nexusLoginBtn.dataset.bound) {
            nexusLoginBtn.dataset.bound = '1';
            nexusLoginBtn.addEventListener('click', () => {
                window.chrome.webview.postMessage({ type: 'NEXUS_LOGIN' });
            });
        }

        const nexusDismissBtn = document.getElementById('nexus-banner-dismiss');
        if (nexusDismissBtn && !nexusDismissBtn.dataset.bound) {
            nexusDismissBtn.dataset.bound = '1';
            nexusDismissBtn.addEventListener('click', () => {
                localStorage.setItem('nexusBannerDismissed', 'true');
                const banner = document.getElementById('nexus-tip-banner');
                if (banner) {
                    banner.style.opacity = '0';
                    banner.style.transform = 'translateY(-10px)';
                    setTimeout(() => banner.remove(), 300);
                }
            });
        }
    }

    syncNexusBanner(data) {
        const banner = document.getElementById('nexus-tip-banner');
        const isDismissed = localStorage.getItem('nexusBannerDismissed') === 'true';
        const isLoggedIn = !!(data && data.managerSettings && data.managerSettings.nexusLoggedIn);

        if (isLoggedIn) {
            if (banner) {
                banner.style.opacity = '0';
                banner.style.transform = 'translateY(-10px)';
                setTimeout(() => banner.remove(), 300);
            }
            return;
        }

        if (!banner && !isDismissed) {
            const hero = document.querySelector('.hero-banner');
            if (hero) {
                hero.insertAdjacentHTML('afterend', this.renderNexusBanner(data));
                if (window.lucide) lucide.createIcons();
                this.mountNexusBannerHandlers();
            }
        }
    }
}
