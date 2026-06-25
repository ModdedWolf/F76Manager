export class EndorsementModal {
    constructor(app) {
        this.app = app;
        this.isOpen = false;
    }

    render() {
        if (!this.isOpen) return '';

        return `
            <div id="endorsement-popup-overlay" class="endorsement-modal-overlay">
                <div class="endorsement-modal-content">
                    <div class="endorsement-modal-header">
                        <div class="endorsement-modal-title">
                            <i data-lucide="heart"></i>
                            <h3>Liking The Tool?</h3>
                        </div>
                        <button class="close-status" id="endorse-cancel-top">&times;</button>
                    </div>
                    
                    <div class="endorsement-modal-body">
                        <p class="endorsement-modal-desc">
                            We hope you're enjoying the Fallout 76 Manager! 
                            If you find this tool helpful, please consider endorsing it on Nexus Mods. 
                            It helps more people find the tool and supports development.
                        </p>
                    </div>

                    <div class="endorsement-modal-footer">
                        <button class="btn-popup secondary" id="endorse-cancel">Maybe Later</button>
                        <button class="btn-popup primary" id="endorse-confirm">
                            <i data-lucide="external-link"></i> Endorse on Nexus
                        </button>
                    </div>
                </div>
            </div>
        `;
    }

    show() {
        this.isOpen = true;
        this.injectAndMount();
    }

    hide() {
        this.isOpen = false;
        const overlay = document.getElementById('endorsement-popup-overlay');
        if (overlay) {
            overlay.style.opacity = '0';
            overlay.style.transition = 'opacity 0.2s ease-out';
            setTimeout(() => overlay.remove(), 200);
        }
    }

    injectAndMount() {
        const existing = document.getElementById('endorsement-popup-overlay');
        if (existing) existing.remove();

        const container = document.createElement('div');
        container.innerHTML = this.render();
        const overlayElement = container.firstElementChild;
        document.body.appendChild(overlayElement);

        if (window.lucide) lucide.createIcons();

        const confirmBtn = document.getElementById('endorse-confirm');
        const cancelBtn = document.getElementById('endorse-cancel');
        const closeTopBtn = document.getElementById('endorse-cancel-top');

        const closeAction = () => {
            this.hide();
            window.chrome.webview.postMessage({ type: 'ENDORSEMENT_DISMISSED' });
        };

        if (confirmBtn) {
            confirmBtn.onclick = () => {
                const url = 'https://www.nexusmods.com/fallout76/mods/3674?tab=files';
                window.chrome.webview.postMessage({ type: 'OPEN_IN_BROWSER', url: url });
                this.hide();
                window.chrome.webview.postMessage({ type: 'ENDORSEMENT_CONFIRMED' });
            };
        }

        if (cancelBtn) cancelBtn.onclick = closeAction;
        if (closeTopBtn) closeTopBtn.onclick = closeAction;

        overlayElement.onclick = (e) => {
            if (e.target === overlayElement) closeAction();
        };
    }
}
