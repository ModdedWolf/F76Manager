export class Profiles {
    render(data) {
        const profiles = data ? (data.profiles || ['Default Profile']) : ['Default Profile'];
        const active = data ? (data.activeProfile || 'Default Profile') : 'Default Profile';

        return `
            <div class="profiles-page animate-fade">
                <div class="section-header">
                    <div class="header-with-icon">
                        <i data-lucide="users" class="primary-icon"></i>
                        <div>
                            <h2>${window._t('configuration_profiles')}</h2>
                            <p class="text-muted">${window._t('profiles_desc')}</p>
                        </div>
                    </div>
                </div>

                <div class="profile-creator-bar">
                    <div class="creator-input-group">
                        <i data-lucide="plus-circle"></i>
                        <input type="text" id="new-profile-name" placeholder="${window._t('profile_name_placeholder')}">
                        <button class="btn primary" id="btn-create-profile">
                            <i data-lucide="save"></i>
                            <span>${window._t('create_from_current')}</span>
                        </button>
                    </div>
                </div>

                <div class="profiles-grid">
                    ${profiles.map(name => `
                        <div class="profile-card ${name === active ? 'active' : ''}">
                            <div class="profile-icon">
                                <i data-lucide="user"></i>
                            </div>
                            <div class="profile-info">
                                <div class="profile-name">${name}</div>
                                <div class="profile-status">
                                    ${name === active ?
                `<span class="active-tag"><span class="dot"></span> ${window._t('tag_active')}</span>` :
                `<span class="inactive-tag">${window._t('tag_inactive')}</span>`
            }
                                </div>
                            </div>
                            <div class="profile-actions">
                                ${name !== active ? `
                                    <button class="btn-sm switch-profile" data-name="${name}">
                                        <i data-lucide="refresh-cw"></i>
                                        <span>${window._t('switch')}</span>
                                    </button>
                                ` : ''}
                                ${name !== 'Default Profile' ? `
                                    <button class="icon-btn-sm delete-profile" data-name="${name}" title="${window._t('delete')}">
                                        <i data-lucide="trash-2"></i>
                                    </button>
                                ` : ''}
                            </div>
                        </div>
                    `).join('')}
                </div>

                <div class="profiles-tip">
                    <i data-lucide="info"></i>
                    <p>${window._t('profiles_tip')}</p>
                </div>
            </div>
        `;
    }

    updateValues() {
    }

    onMount() {
        const createBtn = document.getElementById('btn-create-profile');
        if (createBtn) {
            createBtn.addEventListener('click', () => {
                const name = document.getElementById('new-profile-name').value;
                if (!name) return;
                window.chrome.webview.postMessage({ type: 'CREATE_PROFILE', name: name });
            });
        }

        document.querySelectorAll('.switch-profile').forEach(btn => {
            btn.addEventListener('click', () => {
                const name = btn.getAttribute('data-name');
                window.chrome.webview.postMessage({ type: 'SWITCH_PROFILE', name: name });
            });
        });

        document.querySelectorAll('.delete-profile').forEach(btn => {
            btn.addEventListener('click', async () => {
                const name = btn.getAttribute('data-name');
                const ok = await window.appConfirm({
                    title: 'Confirmation',
                    message: window._t('delete_profile_confirm', name),
                    okText: window._t('delete'),
                    cancelText: window._t('cancel'),
                    danger: true
                });
                if (ok) {
                    window.chrome.webview.postMessage({ type: 'DELETE_PROFILE', name: name });
                }
            });
        });
    }
}
