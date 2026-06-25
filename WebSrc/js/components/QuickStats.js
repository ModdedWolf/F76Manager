export class QuickStats {
    render(data) {
        const stats = data ? (data.stats || { modsActive: 0, activeModsSize: '0 B' }) : { modsActive: 0, activeModsSize: '0 B' };

        return `
            <div class="widget stats-widget">
                <div class="stat-item">
                    <span class="stat-label">${window._t('stat_mods_active')}</span>
                    <span class="stat-value" id="stat-mods-active">${stats.modsActive}</span>
                </div>
                <div class="stat-item">
                    <span class="stat-label">${window._t('stat_active_mod_storage')}</span>
                    <span class="stat-value" id="stat-active-mod-storage">${stats.activeModsSize || '0 B'}</span>
                </div>
                <div class="stat-item">
                    <span class="stat-label">${window._t('stat_conflicts')}</span>
                    <span class="stat-value ${(data && data.conflictsCount > 0) ? 'text-error' : ''}" id="stat-conflicts">${(data && typeof data.conflictsCount !== 'undefined') ? data.conflictsCount : 0}</span>
                </div>
            </div>
        `;
    }
}
